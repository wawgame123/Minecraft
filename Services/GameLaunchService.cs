using System.Diagnostics;
using System.IO;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class GameLaunchService
{
    public IReadOnlyList<string> ValidateReady(LauncherManifest manifest, LauncherSettings settings)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            issues.Add("Введите ник игрока.");
        }
        else if (!IsValidMinecraftName(settings.PlayerName))
        {
            issues.Add("Ник должен быть 3-16 символов: латиница, цифры или _.");
        }

        if (string.IsNullOrWhiteSpace(TryResolveJava(settings)))
        {
            issues.Add("Java не найдена. Установите Java 21 или положите runtime\\bin\\java.exe рядом с лаунчером/сборкой.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Launch.MainClass))
        {
            issues.Add("В manifest.json не заполнен launch.mainClass.");
        }

        foreach (var relativePath in manifest.Launch.Classpath.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.Combine(settings.InstallDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                issues.Add($"Не найден файл classpath: {relativePath}");
            }
        }

        return issues;
    }

    public Process Start(LauncherManifest manifest, LauncherSettings settings)
    {
        var issues = ValidateReady(manifest, settings);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException("Minecraft не готов к запуску: " + string.Join("; ", issues.Take(4)));
        }

        var javaPath = TryResolveJava(settings);
        var args = BuildArguments(manifest, settings);
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath!,
            Arguments = args,
            WorkingDirectory = settings.InstallDirectory,
            UseShellExecute = false
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");
    }

    private static bool IsValidMinecraftName(string playerName)
    {
        var trimmed = playerName.Trim();
        return trimmed.Length is >= 3 and <= 16
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string? TryResolveJava(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath))
        {
            return settings.JavaPath;
        }

        foreach (var candidate in LocalJavaCandidates(settings))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
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

        return FindOnPath("java.exe") ?? FindOnPath("java") ?? FindInstalledJava();
    }

    private static IEnumerable<string> LocalJavaCandidates(LauncherSettings settings)
    {
        yield return Path.Combine(settings.InstallDirectory, "runtime", "bin", "java.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "runtime", "bin", "java.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "java", "bin", "java.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string? FindInstalledJava()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path));

        foreach (var root in roots)
        {
            foreach (var vendorRoot in JavaVendorRoots(root))
            {
                var java = FindJavaUnderDirectory(vendorRoot);
                if (!string.IsNullOrWhiteSpace(java))
                {
                    return java;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> JavaVendorRoots(string programFiles)
    {
        yield return Path.Combine(programFiles, "Java");
        yield return Path.Combine(programFiles, "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFiles, "BellSoft");
        yield return Path.Combine(programFiles, "Zulu");
        yield return Path.Combine(programFiles, "Amazon Corretto");
    }

    private static string? FindJavaUnderDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var direct = Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var candidate = Path.Combine(child, "bin", "java.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
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
