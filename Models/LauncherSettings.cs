namespace ServerLauncher.Models;

public sealed class LauncherSettings
{
    public string ManifestUrl { get; set; } = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/manifest.json";
    public string InstallDirectory { get; set; } = DefaultInstallDirectory();
    public bool EnableAutoUpdate { get; set; } = true;
    public string UpdateManifestUrl { get; set; } = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/launcher/update.json";
    public string BugReportEmail { get; set; } = "tupikp37@gmail.com";
    public string BugReportEndpoint { get; set; } = "";
    public bool OpenEmailOnError { get; set; } = true;
    public bool EnableShaders { get; set; }
    public int RamMb { get; set; } = 4096;
    public string JavaPath { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string ExtraLaunchArguments { get; set; } = "";
    public string VisualTheme { get; set; } = "Obsidian";
    public string AccentColor { get; set; } = "Crimson";
    public bool DynamicBackground { get; set; } = true;
    public bool CompactMode { get; set; }
    public double PanelOpacity { get; set; } = 0.92;
    public string? LastSeenLauncherVersion { get; set; }

    public static string DefaultInstallDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "Minivibe", "Game");
    }
}
