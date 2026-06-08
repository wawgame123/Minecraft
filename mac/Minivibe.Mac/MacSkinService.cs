using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using ServerLauncher.Models;

namespace Minivibe.Mac;

internal sealed class MacSkinService
{
    private const string SharedSkinsIndexUrl = "https://api.github.com/repos/wawgame123/Minecraft/contents/skins?ref=main";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    public MacSkinService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("minivibe-mac-launcher");
    }

    public string InstallSkin(LauncherSettings settings, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            throw new InvalidOperationException("Сначала подтвердите ник игрока.");
        }

        var pngBytes = ReadSkinPngBytes(sourcePath);
        SaveSkinToOfflineCache(settings, settings.PlayerName, pngBytes);
        return Path.Combine(
            settings.InstallDirectory,
            "cachedImages",
            "skins",
            "uuid",
            OfflinePlayerUuid(settings.PlayerName) + ".png");
    }

    public string? CachedSkinPath(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            return null;
        }

        var skinsRoot = Path.Combine(settings.InstallDirectory, "cachedImages", "skins");
        var playerName = settings.PlayerName.Trim();
        foreach (var path in new[]
        {
            Path.Combine(skinsRoot, "uuid", OfflinePlayerUuid(playerName) + ".png"),
            Path.Combine(skinsRoot, OfflinePlayerUuid(playerName) + ".png"),
            Path.Combine(skinsRoot, playerName + ".png"),
            Path.Combine(skinsRoot, playerName.ToLowerInvariant() + ".png")
        })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public async Task SaveOfflineSkinsConfigAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        var configRoot = Path.Combine(settings.InstallDirectory, "config");
        Directory.CreateDirectory(configRoot);

        var baseUrl = settings.SkinServerUrl.Trim().TrimEnd('/');
        var config = new OfflineSkinsConfig
        {
            UseMojang = false,
            UseCrafatar = false,
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
        var configJson = JsonSerializer.Serialize(config, JsonOptions);
        var existingConfigJson = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : "";
        if (!string.Equals(existingConfigJson, configJson, StringComparison.Ordinal))
        {
            RefreshOfflineSkinCache(settings);
        }

        await File.WriteAllTextAsync(configPath, configJson, cancellationToken);
    }

    public async Task UploadSharedSkinAsync(
        LauncherSettings settings,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            throw new InvalidOperationException("Сначала подтвердите ник игрока.");
        }

        var skinBase64 = Convert.ToBase64String(ReadSkinPngBytes(sourcePath));
        foreach (var playerName in PlayerNameAliases(settings.PlayerName))
        {
            var request = new SharedSkinUploadRequest(playerName, skinBase64);
            using var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.PostAsync(LauncherSettings.SharedSkinUploadUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {(int)response.StatusCode}"
                    : body;
                throw new InvalidOperationException("Не удалось загрузить скин в общий каталог: " + message);
            }
        }
    }

    public async Task<int> SyncSharedSkinsAsync(
        LauncherSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.EnableSkinServer)
        {
            return 0;
        }

        await using var stream = await _httpClient.GetStreamAsync(SharedSkinsIndexUrl, cancellationToken);
        var items = await JsonSerializer.DeserializeAsync<List<SharedSkinIndexItem>>(stream, JsonOptions, cancellationToken) ?? [];
        var skins = items
            .Where(item =>
                string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) &&
                item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.DownloadUrl))
            .ToList();

        var synced = 0;
        foreach (var skin in skins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playerName = Path.GetFileNameWithoutExtension(skin.Name).Trim();
            if (!IsValidMinecraftName(playerName))
            {
                continue;
            }

            try
            {
                var pngBytes = await _httpClient.GetByteArrayAsync(skin.DownloadUrl, cancellationToken);
                ValidateSkinPngBytes(pngBytes);
                SaveSkinToOfflineCache(settings, playerName, pngBytes);
                synced++;
                progress?.Report($"Синхронизирую скины: {synced}/{skins.Count}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                progress?.Report($"Не удалось обновить скин {playerName}, продолжаю...");
            }
        }

        return synced;
    }

    public static void ValidateSkinImage(string path)
    {
        _ = ReadSkinPngBytes(path);
    }

    public static byte[] ReadSkinPngBytes(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Файл скина не найден.", path);
        }

        using var bitmap = new Bitmap(path);
        if (bitmap.PixelSize.Width != 64 || bitmap.PixelSize.Height is not (32 or 64))
        {
            throw new InvalidOperationException("Скин должен быть PNG/JPG размером 64x64 или 64x32.");
        }

        using var output = new MemoryStream();
        bitmap.Save(output);
        return output.ToArray();
    }

    private static void ValidateSkinPngBytes(byte[] pngBytes)
    {
        using var input = new MemoryStream(pngBytes);
        using var bitmap = new Bitmap(input);
        if (bitmap.PixelSize.Width != 64 || bitmap.PixelSize.Height is not (32 or 64))
        {
            throw new InvalidOperationException("Скин должен быть PNG размером 64x64 или 64x32.");
        }
    }

    private static void SaveSkinToOfflineCache(LauncherSettings settings, string playerName, byte[] pngBytes)
    {
        var skinsRoot = Path.Combine(settings.InstallDirectory, "cachedImages", "skins");
        var uuidRoot = Path.Combine(skinsRoot, "uuid");
        Directory.CreateDirectory(uuidRoot);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in PlayerNameAliases(playerName))
        {
            var uuid = OfflinePlayerUuid(alias);
            paths.Add(Path.Combine(uuidRoot, uuid + ".png"));
            paths.Add(Path.Combine(skinsRoot, uuid + ".png"));
            paths.Add(Path.Combine(skinsRoot, alias + ".png"));
        }

        foreach (var path in paths)
        {
            File.WriteAllBytes(path, pngBytes);
        }
    }

    private static void RefreshOfflineSkinCache(LauncherSettings settings)
    {
        var skinsRoot = Path.Combine(settings.InstallDirectory, "cachedImages", "skins");
        if (!Directory.Exists(skinsRoot))
        {
            return;
        }

        var root = Path.GetFullPath(skinsRoot);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            foreach (var alias in PlayerNameAliases(settings.PlayerName))
            {
                var uuid = OfflinePlayerUuid(alias);
                keep.Add(Path.GetFullPath(Path.Combine(skinsRoot, "uuid", uuid + ".png")));
                keep.Add(Path.GetFullPath(Path.Combine(skinsRoot, uuid + ".png")));
                keep.Add(Path.GetFullPath(Path.Combine(skinsRoot, alias + ".png")));
            }
        }

        foreach (var file in Directory.EnumerateFiles(skinsRoot, "*.png", SearchOption.AllDirectories))
        {
            var path = Path.GetFullPath(file);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || keep.Contains(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IEnumerable<string> PlayerNameAliases(string playerName)
    {
        var exact = playerName.Trim();
        yield return exact;

        var lower = exact.ToLowerInvariant();
        if (!string.Equals(exact, lower, StringComparison.Ordinal))
        {
            yield return lower;
        }
    }

    private static string OfflinePlayerUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static bool IsValidMinecraftName(string playerName)
    {
        if (playerName.Length is < 3 or > 16)
        {
            return false;
        }

        return playerName.All(static ch =>
            ch is >= 'A' and <= 'Z' ||
            ch is >= 'a' and <= 'z' ||
            ch is >= '0' and <= '9' ||
            ch == '_');
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

    private sealed record SharedSkinUploadRequest(
        [property: JsonPropertyName("playerName")] string PlayerName,
        [property: JsonPropertyName("skinBase64")] string SkinBase64);

    private sealed class SharedSkinIndexItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";
    }
}
