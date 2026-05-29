using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class LauncherUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HttpClient _httpClient = new();

    public async Task<bool> CheckAndApplyUpdateAsync(
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableAutoUpdate || string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            return false;
        }

        progress?.Report("Проверяю обновления лаунчера...");
        var update = await LoadUpdateManifestAsync(settings.UpdateManifestUrl, cancellationToken);
        if (update is null || string.IsNullOrWhiteSpace(update.Version) || string.IsNullOrWhiteSpace(update.Url))
        {
            return false;
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (!Version.TryParse(update.Version, out var remoteVersion) || remoteVersion <= currentVersion)
        {
            progress?.Report("Лаунчер актуален.");
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("Обновление доступно, но текущий запуск не похож на готовый .exe.");
            return false;
        }

        progress?.Report($"Скачиваю обновление лаунчера {update.Version}...");
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Minivibe",
            "Updates",
            update.Version);
        Directory.CreateDirectory(updateRoot);

        var zipPath = Path.Combine(updateRoot, "launcher-update.zip");
        await DownloadFileAsync(update.Url, zipPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            var actualHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHA-256 обновления лаунчера не совпал.");
            }
        }

        var extractPath = Path.Combine(updateRoot, "extracted");
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractPath);
        StartUpdaterScript(extractPath, AppContext.BaseDirectory, processPath, Environment.ProcessId);
        return true;
    }

    private async Task<LauncherUpdateManifest?> LoadUpdateManifestAsync(string updateUrl, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(updateUrl, cancellationToken);
            return await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = await _httpClient.GetStreamAsync(url, cancellationToken);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static void StartUpdaterScript(string sourceDirectory, string targetDirectory, string exePath, int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"minivibe-updater-{Guid.NewGuid():N}.ps1");
        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $source = '{{EscapePowerShell(sourceDirectory)}}'
        $target = '{{EscapePowerShell(targetDirectory)}}'
        $exe = '{{EscapePowerShell(exePath)}}'
        $pidToWait = {{processId}}
        try {
          Wait-Process -Id $pidToWait -Timeout 60 -ErrorAction SilentlyContinue
        } catch {}
        Start-Sleep -Milliseconds 500
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        Start-Process -FilePath $exe -WorkingDirectory $target
        Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
        """;
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
