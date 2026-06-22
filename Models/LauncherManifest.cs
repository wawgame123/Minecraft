using System.Text.Json.Serialization;

namespace ServerLauncher.Models;

public sealed class LauncherManifest
{
    [JsonPropertyName("manifestFormatVersion")]
    public int ManifestFormatVersion { get; set; } = 2;

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "Minecraft Server";

    [JsonPropertyName("packVersion")]
    public string PackVersion { get; set; } = "unknown";

    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = "";

    [JsonPropertyName("loader")]
    public string Loader { get; set; } = "";

    [JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; set; } = "";

    [JsonPropertyName("blueMapUrl")]
    public string BlueMapUrl { get; set; } = "";

    [JsonPropertyName("modTypes")]
    public List<string> ModTypes { get; set; } = [];

    [JsonPropertyName("requiredFiles")]
    public List<ManifestFile> RequiredFiles { get; set; } = [];

    [JsonPropertyName("optionalShaders")]
    public List<ManifestFile> OptionalShaders { get; set; } = [];

    [JsonPropertyName("optionalEmotes")]
    public List<ManifestFile> OptionalEmotes { get; set; } = [];

    [JsonPropertyName("changelog")]
    public List<string> Changelog { get; set; } = [];

    [JsonPropertyName("news")]
    [JsonConverter(typeof(NewsItemListJsonConverter))]
    public List<NewsItem> News { get; set; } = [];

    [JsonPropertyName("launch")]
    public LaunchManifestOptions Launch { get; set; } = new();
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("extractTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ExtractTo { get; set; }
}

public sealed class LaunchManifestOptions
{
    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("classpath")]
    public List<string> Classpath { get; set; } = [];

    [JsonPropertyName("jvmArgs")]
    public List<string> JvmArgs { get; set; } = [];

    [JsonPropertyName("gameArgs")]
    public List<string> GameArgs { get; set; } = [];
}

public static class LauncherManifestNormalizer
{
    public const int CurrentFormatVersion = 2;
    public const string DefaultModType = "Основные";

    public static void Normalize(LauncherManifest manifest)
    {
        var legacyManifest = manifest.ManifestFormatVersion <= 0;

        manifest.ModTypes = NormalizeModTypes(manifest.ModTypes);

        foreach (var file in manifest.RequiredFiles)
        {
            if (legacyManifest)
            {
                file.Required = true;
            }

            if (IsModFile(file))
            {
                if (string.IsNullOrWhiteSpace(file.Category)
                    || file.Category.Equals("mod", StringComparison.OrdinalIgnoreCase))
                {
                    file.Category = DefaultModType;
                }

                if (!manifest.ModTypes.Contains(file.Category, StringComparer.OrdinalIgnoreCase))
                {
                    manifest.ModTypes.Add(file.Category);
                }
            }
        }

        manifest.ModTypes = NormalizeModTypes(manifest.ModTypes);
        manifest.ManifestFormatVersion = CurrentFormatVersion;
    }

    public static bool IsModFile(ManifestFile file)
    {
        var path = file.Path.Replace('\\', '/');
        return path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeModTypes(IEnumerable<string>? modTypes)
    {
        var result = new List<string>();
        foreach (var modType in modTypes ?? [])
        {
            var normalized = modType.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }

        if (result.Count == 0)
        {
            result.Add(DefaultModType);
        }

        return result;
    }
}
