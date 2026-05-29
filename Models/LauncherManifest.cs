using System.Text.Json.Serialization;

namespace ServerLauncher.Models;

public sealed class LauncherManifest
{
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

    [JsonPropertyName("requiredFiles")]
    public List<ManifestFile> RequiredFiles { get; set; } = [];

    [JsonPropertyName("optionalShaders")]
    public List<ManifestFile> OptionalShaders { get; set; } = [];

    [JsonPropertyName("changelog")]
    public List<string> Changelog { get; set; } = [];

    [JsonPropertyName("news")]
    public List<string> News { get; set; } = [];

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
