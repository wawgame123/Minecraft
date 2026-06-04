using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class FileSyncService
{
    public const string StatusCurrent = "Актуален";
    public const string StatusMissing = "Отсутствует";
    public const string StatusWrongSize = "Неверный размер";
    public const string StatusCorrupt = "Поврежден";
    private const int MaxParallelChecks = 8;
    private const int MaxParallelDownloads = 16;
    private const string EmotesArchiveFileName = "emotes.rar";
    private const string EmotesArchiveUrl = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/server-pack/neoforge-21.1.228/emotes.rar";
    private static readonly Regex TomlModIdRegex = new(@"(?m)^\s*modId\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex TomlVersionRegex = new(@"(?m)^\s*version\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex VersionTokenRegex = new(@"^(?:v?\d|\d+\.\d|mc\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyList<FileStatusItem>> VerifyAndRepairAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        bool downloadMissingFiles,
        bool verifyHashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool includeEmotes = false,
        bool forceExtractArchives = false,
        bool includeRequiredFiles = true)
    {
        Directory.CreateDirectory(settings.InstallDirectory);

        var files = GetManagedFiles(manifest, settings.EnableShaders, includeEmotes, includeRequiredFiles).ToList();
        RemoveBlockedMods(settings.InstallDirectory, progress);
        RemoveOutdatedManagedMods(settings.InstallDirectory, files, progress);

        var statuses = new FileStatusItem[files.Count];
        var repairQueue = new ConcurrentBag<(int Index, ManifestFile File, string FullPath)>();
        var checkedCount = 0;

        ReportPercent(progress, "Проверка", 0, files.Count);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelChecks,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var file = files[index];
                var fullPath = ResolveInsideInstallDirectory(settings.InstallDirectory, file.Path);
                var status = await CheckFileAsync(fullPath, file, verifyHashes, token);
                statuses[index] = new FileStatusItem
                {
                    Path = file.Path,
                    Category = file.Category,
                    Required = file.Required,
                    Size = file.Size,
                    Status = status
                };

                if (status != StatusCurrent)
                {
                    repairQueue.Add((index, file, fullPath));
                }

                var done = Interlocked.Increment(ref checkedCount);
                ReportPercent(progress, "Проверка", done, files.Count);
            });

        if (downloadMissingFiles && repairQueue.Count > 0)
        {
            var completed = 0;
            var repairCount = repairQueue.Count;
            ReportPercent(progress, "Скачивание", completed, repairCount);
            await Parallel.ForEachAsync(
                repairQueue,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDownloads,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    if (settings.ShowDownloadDetails)
                    {
                        progress?.Report("Скачивание: " + item.File.Path);
                    }

                    await DownloadFileAsync(item.File, item.FullPath, token);
                    var status = await CheckFileAsync(item.FullPath, item.File, verifyHashes: true, token);
                    statuses[item.Index].Status = status;

                    var done = Interlocked.Increment(ref completed);
                    ReportPercent(progress, "Скачивание", done, repairCount);
                });
        }

        await ExtractManagedArchivesAsync(files, settings.InstallDirectory, progress, cancellationToken, forceExtractArchives);

        progress?.Report("Проверка завершена");
        return statuses;
    }

    public static IEnumerable<ManifestFile> GetManagedFiles(
        LauncherManifest manifest,
        bool includeShaders,
        bool includeEmotes = false,
        bool includeRequiredFiles = true)
    {
        if (includeRequiredFiles)
        {
            foreach (var file in manifest.RequiredFiles.Where(file => file.Required))
            {
                yield return file;
            }
        }

        if (includeShaders)
        {
            foreach (var file in manifest.OptionalShaders)
            {
                yield return file;
            }
        }

        if (!includeEmotes)
        {
            yield break;
        }

        foreach (var file in manifest.OptionalEmotes)
        {
            yield return file;
        }
    }

    private static async Task<string> CheckFileAsync(
        string fullPath,
        ManifestFile file,
        bool verifyHashes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return StatusMissing;
        }

        var info = new FileInfo(fullPath);
        if (file.Size > 0 && info.Length != file.Size)
        {
            return StatusWrongSize;
        }

        if (verifyHashes && HasRealHash(file.Sha256))
        {
            var actualHash = await ComputeSha256Async(fullPath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCorrupt;
            }
        }

        return StatusCurrent;
    }

    private async Task DownloadFileAsync(
        ManifestFile file,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            throw new InvalidOperationException($"Для файла {file.Path} не указан url.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".download";

        try
        {
            await using (var source = await _httpClient.GetStreamAsync(file.Url, cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw new InvalidOperationException($"Не удалось скачать файл {file.Path}: {file.Url}. {ex.Message}", ex);
        }

        if (HasRealHash(file.Sha256))
        {
            var actualHash = await ComputeSha256Async(tempPath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException($"SHA-256 не совпал после загрузки: {file.Path}");
            }
        }

        File.Move(tempPath, destinationPath, true);
    }

    private static async Task ExtractManagedArchivesAsync(
        IReadOnlyList<ManifestFile> files,
        string installDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool forceExtractArchives)
    {
        var archives = files.Where(IsExtractableArchive).ToList();
        if (archives.Count == 0)
        {
            return;
        }

        var completed = 0;
        ReportPercent(progress, "Распаковка", completed, archives.Count);
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractArchiveIfNeeded(archive, installDirectory, forceExtractArchives);
            completed++;
            ReportPercent(progress, "Распаковка", completed, archives.Count);
            await Task.Yield();
        }
    }

    private static void ExtractArchiveIfNeeded(ManifestFile file, string installDirectory, bool force)
    {
        var archivePath = ResolveInsideInstallDirectory(installDirectory, file.Path);
        if (!File.Exists(archivePath))
        {
            return;
        }

        var targetDirectory = ResolveInsideInstallDirectory(installDirectory, file.ExtractTo!);
        var markerPath = ResolveInsideInstallDirectory(
            installDirectory,
            Path.Combine(".minivibe-state", "extracted", SafeMarkerName(file.Path) + ".sha256"));
        var expectedMarker = file.Sha256 ?? "";

        if (Directory.Exists(targetDirectory)
            && File.Exists(markerPath)
            && !force
            && string.Equals(File.ReadAllText(markerPath).Trim(), expectedMarker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "MinivibeExtract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, tempDirectory, overwriteFiles: true);
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(tempDirectory, targetDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, expectedMarker);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static bool IsExtractableArchive(ManifestFile file)
    {
        return !string.IsNullOrWhiteSpace(file.ExtractTo)
            && file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeMarkerName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    public async Task InstallEmotesArchiveAsync(
        LauncherSettings settings,
        bool reinstall,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.InstallDirectory);
        var archivePath = ResolveInsideInstallDirectory(settings.InstallDirectory, EmotesArchiveFileName);
        var targetDirectory = ResolveInsideInstallDirectory(settings.InstallDirectory, "emotes");

        if (reinstall)
        {
            DeleteInsideInstallDirectory(settings.InstallDirectory, "emotes", recursive: true);
            DeleteInsideInstallDirectory(settings.InstallDirectory, EmotesArchiveFileName, recursive: false);
        }

        if (!File.Exists(archivePath))
        {
            progress?.Report(settings.ShowDownloadDetails
                ? "Скачивание: " + EmotesArchiveFileName
                : "Скачивание эмоций 0%");
            await DownloadFileWithoutHashAsync(EmotesArchiveUrl, archivePath, cancellationToken);
            progress?.Report("Скачивание эмоций 100%");
        }

        progress?.Report("Распаковка эмоций 0%");
        ExtractArchiveWithoutContentCheck(archivePath, targetDirectory);
        progress?.Report("Распаковка эмоций 100%");
    }

    private async Task DownloadFileWithoutHashAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".download";
        try
        {
            await using (var source = await _httpClient.GetStreamAsync(url, cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static void ExtractArchiveWithoutContentCheck(string archivePath, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            var relativePath = NormalizeArchiveEntryPath(entry.Key);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, relativePath));
            var root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.WriteToFile(destination, new ExtractionOptions { Overwrite = true });
        }
    }

    private static string NormalizeArchiveEntryPath(string? entryKey)
    {
        var relativePath = (entryKey ?? "").Replace('\\', '/').Trim('/');
        if (relativePath.StartsWith("emotes/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath["emotes/".Length..];
        }

        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    public void ResetOptionalEmotes(LauncherManifest manifest, LauncherSettings settings)
    {
        foreach (var file in manifest.OptionalEmotes.Where(IsExtractableArchive))
        {
            DeleteInsideInstallDirectory(settings.InstallDirectory, file.Path, recursive: false);
            DeleteInsideInstallDirectory(settings.InstallDirectory, file.ExtractTo!, recursive: true);
            DeleteInsideInstallDirectory(
                settings.InstallDirectory,
                Path.Combine(".minivibe-state", "extracted", SafeMarkerName(file.Path) + ".sha256"),
                recursive: false);
        }
    }

    private static void DeleteInsideInstallDirectory(string installDirectory, string relativePath, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var fullPath = ResolveInsideInstallDirectory(installDirectory, relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return;
        }

        if (recursive && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string ResolveInsideInstallDirectory(string installDirectory, string relativePath)
    {
        var root = Path.GetFullPath(installDirectory);
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));

        if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Недопустимый путь в manifest.json: {relativePath}");
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasRealHash(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash)
            && !hash.Equals("HASH_HERE", StringComparison.OrdinalIgnoreCase)
            && hash.Length >= 32;
    }

    private static void RemoveBlockedMods(string installDirectory, IProgress<string>? progress)
    {
        var modsDirectory = Path.Combine(installDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return;
        }

        foreach (var modPath in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            if (!IsBlockedModJar(modPath))
            {
                continue;
            }

            try
            {
                File.Delete(modPath);
                progress?.Report("Удален несовместимый мод More Culling");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Не удалось удалить несовместимый мод More Culling: {modPath}. {ex.Message}", ex);
            }
        }
    }

    private static void RemoveOutdatedManagedMods(
        string installDirectory,
        IReadOnlyCollection<ManifestFile> managedFiles,
        IProgress<string>? progress)
    {
        var modsDirectory = Path.Combine(installDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return;
        }

        var expectedMods = BuildExpectedModMap(installDirectory, managedFiles);
        if (expectedMods.Count == 0)
        {
            return;
        }

        foreach (var modPath in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var actual = ReadModJarIdentity(modPath);
            if (actual is null || !expectedMods.TryGetValue(actual.Id, out var expectedMatches))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(installDirectory, modPath).Replace('\\', '/');
            var expected = expectedMatches
                .FirstOrDefault(match => string.Equals(relativePath, match.ManifestPath, StringComparison.OrdinalIgnoreCase));
            var sameManifestPath = expected is not null;
            var versionMismatch = expected is not null
                && !string.IsNullOrWhiteSpace(expected.Version)
                && !string.IsNullOrWhiteSpace(actual.Version)
                && !string.Equals(actual.Version, expected.Version, StringComparison.OrdinalIgnoreCase);

            if (sameManifestPath && !versionMismatch)
            {
                continue;
            }

            try
            {
                File.Delete(modPath);
                progress?.Report($"Заменяю мод {actual.Id} на версию из manifest.json");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Не удалось удалить устаревший мод {actual.Id}: {modPath}. {ex.Message}", ex);
            }
        }
    }

    private static Dictionary<string, List<ExpectedMod>> BuildExpectedModMap(string installDirectory, IEnumerable<ManifestFile> managedFiles)
    {
        var expectedMods = new Dictionary<string, List<ExpectedMod>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in managedFiles.Where(IsManagedModJar))
        {
            var expectedPath = ResolveInsideInstallDirectory(installDirectory, file.Path);
            var identity = File.Exists(expectedPath)
                ? ReadModJarIdentity(expectedPath)
                : CreateFallbackIdentityFromFileName(expectedPath);

            if (identity is null)
            {
                continue;
            }

            var manifestPath = file.Path.Replace('\\', '/');
            if (!expectedMods.TryGetValue(identity.Id, out var expectedMatches))
            {
                expectedMatches = [];
                expectedMods[identity.Id] = expectedMatches;
            }

            if (!expectedMatches.Any(match => string.Equals(match.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase)))
            {
                expectedMatches.Add(new ExpectedMod(identity.Id, identity.Version, manifestPath));
            }
        }

        return expectedMods;
    }

    private static bool IsManagedModJar(ManifestFile file)
    {
        var path = file.Path.Replace('\\', '/');
        return path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static ModJarIdentity? ReadModJarIdentity(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (!IsModMetadataEntry(entry.FullName) || entry.Length <= 0 || entry.Length > 1024 * 1024)
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                var metadata = reader.ReadToEnd();
                var identity = entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? ReadJsonModIdentity(metadata)
                    : ReadTomlModIdentity(metadata);

                if (identity is not null)
                {
                    return identity;
                }
            }
        }
        catch
        {
            return CreateFallbackIdentityFromFileName(jarPath);
        }

        return CreateFallbackIdentityFromFileName(jarPath);
    }

    private static ModJarIdentity? ReadJsonModIdentity(string metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(metadata);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = GetJsonString(root, "id")
                ?? GetJsonString(root, "modid")
                ?? GetJsonString(root, "modId");
            var version = GetJsonString(root, "version");

            return CreateIdentity(id, version);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static ModJarIdentity? ReadTomlModIdentity(string metadata)
    {
        var id = TomlModIdRegex.Match(metadata).Groups["value"].Value;
        var version = TomlVersionRegex.Match(metadata).Groups["value"].Value;
        return CreateIdentity(id, version);
    }

    private static ModJarIdentity? CreateFallbackIdentityFromFileName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var id = InferModIdFromFileName(fileName);
        return CreateIdentity(id, version: "");
    }

    private static ModJarIdentity? CreateIdentity(string? id, string? version)
    {
        id = NormalizeModName(id ?? "");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ModJarIdentity(id, version ?? "");
    }

    private static string InferModIdFromFileName(string fileName)
    {
        var normalized = fileName.Trim();
        var packageCandidate = normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.Contains('-', StringComparison.Ordinal) || part.Contains('_', StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(packageCandidate))
        {
            normalized = packageCandidate;
        }

        var parts = normalized
            .Split(['-', '_', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var idParts = new List<string>();

        foreach (var part in parts)
        {
            if (IsVersionOrLoaderToken(part))
            {
                break;
            }

            idParts.Add(part);
        }

        return NormalizeModName(idParts.Count == 0 ? normalized : string.Join("", idParts));
    }

    private static bool IsVersionOrLoaderToken(string token)
    {
        return token.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
            || token.Equals("forge", StringComparison.OrdinalIgnoreCase)
            || token.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            || token.Equals("quilt", StringComparison.OrdinalIgnoreCase)
            || token.Equals("common", StringComparison.OrdinalIgnoreCase)
            || token.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
            || VersionTokenRegex.IsMatch(token);
    }

    private static bool IsBlockedModJar(string modPath)
    {
        var normalizedName = NormalizeModName(Path.GetFileNameWithoutExtension(modPath));
        if (normalizedName.Contains("moreculling", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var archive = ZipFile.OpenRead(modPath);
            foreach (var entry in archive.Entries)
            {
                var normalizedEntryName = NormalizeModName(entry.FullName);
                if (normalizedEntryName.Contains("moreculling", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!IsModMetadataEntry(entry.FullName) || entry.Length <= 0 || entry.Length > 512 * 1024)
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                var metadata = NormalizeModName(reader.ReadToEnd());
                if (metadata.Contains("moreculling", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsModMetadataEntry(string entryName)
    {
        return string.Equals(entryName, "fabric.mod.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryName, "quilt.mod.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryName, "mcmod.info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryName, "META-INF/mods.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryName, "META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModName(string value)
    {
        return value
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static void ReportPercent(IProgress<string>? progress, string label, int completed, int total)
    {
        if (progress is null)
        {
            return;
        }

        var percent = total <= 0 ? 100 : (int)Math.Round(completed * 100d / total);
        progress.Report($"{label} {Math.Clamp(percent, 0, 100)}%");
    }
}

internal sealed record ModJarIdentity(
    string Id,
    string Version);

internal sealed record ExpectedMod(
    string Id,
    string Version,
    string ManifestPath);
