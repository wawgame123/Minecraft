using System.Text.Json.Serialization;

namespace ServerLauncher.Models;

public sealed class LauncherUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];

    [JsonPropertyName("platforms")]
    public Dictionary<string, LauncherUpdateAsset> Platforms { get; set; } = [];
}

public sealed class LauncherUpdateAsset
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];

    [JsonPropertyName("patches")]
    public Dictionary<string, LauncherUpdatePatch> Patches { get; set; } = [];
}

public sealed class LauncherUpdatePatch
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}
