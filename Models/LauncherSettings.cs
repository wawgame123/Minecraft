namespace ServerLauncher.Models;

public sealed class LauncherSettings
{
    public string InstallDirectory { get; set; } = DefaultInstallDirectory();
    public bool EnableAutoUpdate { get; set; } = true;
    public bool EnableShaders { get; set; }
    public int RamMb { get; set; } = 4096;
    public string JavaPath { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SkinSourcePath { get; set; } = "";
    public string SkinServerUrl { get; set; } = "";
    public bool EnableSkinServer { get; set; }
    public string ExtraLaunchArguments { get; set; } = "";
    public string VisualTheme { get; set; } = "Obsidian";
    public string AccentColor { get; set; } = "Crimson";
    public string CustomBackgroundColor { get; set; } = "";
    public string CustomSidebarColor { get; set; } = "";
    public string CustomSurfaceColor { get; set; } = "";
    public string CustomBorderColor { get; set; } = "";
    public string CustomTextColor { get; set; } = "";
    public string CustomMutedTextColor { get; set; } = "";
    public string CustomAccentColor { get; set; } = "";
    public string CustomGradientStartColor { get; set; } = "";
    public string CustomGradientEndColor { get; set; } = "";
    public bool DynamicBackground { get; set; } = true;
    public double PanelOpacity { get; set; } = 0.92;
    public string? LastSeenLauncherVersion { get; set; }

    public static string DefaultInstallDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "Minivibe", "Game");
    }
}
