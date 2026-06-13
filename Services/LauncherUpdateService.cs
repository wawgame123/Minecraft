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
    private readonly string _pendingPatchNotesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Minivibe",
        "pending-patch-notes.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HttpClient _httpClient = new();

    public async Task<LauncherUpdateManifest?> FindAvailableUpdateAsync(CancellationToken cancellationToken)
    {
        var update = await LoadUpdateManifestAsync(LauncherEndpoints.UpdateManifestUrl, cancellationToken);
        if (update is null)
        {
            return null;
        }

        var asset = ResolveWindowsUpdateAsset(update);
        var targetVersion = ResolveAssetVersion(update, asset);
        return !string.IsNullOrWhiteSpace(asset.Url) && IsNewerThanCurrent(targetVersion) ? update : null;
    }

    public async Task<PreparedLauncherUpdate?> CheckAndPrepareUpdateAsync(
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableAutoUpdate)
        {
            return null;
        }

        progress?.Report("Проверяю обновления лаунчера...");
        var update = await FindAvailableUpdateAsync(cancellationToken);
        if (update is null)
        {
            progress?.Report("Лаунчер актуален.");
            return null;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("Обновление доступно, но текущий запуск не похож на готовый .exe.");
            return null;
        }

        var asset = ResolveWindowsUpdateAsset(update);
        var targetVersion = ResolveAssetVersion(update, asset);
        var currentVersion = CurrentLauncherVersion();
        var patch = asset.Patches.TryGetValue(currentVersion, out var availablePatch)
            && !string.IsNullOrWhiteSpace(availablePatch.Url)
                ? availablePatch
                : null;
        var payloadUrl = patch?.Url ?? asset.Url;
        var payloadHash = patch?.Sha256 ?? asset.Sha256;
        if (string.IsNullOrWhiteSpace(payloadUrl))
        {
            throw new InvalidOperationException("Для Windows не указан файл обновления лаунчера.");
        }

        progress?.Report(patch is null
            ? $"Скачиваю полное обновление лаунчера {targetVersion}..."
            : $"Докачиваю измененные файлы {currentVersion} → {targetVersion}...");
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Minivibe",
            "Updates",
            targetVersion);
        Directory.CreateDirectory(updateRoot);

        var zipPath = Path.Combine(updateRoot, patch is null ? "launcher-update.zip" : "launcher-patch.zip");
        await DownloadFileAsync(payloadUrl, zipPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(payloadHash))
        {
            var actualHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!string.Equals(actualHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(patch is null
                    ? "SHA-256 обновления лаунчера не совпал."
                    : "SHA-256 частичного обновления лаунчера не совпал.");
            }
        }

        var extractPath = Path.Combine(updateRoot, "extracted");
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractPath);
        return new PreparedLauncherUpdate(
            CreatePlatformManifest(update, asset),
            extractPath,
            AppContext.BaseDirectory,
            processPath,
            Environment.ProcessId);
    }

    public async Task ApplyPreparedUpdateAsync(
        PreparedLauncherUpdate preparedUpdate,
        CancellationToken cancellationToken)
    {
        await SavePendingPatchNotesAsync(preparedUpdate.Manifest, cancellationToken);
        StartUpdaterScript(
            preparedUpdate.ExtractPath,
            preparedUpdate.TargetDirectory,
            preparedUpdate.ExePath,
            preparedUpdate.ProcessId);
    }

    public async Task<LauncherUpdateManifest?> ReadPendingPatchNotesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_pendingPatchNotesPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_pendingPatchNotesPath);
            var notes = await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream, JsonOptions, cancellationToken);
            File.Delete(_pendingPatchNotesPath);
            return notes;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LauncherUpdateManifest?> LoadPatchNotesForVersionAsync(
        string version,
        CancellationToken cancellationToken)
    {
        var update = await LoadUpdateManifestAsync(LauncherEndpoints.UpdateManifestUrl, cancellationToken);
        if (update is null)
        {
            return null;
        }

        var asset = ResolveWindowsUpdateAsset(update);
        return string.Equals(ResolveAssetVersion(update, asset), version, StringComparison.OrdinalIgnoreCase)
            ? CreatePlatformManifest(update, asset)
            : null;
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

    private static bool IsNewerThanCurrent(string remoteVersion)
    {
        if (!Version.TryParse(remoteVersion, out var parsedRemoteVersion))
        {
            return false;
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return NormalizeVersion(parsedRemoteVersion) > NormalizeVersion(currentVersion);
    }

    private static LauncherUpdateAsset ResolveWindowsUpdateAsset(LauncherUpdateManifest update)
    {
        if (update.Platforms.TryGetValue("win-x64", out var asset)
            && !string.IsNullOrWhiteSpace(asset.Url))
        {
            return asset;
        }

        return new LauncherUpdateAsset
        {
            Version = update.Version,
            Url = update.Url,
            Sha256 = update.Sha256,
            Notes = update.Notes
        };
    }

    private static string ResolveAssetVersion(LauncherUpdateManifest update, LauncherUpdateAsset asset)
    {
        return string.IsNullOrWhiteSpace(asset.Version) ? update.Version : asset.Version;
    }

    private static LauncherUpdateManifest CreatePlatformManifest(
        LauncherUpdateManifest update,
        LauncherUpdateAsset asset)
    {
        return new LauncherUpdateManifest
        {
            Version = ResolveAssetVersion(update, asset),
            Url = asset.Url,
            Sha256 = asset.Sha256,
            Mandatory = update.Mandatory,
            Notes = asset.Notes.Count > 0 ? asset.Notes : update.Notes,
            Platforms = update.Platforms
        };
    }

    private static string CurrentLauncherVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0];
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return version.Build == 0
            ? $"{version.Major}.{version.Minor}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = await _httpClient.GetStreamAsync(url, cancellationToken);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task SavePendingPatchNotesAsync(LauncherUpdateManifest update, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pendingPatchNotesPath)!);
        await using var stream = File.Create(_pendingPatchNotesPath);
        await JsonSerializer.SerializeAsync(stream, update, JsonOptions, cancellationToken);
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
        $deleteMarker = Join-Path $source '.minivibe-delete.txt'
        if (Test-Path -LiteralPath $deleteMarker) {
          $targetRoot = [IO.Path]::GetFullPath($target).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
          foreach ($relativePath in Get-Content -LiteralPath $deleteMarker) {
            if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
            $candidate = [IO.Path]::GetFullPath((Join-Path $target $relativePath))
            if ($candidate.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
              Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
            }
          }
          Remove-Item -LiteralPath $deleteMarker -Force
        }
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

public sealed record PreparedLauncherUpdate(
    LauncherUpdateManifest Manifest,
    string ExtractPath,
    string TargetDirectory,
    string ExePath,
    int ProcessId);
