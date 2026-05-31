using System.Collections.Concurrent;
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
    private const int MaxParallelChecks = 8;
    private const int MaxParallelDownloads = 16;

    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyList<FileStatusItem>> VerifyAndRepairAsync(
        LauncherManifest manifest,
        LauncherSettings settings,
        bool downloadMissingFiles,
        bool verifyHashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.InstallDirectory);
        var files = GetManagedFiles(manifest, settings.EnableShaders).ToList();
        var statuses = new FileStatusItem[files.Count];
        var repairQueue = new ConcurrentBag<(int Index, ManifestFile File, string FullPath)>();
        var checkedCount = 0;

        ReportPercent(progress, "Проверка", 0, files.Count);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelChecks,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var file = files[index];
                var fullPath = ResolveInsideInstallDirectory(settings.InstallDirectory, file.Path);
                var status = await CheckFileAsync(fullPath, file, verifyHashes, token);
                statuses[index] = new FileStatusItem
                {
                    Path = file.Path,
                    Category = file.Category,
                    Required = file.Required,
                    Size = file.Size,
                    Status = status
                };

                if (status != StatusCurrent)
                {
                    repairQueue.Add((index, file, fullPath));
                }

                var done = Interlocked.Increment(ref checkedCount);
                ReportPercent(progress, "Проверка", done, files.Count);
            });

        if (downloadMissingFiles && repairQueue.Count > 0)
        {
            var completed = 0;
            var repairCount = repairQueue.Count;
            ReportPercent(progress, "Скачивание", completed, repairCount);
            await Parallel.ForEachAsync(
                repairQueue,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDownloads,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    await DownloadFileAsync(item.File, item.FullPath, token);
                    var status = await CheckFileAsync(item.FullPath, item.File, verifyHashes: true, token);
                    statuses[item.Index].Status = status;

                    var done = Interlocked.Increment(ref completed);
                    ReportPercent(progress, "Скачивание", done, repairCount);
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

    private static async Task<string> CheckFileAsync(
        string fullPath,
        ManifestFile file,
        bool verifyHashes,
        CancellationToken cancellationToken)
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

        if (verifyHashes && HasRealHash(file.Sha256))
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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            throw new InvalidOperationException($"Для файла {file.Path} не указан url.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".download";

        try
        {
            await using (var source = await _httpClient.GetStreamAsync(file.Url, cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }
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

    private static void ReportPercent(IProgress<string>? progress, string label, int completed, int total)
    {
        if (progress is null)
        {
            return;
        }

        var percent = total <= 0 ? 100 : (int)Math.Round(completed * 100d / total);
        progress.Report($"{label} {Math.Clamp(percent, 0, 100)}%");
    }
}
