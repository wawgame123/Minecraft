namespace ServerLauncher.Models;

public sealed class LauncherSettings
{
    public string ManifestUrl { get; set; } = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/manifest.json";
    public string InstallDirectory { get; set; } = DefaultInstallDirectory();
    public bool EnableShaders { get; set; }
    public int RamMb { get; set; } = 4096;
    public string JavaPath { get; set; } = "";
    public string PlayerName { get; set; } = Environment.UserName;
    public string ExtraLaunchArguments { get; set; } = "";

    public static string DefaultInstallDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "ServerLauncher", "Game");
    }
}
