using System.IO;
using System.Reflection;
using System.Text.Json;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; }

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Minivibe");
        Directory.CreateDirectory(directory);
        SettingsPath = Path.Combine(directory, "settings.json");
    }

    public async Task<LauncherSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            var settings = new LauncherSettings
            {
                LastSeenLauncherVersion = CurrentLauncherVersion()
            };
            await SaveAsync(settings);
            return settings;
        }

        await using var stream = File.OpenRead(SettingsPath);
        return await JsonSerializer.DeserializeAsync<LauncherSettings>(stream) ?? new LauncherSettings();
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    private static string CurrentLauncherVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
