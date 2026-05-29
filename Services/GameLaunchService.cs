using System.Diagnostics;
using System.IO;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class GameLaunchService
{
    public Process Start(LauncherManifest manifest, LauncherSettings settings)
    {
        var javaPath = ResolveJava(settings);
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new InvalidOperationException("Java не найдена. Укажите путь к java.exe в настройках.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Launch.MainClass))
        {
            throw new InvalidOperationException(
                "Файлы проверены, но запуск Minecraft не настроен. Добавьте в manifest.json блок launch с mainClass, classpath, jvmArgs и gameArgs.");
        }

        var args = BuildArguments(manifest, settings);
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = args,
            WorkingDirectory = settings.InstallDirectory,
            UseShellExecute = false
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");
    }

    private static string ResolveJava(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath))
        {
            return settings.JavaPath;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaFromHome = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaFromHome))
            {
                return javaFromHome;
            }
        }

        return "java";
    }

    private static string BuildArguments(LauncherManifest manifest, LauncherSettings settings)
    {
        var classpath = manifest.Launch.Classpath
            .Select(path => Path.Combine(settings.InstallDirectory, path.Replace('/', Path.DirectorySeparatorChar)))
            .Select(Quote);

        var args = new List<string>
        {
            $"-Xmx{Math.Clamp(settings.RamMb, 1024, 32768)}M"
        };

        args.AddRange(manifest.Launch.JvmArgs.Select(arg => ExpandToken(arg, manifest, settings)));

        if (manifest.Launch.Classpath.Count > 0)
        {
            args.Add("-cp");
            args.Add(Quote(string.Join(Path.PathSeparator, classpath.Select(Unquote))));
        }

        args.Add(manifest.Launch.MainClass);
        args.AddRange(manifest.Launch.GameArgs.Select(arg => ExpandToken(arg, manifest, settings)));

        if (!string.IsNullOrWhiteSpace(settings.ExtraLaunchArguments))
        {
            args.Add(settings.ExtraLaunchArguments);
        }

        return string.Join(" ", args);
    }

    private static string ExpandToken(string value, LauncherManifest manifest, LauncherSettings settings)
    {
        return value
            .Replace("${game_directory}", settings.InstallDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${player_name}", settings.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("${version_name}", manifest.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
            .Replace("${loader}", manifest.Loader, StringComparison.OrdinalIgnoreCase)
            .Replace("${loader_version}", manifest.LoaderVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static string Unquote(string value)
    {
        return value.Trim('"');
    }
}
