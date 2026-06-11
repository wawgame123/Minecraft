using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class MinecraftRuntimeService
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetBaseUrl = "https://resources.download.minecraft.net";
    private const string NeoForgeMavenBaseUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string NeoForgeMavenRootUrl = "https://maven.neoforged.net/releases";
    private const string MinecraftLibrariesUrl = "https://libraries.minecraft.net";
    private const int MaxParallelLibraryDownloads = 12;
    private const int MaxParallelAssetDownloads = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient = new();

    public async Task<MinecraftRuntime> EnsureAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        string javaPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var minecraftRoot = ResolveMinecraftRoot(settings.InstallDirectory);
        var runtimeRoot = Path.Combine(settings.InstallDirectory, "minecraft-runtime");
        var librariesRoot = Path.Combine(minecraftRoot, "libraries");
        var versionsRoot = Path.Combine(minecraftRoot, "versions", manifest.MinecraftVersion);
        var assetsRoot = Path.Combine(minecraftRoot, "assets");
        var nativesRoot = Path.Combine(settings.InstallDirectory, "natives", manifest.MinecraftVersion);

        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(versionsRoot);
        Directory.CreateDirectory(assetsRoot);
        Directory.CreateDirectory(nativesRoot);

        progress?.Report("Библиотеки Minecraft 0%");
        var versionJson = await LoadVersionJsonAsync(manifest.MinecraftVersion, versionsRoot, progress, cancellationToken);
        var loaderInstall = await EnsureLoaderInstallAsync(manifest, settings, javaPath, runtimeRoot, minecraftRoot, progress, cancellationToken);
        var loaderJson = loaderInstall?.VersionJson;

        var clientJarPath = Path.Combine(versionsRoot, manifest.MinecraftVersion + ".jar");
        if (versionJson.Downloads.Client is not null)
        {
            ReportDownloadDetail(settings, progress, "Minecraft client", clientJarPath);
            await EnsureDownloadAsync(versionJson.Downloads.Client, clientJarPath, progress, cancellationToken);
        }

        var workItems = new List<RuntimeDownloadItem>();
        AddLibraryDownloads(versionJson.Libraries, librariesRoot, workItems);
        if (loaderJson is not null)
        {
            AddLibraryDownloads(loaderJson.Libraries, librariesRoot, workItems);
        }

        await EnsureLibraryDownloadsAsync(workItems, progress, cancellationToken, settings.ShowDownloadDetails);

        foreach (var nativeJarPath in workItems
            .Where(item => item.IsNative)
            .Select(item => item.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ExtractNatives(nativeJarPath, nativesRoot);
        }

        progress?.Report("Библиотеки Minecraft 100%");
        var assetIndexId = versionJson.AssetIndex.Id;
        await EnsureAssetsAsync(versionJson.AssetIndex, assetsRoot, progress, cancellationToken, settings.ShowDownloadDetails);

        var classpath = workItems
            .Where(item => item.AddToClasspath)
            .Select(item => item.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var launchClientJarPath = !string.IsNullOrWhiteSpace(loaderInstall?.VersionJarPath)
            ? loaderInstall.VersionJarPath
            : clientJarPath;

        return new MinecraftRuntime(
            loaderJson?.Id ?? manifest.MinecraftVersion,
            launchClientJarPath,
            classpath,
            nativesRoot,
            assetsRoot,
            assetIndexId,
            librariesRoot,
            loaderJson?.MainClass ?? "",
            ExtractStringArguments(loaderJson?.Arguments.Jvm),
            ExtractStringArguments(loaderJson?.Arguments.Game),
            loaderInstall?.ArtifactPaths ?? []);
    }

    private async Task<MinecraftVersionJson> LoadVersionJsonAsync(
        string minecraftVersion,
        string versionDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var versionJsonPath = Path.Combine(versionDirectory, minecraftVersion + ".json");
        MinecraftVersionJson? localVersion = null;

        if (File.Exists(versionJsonPath))
        {
            await using var localStream = File.OpenRead(versionJsonPath);
            localVersion = await JsonSerializer.DeserializeAsync<MinecraftVersionJson>(localStream, JsonOptions, cancellationToken);
        }

        if (localVersion is not null && localVersion.Libraries.Count > 0)
        {
            return localVersion;
        }

        progress?.Report("Metadata Minecraft 0%");
        await using var manifestStream = await _httpClient.GetStreamAsync(VersionManifestUrl, cancellationToken);
        var versionManifest = await JsonSerializer.DeserializeAsync<MinecraftVersionManifest>(manifestStream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Не удалось прочитать version_manifest_v2.json Minecraft.");

        var versionInfo = versionManifest.Versions
            .FirstOrDefault(version => string.Equals(version.Id, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Minecraft {minecraftVersion} не найден в version_manifest_v2.json.");

        var bytes = await _httpClient.GetByteArrayAsync(versionInfo.Url, cancellationToken);
        await File.WriteAllBytesAsync(versionJsonPath, bytes, cancellationToken);

        await using var stream = File.OpenRead(versionJsonPath);
        return await JsonSerializer.DeserializeAsync<MinecraftVersionJson>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Не удалось прочитать metadata Minecraft {minecraftVersion}.");
    }

    private async Task<LoaderInstall?> EnsureLoaderInstallAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        string javaPath,
        string runtimeRoot,
        string minecraftRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(manifest.Loader, "neoforge", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.LoaderVersion))
        {
            return null;
        }

        var loaderRoot = Path.Combine(runtimeRoot, "loaders", "neoforge", manifest.LoaderVersion);
        Directory.CreateDirectory(loaderRoot);
        var versionJsonPath = Path.Combine(loaderRoot, "version.json");
        var installerVersionJson = await LoadInstallerVersionJsonAsync(manifest, settings, loaderRoot, versionJsonPath, progress, cancellationToken);
        var loaderVersionId = string.IsNullOrWhiteSpace(installerVersionJson.Id)
            ? $"neoforge-{manifest.LoaderVersion}"
            : installerVersionJson.Id;
        var installedCandidate = await FindInstalledLoaderVersionAsync(minecraftRoot, settings.InstallDirectory, loaderVersionId, manifest.LoaderVersion, cancellationToken);
        var installedVersionRoot = installedCandidate?.VersionDirectory ?? Path.Combine(minecraftRoot, "versions", loaderVersionId);
        var installedVersionJsonPath = installedCandidate?.VersionJsonPath ?? Path.Combine(installedVersionRoot, loaderVersionId + ".json");
        var installedVersion = installedCandidate?.VersionJson ?? await TryReadInstalledLoaderVersionAsync(installedVersionJsonPath, cancellationToken);
        var installedVersionJarPath = installedCandidate?.VersionJarPath ?? ResolveInstalledVersionJar(installedVersionRoot, installedVersionJsonPath, installedVersion, manifest.LoaderVersion);
        var neoforgeArtifactPaths = FindNeoForgeRuntimeJars(minecraftRoot, settings.InstallDirectory, manifest.LoaderVersion);
        var hasNeoForgeArtifactJar = neoforgeArtifactPaths.Count > 0;
        var neoforgeClientJarPath = Path.Combine(
            minecraftRoot,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            manifest.LoaderVersion,
            $"neoforge-{manifest.LoaderVersion}-universal.jar");

        if (installedVersion is null || !hasNeoForgeArtifactJar)
        {
            await RunNeoForgeInstallerAsync(javaPath, loaderRoot, manifest.LoaderVersion, minecraftRoot, settings, progress, cancellationToken);
            installedCandidate = await FindInstalledLoaderVersionAsync(minecraftRoot, settings.InstallDirectory, loaderVersionId, manifest.LoaderVersion, cancellationToken);
            installedVersionRoot = installedCandidate?.VersionDirectory ?? installedVersionRoot;
            installedVersionJsonPath = installedCandidate?.VersionJsonPath ?? installedVersionJsonPath;
            installedVersion = installedCandidate?.VersionJson ?? await TryReadInstalledLoaderVersionAsync(installedVersionJsonPath, cancellationToken);
            installedVersionJarPath = installedCandidate?.VersionJarPath ?? ResolveInstalledVersionJar(installedVersionRoot, installedVersionJsonPath, installedVersion, manifest.LoaderVersion);
            neoforgeArtifactPaths = FindNeoForgeRuntimeJars(minecraftRoot, settings.InstallDirectory, manifest.LoaderVersion);
            hasNeoForgeArtifactJar = neoforgeArtifactPaths.Count > 0;
        }

        if (!hasNeoForgeArtifactJar)
        {
            throw new InvalidOperationException($"NeoForge установлен не полностью: не найден {neoforgeClientJarPath}");
        }

        if (installedVersion is not null)
        {
            return new LoaderInstall(
                installedVersion,
                VersionJarPath: installedVersionJarPath,
                ArtifactPaths: neoforgeArtifactPaths);
        }

        return new LoaderInstall(
            installerVersionJson,
            VersionJarPath: installedVersionJarPath,
            ArtifactPaths: neoforgeArtifactPaths);
    }

    private async Task<MinecraftVersionJson> LoadInstallerVersionJsonAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        string loaderRoot,
        string versionJsonPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(versionJsonPath))
        {
            await using var localStream = File.OpenRead(versionJsonPath);
            var localVersion = await JsonSerializer.DeserializeAsync<MinecraftVersionJson>(localStream, JsonOptions, cancellationToken);
            if (localVersion is not null && localVersion.Libraries.Count > 0 && !string.IsNullOrWhiteSpace(localVersion.MainClass))
            {
                return localVersion;
            }
        }

        progress?.Report("Metadata NeoForge 0%");
        var installerPath = Path.Combine(loaderRoot, $"neoforge-{manifest.LoaderVersion}-installer.jar");
        var installerUrl = $"{NeoForgeMavenBaseUrl}/{manifest.LoaderVersion}/neoforge-{manifest.LoaderVersion}-installer.jar";
        ReportDownloadDetail(settings, progress, "NeoForge installer", installerPath);
        await DownloadFileAsync(installerUrl, installerPath, expectedSize: 0, expectedSha1: "", cancellationToken);

        using var archive = ZipFile.OpenRead(installerPath);
        var entry = archive.GetEntry("version.json")
            ?? throw new InvalidOperationException($"В NeoForge installer {manifest.LoaderVersion} не найден version.json.");

        await using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        await entryStream.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        await File.WriteAllBytesAsync(versionJsonPath, bytes, cancellationToken);

        return JsonSerializer.Deserialize<MinecraftVersionJson>(bytes, JsonOptions)
            ?? throw new InvalidOperationException($"Не удалось прочитать version.json NeoForge {manifest.LoaderVersion}.");
    }

    private static async Task<MinecraftVersionJson?> TryReadInstalledLoaderVersionAsync(
        string installedVersionJsonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(installedVersionJsonPath))
        {
            return null;
        }

        MinecraftVersionJson? installedVersion;
        try
        {
            await using var installedStream = File.OpenRead(installedVersionJsonPath);
            installedVersion = await JsonSerializer.DeserializeAsync<MinecraftVersionJson>(installedStream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (installedVersion is null
            || installedVersion.Libraries.Count == 0
            || string.IsNullOrWhiteSpace(installedVersion.MainClass))
        {
            return null;
        }

        return installedVersion;
    }

    private static async Task<InstalledLoaderCandidate?> FindInstalledLoaderVersionAsync(
        string minecraftRoot,
        string installDirectory,
        string preferredVersionId,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        foreach (var directory in LoaderVersionDirectories(minecraftRoot, installDirectory, preferredVersionId, loaderVersion))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var jsonPaths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var jsonPath in jsonPaths)
            {
                var version = await TryReadInstalledLoaderVersionAsync(jsonPath, cancellationToken);
                if (version is null || !LooksLikeNeoForgeVersion(version, loaderVersion))
                {
                    continue;
                }

                var jarPath = ResolveInstalledVersionJar(directory, jsonPath, version, loaderVersion);

                return new InstalledLoaderCandidate(version, directory, jsonPath, jarPath);
            }
        }

        return null;
    }

    private static string? ResolveInstalledVersionJar(
        string versionDirectory,
        string versionJsonPath,
        MinecraftVersionJson? version,
        string loaderVersion)
    {
        var exactJarPath = Path.ChangeExtension(versionJsonPath, ".jar");
        if (File.Exists(exactJarPath))
        {
            return exactJarPath;
        }

        if (!Directory.Exists(versionDirectory))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(version?.Id))
        {
            var versionIdJarPath = Path.Combine(versionDirectory, version.Id + ".jar");
            if (File.Exists(versionIdJarPath))
            {
                return versionIdJarPath;
            }
        }

        var folderName = new DirectoryInfo(versionDirectory).Name;
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            var folderNamedJarPath = Path.Combine(versionDirectory, folderName + ".jar");
            if (File.Exists(folderNamedJarPath))
            {
                return folderNamedJarPath;
            }
        }

        var candidates = Directory.EnumerateFiles(versionDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Contains("installer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var versionId = version?.Id ?? "";
        return candidates
            .OrderByDescending(path => !string.IsNullOrWhiteSpace(versionId)
                && Path.GetFileNameWithoutExtension(path).Contains(versionId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => Path.GetFileNameWithoutExtension(path).Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(LooksLikeMinecraftClientJar)
            .ThenByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
    }

    private static IEnumerable<string> LoaderVersionDirectories(
        string minecraftRoot,
        string installDirectory,
        string preferredVersionId,
        string loaderVersion)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { preferredVersionId, $"neoforge-{loaderVersion}", loaderVersion }.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            var path = Path.Combine(minecraftRoot, "versions", id);
            if (seen.Add(Path.GetFullPath(path)))
            {
                yield return path;
            }
        }

        var installFullPath = Path.GetFullPath(installDirectory);
        if (seen.Add(installFullPath))
        {
            yield return installFullPath;
        }
    }

    private static bool LooksLikeNeoForgeVersion(MinecraftVersionJson version, string loaderVersion)
    {
        return version.Id.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
            || version.Id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)
            || version.Libraries.Any(library => IsNeoForgeClientLibrary(library.Name, loaderVersion));
    }

    private static IReadOnlyList<string> FindNeoForgeRuntimeJars(string minecraftRoot, string installDirectory, string loaderVersion)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNeoForgeJar(string jarPath, bool allowNameMatch)
        {
            if (!File.Exists(jarPath) || !LooksLikeNeoForgeJar(jarPath, loaderVersion, allowNameMatch))
            {
                return;
            }

            var fullPath = Path.GetFullPath(jarPath);
            if (seen.Add(fullPath))
            {
                results.Add(fullPath);
            }
        }

        foreach (var artifactPath in StandardNeoForgeArtifactPaths(minecraftRoot, loaderVersion))
        {
            AddIfNeoForgeJar(artifactPath, allowNameMatch: true);
        }

        if (Directory.Exists(installDirectory))
        {
            foreach (var jarPath in Directory.EnumerateFiles(installDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            {
                AddIfNeoForgeJar(jarPath, allowNameMatch: false);
            }
        }

        foreach (var root in NeoForgeJarSearchRoots(minecraftRoot, installDirectory))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var jarPath in Directory.EnumerateFiles(root, "*.jar", SearchOption.AllDirectories))
            {
                AddIfNeoForgeJar(jarPath, allowNameMatch: true);
            }
        }

        return results;
    }

    private static IEnumerable<string> StandardNeoForgeArtifactPaths(string minecraftRoot, string loaderVersion)
    {
        var root = Path.Combine(
            minecraftRoot,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            loaderVersion);

        yield return Path.Combine(root, $"neoforge-{loaderVersion}-client.jar");
        yield return Path.Combine(root, $"neoforge-{loaderVersion}-universal.jar");
    }

    private static IEnumerable<string> NeoForgeJarSearchRoots(string minecraftRoot, string installDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in CandidateMinecraftRoots(minecraftRoot, installDirectory))
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, "libraries", "net", "neoforged", "neoforge"));
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> CandidateMinecraftRoots(string minecraftRoot, string installDirectory)
    {
        yield return minecraftRoot;

        var current = new DirectoryInfo(installDirectory);
        for (var depth = 0; current is not null && depth < 3; depth++)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static bool LooksLikeNeoForgeJar(string jarPath, string loaderVersion, bool allowNameMatch = true)
    {
        var fileName = Path.GetFileName(jarPath);
        if (allowNameMatch
            && fileName.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
            && fileName.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)
            && (fileName.Contains("client", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("universal", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            if (archive.GetEntry("META-INF/neoforge.mods.toml") is not null)
            {
                return true;
            }

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("net/neoforged/neoforge", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Contains("META-INF/neoforge", StringComparison.OrdinalIgnoreCase))
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

    private static bool LooksLikeMinecraftClientJar(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            return archive.GetEntry("net/minecraft/client/main/Main.class") is not null
                || archive.GetEntry("net/minecraft/client/Minecraft.class") is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunNeoForgeInstallerAsync(
        string javaPath,
        string loaderRoot,
        string loaderVersion,
        string installDirectory,
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var installerPath = Path.Combine(loaderRoot, $"neoforge-{loaderVersion}-installer.jar");
        if (!File.Exists(installerPath))
        {
            var installerUrl = $"{NeoForgeMavenBaseUrl}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
            ReportDownloadDetail(settings, progress, "NeoForge installer", installerPath);
            await DownloadFileAsync(installerUrl, installerPath, expectedSize: 0, expectedSha1: "", cancellationToken);
        }

        progress?.Report("Установка NeoForge 0%");
        Directory.CreateDirectory(installDirectory);
        EnsureLauncherProfilesFile(installDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"-jar {QuoteArgument(installerPath)} --installClient {QuoteArgument(installDirectory)}",
                WorkingDirectory = installDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException($"NeoForge installer завершился с кодом {process.ExitCode}. {Tail(details, 1600)}");
        }

        progress?.Report("Установка NeoForge 100%");
    }

    private static void EnsureLauncherProfilesFile(string installDirectory)
    {
        var profilesPath = Path.Combine(installDirectory, "launcher_profiles.json");
        if (File.Exists(profilesPath))
        {
            return;
        }

        File.WriteAllText(profilesPath, "{\"profiles\":{},\"version\":3}");
    }

    private static string ResolveMinecraftRoot(string installDirectory)
    {
        var install = new DirectoryInfo(installDirectory);
        if (install.Parent is not null
            && string.Equals(install.Parent.Name, "versions", StringComparison.OrdinalIgnoreCase)
            && install.Parent.Parent is not null)
        {
            return install.Parent.Parent.FullName;
        }

        if (Directory.Exists(Path.Combine(installDirectory, "versions"))
            || Directory.Exists(Path.Combine(installDirectory, "assets"))
            || Directory.Exists(Path.Combine(installDirectory, "libraries")))
        {
            return installDirectory;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultMinecraft = Path.Combine(appData, ".minecraft");
        var installFullPath = Path.GetFullPath(installDirectory);
        var defaultMinecraftFullPath = Path.GetFullPath(defaultMinecraft).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (installFullPath.StartsWith(defaultMinecraftFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return defaultMinecraft;
        }

        return installDirectory;
    }

    private static void ReportDownloadDetail(LauncherSettings settings, IProgress<string>? progress, string label, string path)
    {
        if (settings.ShowDownloadDetails)
        {
            progress?.Report($"{label}: {Path.GetFileName(path)}");
        }
    }

    private static void AddLibraryDownloads(
        IEnumerable<MinecraftLibrary> libraries,
        string librariesRoot,
        ICollection<RuntimeDownloadItem> workItems)
    {
        foreach (var library in libraries.Where(IsAllowedOnCurrentOs))
        {
            var artifact = library.Downloads.Artifact;

            if (artifact is null && TryCreateArtifactFromName(library.Name, out var generatedArtifact))
            {
                artifact = generatedArtifact;
            }

            if (artifact is not null)
            {
                var libraryPath = Path.Combine(librariesRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                workItems.Add(new RuntimeDownloadItem(artifact, libraryPath, AddToClasspath: true, IsNative: false));
            }

            var nativeDownload = NativeDownloadForCurrentOs(library);
            if (nativeDownload is not null)
            {
                var nativeJarPath = Path.Combine(librariesRoot, nativeDownload.Path.Replace('/', Path.DirectorySeparatorChar));
                workItems.Add(new RuntimeDownloadItem(nativeDownload, nativeJarPath, AddToClasspath: false, IsNative: true));
            }
        }
    }

    private static bool IsNeoForgeClientLibrary(string name, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            return false;
        }

        var parts = name.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && string.Equals(parts[0], "net.neoforged", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "neoforge", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], loaderVersion, StringComparison.OrdinalIgnoreCase)
            && (parts.Length == 3 || string.Equals(parts[3], "client", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCreateArtifactFromName(string name, out MinecraftDownload download)
    {
        download = new MinecraftDownload();

        var parts = name.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";

        var groupPath = group.Replace('.', '/');
        var fileName = $"{artifact}-{version}{classifier}.jar";
        var path = $"{groupPath}/{artifact}/{version}/{fileName}";

        var baseUrl = group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase)
            ? NeoForgeMavenRootUrl
            : MinecraftLibrariesUrl;

        download.Path = path;
        download.Url = $"{baseUrl}/{path}";
        download.Sha1 = "";
        download.Size = 0;

        return true;
    }

    private async Task EnsureLibraryDownloadsAsync(
        IReadOnlyCollection<RuntimeDownloadItem> workItems,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool showDownloadDetails)
    {
        var downloads = workItems
            .GroupBy(item => item.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var completed = 0;
        progress?.Report($"Библиотеки Minecraft {Percent(completed, downloads.Count)}%");
        await Parallel.ForEachAsync(
            downloads,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelLibraryDownloads,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                if (showDownloadDetails)
                {
                    progress?.Report("Скачивание библиотеки: " + Path.GetFileName(item.DestinationPath));
                }

                await EnsureDownloadAsync(item.Download, item.DestinationPath, progress: null, token);
                var done = Interlocked.Increment(ref completed);
                progress?.Report($"Библиотеки Minecraft {Percent(done, downloads.Count)}%");
            });
    }

    private async Task EnsureAssetsAsync(
        MinecraftAssetIndex assetIndex,
        string assetsRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool showDownloadDetails)
    {
        var indexesRoot = Path.Combine(assetsRoot, "indexes");
        Directory.CreateDirectory(indexesRoot);
        var indexPath = Path.Combine(indexesRoot, assetIndex.Id + ".json");

        await EnsureDownloadAsync(assetIndex, indexPath, progress, cancellationToken);

        await using var stream = File.OpenRead(indexPath);
        var index = await JsonSerializer.DeserializeAsync<MinecraftAssetsDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Не удалось прочитать asset index Minecraft.");

        var missingAssets = new List<(MinecraftAssetObject Asset, string Path, string Url)>();
        foreach (var asset in index.Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (asset.Hash.Length < 2)
            {
                continue;
            }

            var prefix = asset.Hash[..2];
            var assetPath = Path.Combine(assetsRoot, "objects", prefix, asset.Hash);
            if (File.Exists(assetPath) && new FileInfo(assetPath).Length == asset.Size)
            {
                continue;
            }

            var url = $"{AssetBaseUrl}/{prefix}/{asset.Hash}";
            missingAssets.Add((asset, assetPath, url));
        }

        var completed = 0;
        progress?.Report($"Ресурсы Minecraft {Percent(completed, missingAssets.Count)}%");
        await Parallel.ForEachAsync(
            missingAssets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelAssetDownloads,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                if (showDownloadDetails)
                {
                    progress?.Report($"Скачивание ресурса: {item.Asset.Hash}");
                }

                await DownloadFileAsync(item.Url, item.Path, item.Asset.Size, item.Asset.Hash, token);
                var done = Interlocked.Increment(ref completed);
                progress?.Report($"Ресурсы Minecraft {Percent(done, missingAssets.Count)}%");
            });

        if (missingAssets.Count == 0)
        {
            progress?.Report("Ресурсы Minecraft 100%");
        }
    }

    private async Task EnsureDownloadAsync(
        MinecraftDownload download,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)
            && (download.Size <= 0 || new FileInfo(destinationPath).Length == download.Size)
            && (string.IsNullOrWhiteSpace(download.Sha1) || await Sha1Async(destinationPath, cancellationToken) == download.Sha1))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(download.Url))
        {
            throw new InvalidOperationException("Для runtime-файла не указан url.");
        }

        progress?.Report("Скачивание runtime 0%");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await DownloadFileAsync(download.Url, destinationPath, download.Size, download.Sha1, cancellationToken);
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long expectedSize,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.download";

        try
        {
            await using (var source = await _httpClient.GetStreamAsync(url, cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            if (expectedSize > 0 && new FileInfo(tempPath).Length != expectedSize)
            {
                throw new InvalidOperationException("размер файла не совпал");
            }

            if (!string.IsNullOrWhiteSpace(expectedSha1)
                && !string.Equals(await Sha1Async(tempPath, cancellationToken), expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHA-1 файла не совпал");
            }

            File.Move(tempPath, destinationPath, true);
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

    private static void ExtractNatives(string nativeJarPath, string nativesRoot)
    {
        using var archive = ZipFile.OpenRead(nativeJarPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(nativesRoot, entry.Name));
            var root = Path.GetFullPath(nativesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.ExtractToFile(destination, true);
        }
    }

    private static bool IsAllowedOnCurrentOs(MinecraftLibrary library)
    {
        if (library.Rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        var currentOs = MinecraftOsName();
        foreach (var rule in library.Rules)
        {
            if (rule.Os is not null
                && !string.Equals(rule.Os.Name, currentOs, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static MinecraftDownload? NativeDownloadForCurrentOs(MinecraftLibrary library)
    {
        if (!library.Natives.TryGetValue(MinecraftOsName(), out var classifier))
        {
            return null;
        }

        classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.OrdinalIgnoreCase);
        return library.Downloads.Classifiers.TryGetValue(classifier, out var download) ? download : null;
    }

    private static string MinecraftOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        return "windows";
    }

    private static IReadOnlyList<string> ExtractStringArguments(IReadOnlyList<JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var argument in arguments)
        {
            if (argument.ValueKind == JsonValueKind.String)
            {
                var value = argument.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private static async Task<string> Sha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int Percent(int completed, int total)
    {
        return total <= 0 ? 100 : Math.Clamp((int)Math.Round(completed * 100d / total), 0, 100);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Tail(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[^maxLength..];
    }
}

internal sealed record LoaderInstall(
    MinecraftVersionJson VersionJson,
    string? VersionJarPath,
    IReadOnlyList<string> ArtifactPaths);

internal sealed record InstalledLoaderCandidate(
    MinecraftVersionJson VersionJson,
    string VersionDirectory,
    string VersionJsonPath,
    string? VersionJarPath);

internal sealed record RuntimeDownloadItem(
    MinecraftDownload Download,
    string DestinationPath,
    bool AddToClasspath,
    bool IsNative);

public sealed record MinecraftRuntime(
    string VersionId,
    string ClientJarPath,
    IReadOnlyList<string> ClasspathFiles,
    string NativesDirectory,
    string AssetsDirectory,
    string AssetIndex,
    string LibrariesDirectory,
    string MainClass,
    IReadOnlyList<string> JvmArgs,
    IReadOnlyList<string> GameArgs,
    IReadOnlyList<string> LoaderArtifactFiles);

internal sealed class MinecraftVersionManifest
{
    [JsonPropertyName("versions")]
    public List<MinecraftVersionReference> Versions { get; set; } = [];
}

internal sealed class MinecraftVersionReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

internal sealed class MinecraftVersionJson
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("arguments")]
    public MinecraftLaunchArguments Arguments { get; set; } = new();

    [JsonPropertyName("downloads")]
    public MinecraftVersionDownloads Downloads { get; set; } = new();

    [JsonPropertyName("assetIndex")]
    public MinecraftAssetIndex AssetIndex { get; set; } = new();

    [JsonPropertyName("libraries")]
    public List<MinecraftLibrary> Libraries { get; set; } = [];
}

internal sealed class MinecraftLaunchArguments
{
    [JsonPropertyName("game")]
    public List<JsonElement> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<JsonElement> Jvm { get; set; } = [];
}

internal sealed class MinecraftVersionDownloads
{
    [JsonPropertyName("client")]
    public MinecraftDownload? Client { get; set; }
}

internal class MinecraftDownload
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

internal sealed class MinecraftAssetIndex : MinecraftDownload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

internal sealed class MinecraftLibrary
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public MinecraftLibraryDownloads Downloads { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<MinecraftRule> Rules { get; set; } = [];

    [JsonPropertyName("natives")]
    public Dictionary<string, string> Natives { get; set; } = [];
}

internal sealed class MinecraftLibraryDownloads
{
    [JsonPropertyName("artifact")]
    public MinecraftDownload? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, MinecraftDownload> Classifiers { get; set; } = [];
}

internal sealed class MinecraftRule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("os")]
    public MinecraftRuleOs? Os { get; set; }
}

internal sealed class MinecraftRuleOs
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class MinecraftAssetsDocument
{
    [JsonPropertyName("objects")]
    public Dictionary<string, MinecraftAssetObject> Objects { get; set; } = [];
}

internal sealed class MinecraftAssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
