using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    private const int MaxParallelLibraryDownloads = 12;
    private const int MaxParallelAssetDownloads = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient = new();

    public async Task<MinecraftRuntime> EnsureAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = Path.Combine(settings.InstallDirectory, "minecraft-runtime");
        var librariesRoot = Path.Combine(settings.InstallDirectory, "libraries");
        var versionsRoot = Path.Combine(settings.InstallDirectory, "versions", manifest.MinecraftVersion);
        var assetsRoot = Path.Combine(settings.InstallDirectory, "assets");
        var nativesRoot = Path.Combine(settings.InstallDirectory, "natives", manifest.MinecraftVersion);

        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(versionsRoot);
        Directory.CreateDirectory(assetsRoot);
        Directory.CreateDirectory(nativesRoot);

        progress?.Report("Библиотеки Minecraft 0%");
        var versionJson = await LoadVersionJsonAsync(manifest.MinecraftVersion, versionsRoot, progress, cancellationToken);
        var loaderJson = await LoadLoaderVersionJsonAsync(manifest, runtimeRoot, progress, cancellationToken);

        var clientJarPath = Path.Combine(versionsRoot, manifest.MinecraftVersion + ".jar");
        if (versionJson.Downloads.Client is not null)
        {
            await EnsureDownloadAsync(versionJson.Downloads.Client, clientJarPath, progress, cancellationToken);
        }

        var workItems = new List<RuntimeDownloadItem>();
        AddLibraryDownloads(versionJson.Libraries, librariesRoot, workItems);
        if (loaderJson is not null)
        {
            AddLibraryDownloads(loaderJson.Libraries, librariesRoot, workItems);
        }

        await EnsureLibraryDownloadsAsync(workItems, progress, cancellationToken);

        foreach (var nativeJarPath in workItems
            .Where(item => item.IsNative)
            .Select(item => item.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ExtractNatives(nativeJarPath, nativesRoot);
        }

        progress?.Report("Библиотеки Minecraft 100%");
        var assetIndexId = versionJson.AssetIndex.Id;
        await EnsureAssetsAsync(versionJson.AssetIndex, assetsRoot, progress, cancellationToken);

        var classpath = workItems
            .Where(item => item.AddToClasspath)
            .Select(item => item.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MinecraftRuntime(
            loaderJson?.Id ?? manifest.MinecraftVersion,
            clientJarPath,
            classpath,
            nativesRoot,
            assetsRoot,
            assetIndexId,
            librariesRoot,
            loaderJson?.MainClass ?? "",
            ExtractStringArguments(loaderJson?.Arguments.Jvm),
            ExtractStringArguments(loaderJson?.Arguments.Game));
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

    private async Task<MinecraftVersionJson?> LoadLoaderVersionJsonAsync(
        LauncherManifest manifest,
        string runtimeRoot,
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

    private static void AddLibraryDownloads(
        IEnumerable<MinecraftLibrary> libraries,
        string librariesRoot,
        ICollection<RuntimeDownloadItem> workItems)
    {
        foreach (var library in libraries.Where(IsAllowedOnWindows))
        {
            if (library.Downloads.Artifact is not null)
            {
                var libraryPath = Path.Combine(librariesRoot, library.Downloads.Artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                workItems.Add(new RuntimeDownloadItem(library.Downloads.Artifact, libraryPath, AddToClasspath: true, IsNative: false));
            }

            var nativeDownload = NativeDownloadForWindows(library);
            if (nativeDownload is not null)
            {
                var nativeJarPath = Path.Combine(librariesRoot, nativeDownload.Path.Replace('/', Path.DirectorySeparatorChar));
                workItems.Add(new RuntimeDownloadItem(nativeDownload, nativeJarPath, AddToClasspath: false, IsNative: true));
            }
        }
    }

    private async Task EnsureLibraryDownloadsAsync(
        IReadOnlyCollection<RuntimeDownloadItem> workItems,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
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
                await EnsureDownloadAsync(item.Download, item.DestinationPath, progress: null, token);
                var done = Interlocked.Increment(ref completed);
                progress?.Report($"Библиотеки Minecraft {Percent(done, downloads.Count)}%");
            });
    }

    private async Task EnsureAssetsAsync(
        MinecraftAssetIndex assetIndex,
        string assetsRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
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
        var tempPath = destinationPath + ".download";

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

    private static bool IsAllowedOnWindows(MinecraftLibrary library)
    {
        if (library.Rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in library.Rules)
        {
            if (rule.Os is not null
                && !string.Equals(rule.Os.Name, "windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static MinecraftDownload? NativeDownloadForWindows(MinecraftLibrary library)
    {
        if (!library.Natives.TryGetValue("windows", out var classifier))
        {
            return null;
        }

        classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.OrdinalIgnoreCase);
        return library.Downloads.Classifiers.TryGetValue(classifier, out var download) ? download : null;
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
}

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
    IReadOnlyList<string> GameArgs);

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
