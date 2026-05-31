using System.IO;
using System.Text;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class BugReportService
{
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
        CancellationToken cancellationToken = default)
    {
        var report = CreateReport(exception, context, settings, manifest);
        var reportPath = await SaveReportAsync(report, cancellationToken);
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
