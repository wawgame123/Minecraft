using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class SkinService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string InstallSkin(LauncherSettings settings, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            throw new InvalidOperationException("Сначала подтвердите ник игрока.");
        }

        ValidateSkinImage(sourcePath);
        var uuid = OfflinePlayerUuid(settings.PlayerName);
        var skinsRoot = Path.Combine(settings.InstallDirectory, "cachedImages", "skins");
        var uuidRoot = Path.Combine(skinsRoot, "uuid");
        Directory.CreateDirectory(uuidRoot);

        var byUuid = Path.Combine(uuidRoot, uuid + ".png");
        var byUuidFlat = Path.Combine(skinsRoot, uuid + ".png");
        var byName = Path.Combine(skinsRoot, settings.PlayerName + ".png");
        CopyIfDifferent(sourcePath, byUuid);
        CopyIfDifferent(sourcePath, byUuidFlat);
        CopyIfDifferent(sourcePath, byName);
        return byUuid;
    }

    public string? CachedSkinPath(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            return null;
        }

        var path = Path.Combine(
            settings.InstallDirectory,
            "cachedImages",
            "skins",
            "uuid",
            OfflinePlayerUuid(settings.PlayerName) + ".png");
        if (File.Exists(path))
        {
            return path;
        }

        var flatPath = Path.Combine(
            settings.InstallDirectory,
            "cachedImages",
            "skins",
            OfflinePlayerUuid(settings.PlayerName) + ".png");
        return File.Exists(flatPath) ? flatPath : null;
    }

    public async Task SaveOfflineSkinsConfigAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var configRoot = Path.Combine(settings.InstallDirectory, "config");
        Directory.CreateDirectory(configRoot);

        var baseUrl = settings.SkinServerUrl.Trim().TrimEnd('/');
        var config = new OfflineSkinsConfig
        {
            UseMojang = true,
            UseCrafatar = true,
            UseCustomServer = false,
            HostCustomServer = "http://example.com",
            UseCustomServer2 = settings.EnableSkinServer && !string.IsNullOrWhiteSpace(baseUrl),
            HostCustomServer2Skin = string.IsNullOrWhiteSpace(baseUrl)
                ? "http://example.com/skins/%auto%"
                : baseUrl + "/skins/%name%.png",
            HostCustomServer2Cape = string.IsNullOrWhiteSpace(baseUrl)
                ? "http://example.com/capes/%auto%"
                : baseUrl + "/capes/%name%.png",
            DisablePlayerHeads = false
        };

        var configPath = Path.Combine(configRoot, "offlineskins.json");
        await using var stream = File.Create(configPath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
    }

    public static void ValidateSkinImage(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Файл скина не найден.", path);
        }

        BitmapFrame frame;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        if (frame.PixelWidth != 64 || frame.PixelHeight is not (32 or 64))
        {
            throw new InvalidOperationException("Скин должен быть PNG размером 64x64 или 64x32.");
        }
    }

    public static string OfflinePlayerUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static void CopyIfDifferent(string sourcePath, string destinationPath)
    {
        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private sealed class OfflineSkinsConfig
    {
        [JsonPropertyName("useMojang")]
        public bool UseMojang { get; set; }

        [JsonPropertyName("useCrafatar")]
        public bool UseCrafatar { get; set; }

        [JsonPropertyName("useCustomServer")]
        public bool UseCustomServer { get; set; }

        [JsonPropertyName("hostCustomServer")]
        public string HostCustomServer { get; set; } = "";

        [JsonPropertyName("useCustomServer2")]
        public bool UseCustomServer2 { get; set; }

        [JsonPropertyName("hostCustomServer2Skin")]
        public string HostCustomServer2Skin { get; set; } = "";

        [JsonPropertyName("hostCustomServer2Cape")]
        public string HostCustomServer2Cape { get; set; } = "";

        [JsonPropertyName("disablePlayerHeads")]
        public bool DisablePlayerHeads { get; set; }
    }
}
