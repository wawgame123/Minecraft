using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class FileSyncService
{
    public const string StatusCurrent = "Актуален";
    public const string StatusMissing = "Отсутствует";
    public const string StatusWrongSize = "Неверный размер";
    public const string StatusCorrupt = "Поврежден";

    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyList<FileStatusItem>> VerifyAndRepairAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        bool downloadMissingFiles,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.InstallDirectory);
        var files = GetManagedFiles(manifest, settings.EnableShaders).ToList();
        var statuses = new List<FileStatusItem>();

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report($"Проверка {index + 1}/{files.Count}: {file.Path}");

            var fullPath = ResolveInsideInstallDirectory(settings.InstallDirectory, file.Path);
            var status = await CheckFileAsync(fullPath, file, cancellationToken);

            if (status != StatusCurrent && downloadMissingFiles)
            {
                await DownloadFileAsync(file, fullPath, progress, cancellationToken);
                status = await CheckFileAsync(fullPath, file, cancellationToken);
            }

            statuses.Add(new FileStatusItem
            {
                Path = file.Path,
                Category = file.Category,
                Required = file.Required,
                Size = file.Size,
                Status = status
            });
        }

        progress?.Report("Проверка завершена");
        return statuses;
    }

    public static IEnumerable<ManifestFile> GetManagedFiles(LauncherManifest manifest, bool includeShaders)
    {
        foreach (var file in manifest.RequiredFiles.Where(file => file.Required))
        {
            yield return file;
        }

        if (!includeShaders)
        {
            yield break;
        }

        foreach (var file in manifest.OptionalShaders)
        {
            yield return file;
        }
    }

    private static async Task<string> CheckFileAsync(string fullPath, ManifestFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return StatusMissing;
        }

        var info = new FileInfo(fullPath);
        if (file.Size > 0 && info.Length != file.Size)
        {
            return StatusWrongSize;
        }

        if (HasRealHash(file.Sha256))
        {
            var actualHash = await ComputeSha256Async(fullPath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCorrupt;
            }
        }

        return StatusCurrent;
    }

    private async Task DownloadFileAsync(
        ManifestFile file,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            throw new InvalidOperationException($"Для файла {file.Path} не указан url.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".download";
        progress?.Report($"Скачивание: {file.Path}");

        try
        {
            await using var source = await _httpClient.GetStreamAsync(file.Url, cancellationToken);
            await using var target = File.Create(tempPath);
            await source.CopyToAsync(target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw new InvalidOperationException($"Не удалось скачать файл {file.Path}: {file.Url}. {ex.Message}", ex);
        }

        if (HasRealHash(file.Sha256))
        {
            var actualHash = await ComputeSha256Async(tempPath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException($"SHA-256 не совпал после загрузки: {file.Path}");
            }
        }

        File.Move(tempPath, destinationPath, true);
    }

    private static string ResolveInsideInstallDirectory(string installDirectory, string relativePath)
    {
        var root = Path.GetFullPath(installDirectory);
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));

        if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Недопустимый путь в manifest.json: {relativePath}");
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasRealHash(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash)
            && !hash.Equals("HASH_HERE", StringComparison.OrdinalIgnoreCase)
            && hash.Length >= 32;
    }
}
