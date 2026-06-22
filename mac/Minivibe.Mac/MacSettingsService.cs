using System.Text.Json;
using System.Reflection;
using ServerLauncher.Models;

namespace Minivibe.Mac;

internal sealed class MacSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; }

    public MacSettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Minivibe");
        Directory.CreateDirectory(directory);
        SettingsPath = Path.Combine(directory, "settings-mac.json");
    }

    public async Task<LauncherSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            var settings = new LauncherSettings
            {
                InstallDirectory = DefaultInstallDirectory(),
                SkinServerUrl = LauncherSettings.DefaultSkinServerUrl,
                EnableSkinServer = true,
                EnableAutoUpdate = true,
                LastSeenLauncherVersion = CurrentLauncherVersion()
            };
            await SaveAsync(settings);
            return settings;
        }

        await using var stream = File.OpenRead(SettingsPath);
        var loaded = await JsonSerializer.DeserializeAsync<LauncherSettings>(stream) ?? new LauncherSettings();
        if (string.IsNullOrWhiteSpace(loaded.InstallDirectory))
        {
            loaded.InstallDirectory = DefaultInstallDirectory();
        }

        loaded.SkinServerUrl = LauncherSettings.DefaultSkinServerUrl;
        loaded.EnableSkinServer = true;
        loaded.EnableAutoUpdate = true;
        loaded.SelectedModTypes ??= [];
        return loaded;
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    public static string DefaultInstallDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "minivibe",
            "Game");
    }

    private static string CurrentLauncherVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
