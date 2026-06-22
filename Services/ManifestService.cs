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

    public Task<LauncherManifest> LoadAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        return LoadAsync(manifestUrl, cancellationToken, bypassCache: false);
    }

    public async Task<LauncherManifest> LoadAsync(
        string manifestUrl,
        CancellationToken cancellationToken,
        bool bypassCache)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException("Внутренняя ссылка на manifest.json не настроена.");
        }

        await using var stream = await OpenManifestStreamAsync(manifestUrl, cancellationToken, bypassCache);
        var manifest = await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions, cancellationToken);

        if (manifest is null)
        {
            throw new InvalidOperationException("manifest.json пустой или имеет неверный формат.");
        }

        LauncherManifestNormalizer.Normalize(manifest);
        return manifest;
    }

    private async Task<Stream> OpenManifestStreamAsync(
        string manifestUrl,
        CancellationToken cancellationToken,
        bool bypassCache)
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
            var requestUrl = bypassCache ? AddCacheBuster(manifestUrl) : manifestUrl;
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (bypassCache)
            {
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return new MemoryStream(await response.Content.ReadAsByteArrayAsync(cancellationToken), writable: false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Не удалось загрузить manifest.json: {manifestUrl}. {ex.Message}", ex);
        }
    }

    private static string AddCacheBuster(string url)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + "minivibe=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
