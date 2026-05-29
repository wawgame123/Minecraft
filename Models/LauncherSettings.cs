namespace ServerLauncher.Models;

public sealed class LauncherSettings
{
    public string ManifestUrl { get; set; } = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/manifest.json";
    public string InstallDirectory { get; set; } = DefaultInstallDirectory();
    public bool EnableShaders { get; set; }
    public int RamMb { get; set; } = 4096;
    public string JavaPath { get; set; } = "";
    public string PlayerName { get; set; } = "wawgame";
    public string ExtraLaunchArguments { get; set; } = "";
    public string VisualTheme { get; set; } = "Obsidian";
    public string AccentColor { get; set; } = "Crimson";
    public bool DynamicBackground { get; set; } = true;
    public bool CompactMode { get; set; }
    public double PanelOpacity { get; set; } = 0.92;

    public static string DefaultInstallDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "ServerLauncher", "Game");
    }
}
