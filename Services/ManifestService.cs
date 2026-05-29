using System.IO;
using System.Net.Http;
using System.Text.Json;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HttpClient _httpClient = new();

    public async Task<LauncherManifest> LoadAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException("Внутренняя ссылка на manifest.json не настроена.");
        }

        await using var stream = await OpenManifestStreamAsync(manifestUrl, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions, cancellationToken);

        if (manifest is null)
        {
            throw new InvalidOperationException("manifest.json пустой или имеет неверный формат.");
        }

        return manifest;
    }

    private async Task<Stream> OpenManifestStreamAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        if (File.Exists(manifestUrl))
        {
            return File.OpenRead(manifestUrl);
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return File.OpenRead(uri.LocalPath);
        }

        try
        {
            return await _httpClient.GetStreamAsync(manifestUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Не удалось загрузить manifest.json: {manifestUrl}. {ex.Message}", ex);
        }
    }
}
