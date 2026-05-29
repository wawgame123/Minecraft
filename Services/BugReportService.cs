using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class BugReportService
{
    private readonly HttpClient _httpClient = new();

    public string ReportsDirectory { get; }

    public BugReportService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        ReportsDirectory = Path.Combine(appData, "Minivibe", "Reports");
        Directory.CreateDirectory(ReportsDirectory);
    }

    public async Task<string> HandleAsync(
        Exception exception,
        string context,
        LauncherSettings settings,
        LauncherManifest? manifest,
        bool openEmailDraft,
        CancellationToken cancellationToken = default)
    {
        var report = CreateReport(exception, context, settings, manifest);
        var reportPath = await SaveReportAsync(report, cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.BugReportEndpoint))
        {
            await TryPostReportAsync(settings.BugReportEndpoint, report, cancellationToken);
        }

        if (openEmailDraft && settings.OpenEmailOnError)
        {
            TryOpenEmailDraft(settings.BugReportEmail, report, reportPath);
        }

        TryCopyToClipboard(report);
        return reportPath;
    }

    public string CreateReport(Exception exception, string context, LauncherSettings settings, LauncherManifest? manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("minivibe launcher bug report");
        builder.AppendLine($"Time: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Context: {context}");
        builder.AppendLine($"App version: {typeof(BugReportService).Assembly.GetName().Version}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"User: {Environment.UserName}");
        builder.AppendLine($"Manifest URL: {LauncherEndpoints.ManifestUrl}");
        builder.AppendLine($"Install directory: {settings.InstallDirectory}");
        builder.AppendLine($"Shaders enabled: {settings.EnableShaders}");
        builder.AppendLine($"Auto update enabled: {settings.EnableAutoUpdate}");

        if (manifest is not null)
        {
            builder.AppendLine($"Server: {manifest.ServerName}");
            builder.AppendLine($"Pack: {manifest.PackVersion}");
            builder.AppendLine($"Minecraft: {manifest.MinecraftVersion}");
            builder.AppendLine($"Loader: {manifest.Loader} {manifest.LoaderVersion}");
        }

        builder.AppendLine();
        builder.AppendLine("Exception:");
        builder.AppendLine(exception.ToString());

        return builder.ToString();
    }

    public async Task<string> SaveReportAsync(string report, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ReportsDirectory);
        var fileName = $"bug-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt";
        var path = Path.Combine(ReportsDirectory, fileName);
        await File.WriteAllTextAsync(path, report, Encoding.UTF8, cancellationToken);
        return path;
    }

    private async Task TryPostReportAsync(string endpoint, string report, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { app = "minivibe", report }),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Email draft and local report are the reliable fallback paths.
        }
    }

    private static void TryOpenEmailDraft(string email, string report, string reportPath)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        try
        {
            var body = report.Length > 6000 ? report[..6000] + "\n\n[report was shortened]\n" : report;
            body += $"\n\nLocal report: {reportPath}";
            var uri = "mailto:" + Uri.EscapeDataString(email)
                + "?subject=" + Uri.EscapeDataString("minivibe launcher bug report")
                + "&body=" + Uri.EscapeDataString(body);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Some systems have no default mail client.
        }
    }

    private static void TryCopyToClipboard(string report)
    {
        try
        {
            System.Windows.Clipboard.SetText(report);
        }
        catch
        {
            // Clipboard can be unavailable during shutdown or from non-UI exception paths.
        }
    }
}
