using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerLauncher.Models;
using ServerLauncher.Services;

namespace Minivibe.Mac;

internal sealed class MacGameLaunchService
{
    private const int RequiredJavaMajorVersion = 21;
    private const string PortableJavaFolderName = "java-21";

    private readonly HttpClient _httpClient = new();

    public IReadOnlyList<string> ValidateReady(
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime? runtime = null)
    {
        var issues = new List<string>();

        if (!IsValidMinecraftName(settings.PlayerName))
        {
            issues.Add("Введите ник 3-16 символов: латиница, цифры или _.");
        }

        if (string.IsNullOrWhiteSpace(TryResolveJava(settings)))
        {
            issues.Add($"Java {RequiredJavaMajorVersion}+ не найдена.");
        }

        var mainClass = !string.IsNullOrWhiteSpace(runtime?.MainClass)
            ? runtime.MainClass
            : manifest.Launch.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            issues.Add("В manifest.json не заполнен launch.mainClass.");
        }

        if (runtime is not null)
        {
            foreach (var runtimeFile in runtime.ClasspathFiles
                .Append(runtime.ClientJarPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                if (!File.Exists(runtimeFile))
                {
                    issues.Add($"Не найдена библиотека Minecraft: {Path.GetFileName(runtimeFile)}");
                }
            }
        }

        return issues;
    }

    public Process Start(
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime runtime,
        Action<string>? outputReceived = null,
        Action<string>? errorReceived = null,
        Action<int>? processExited = null)
    {
        var issues = ValidateReady(manifest, settings, runtime);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException("Minecraft не готов к запуску: " + string.Join("; ", issues.Take(4)));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = TryResolveJava(settings)!,
            Arguments = BuildArguments(manifest, settings, runtime),
            WorkingDirectory = settings.InstallDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = outputReceived is not null,
            RedirectStandardError = errorReceived is not null
        };

        if (outputReceived is not null)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
        }

        if (errorReceived is not null)
        {
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = processExited is not null
        };

        if (outputReceived is not null)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    outputReceived(eventArgs.Data);
                }
            };
        }

        if (errorReceived is not null)
        {
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    errorReceived(eventArgs.Data);
                }
            };
        }

        if (processExited is not null)
        {
            process.Exited += (_, _) => processExited(process.ExitCode);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить Minecraft.");
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
            // Some systems do not allow changing process priority.
        }

        if (outputReceived is not null)
        {
            process.BeginOutputReadLine();
        }

        if (errorReceived is not null)
        {
            process.BeginErrorReadLine();
        }

        return process;
    }

    public async Task<string> EnsureCompatibleJavaAsync(
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var javaPath = TryResolveJava(settings);
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            progress?.Report($"Java {JavaMajorVersion(javaPath) ?? RequiredJavaMajorVersion} найдена.");
            return javaPath;
        }

        progress?.Report("Java 21 не найдена, скачиваю runtime для macOS...");
        javaPath = await DownloadPortableJavaAsync(settings, progress, cancellationToken);
        progress?.Report("Java 21 готова.");
        return javaPath;
    }

    private static bool IsValidMinecraftName(string playerName)
    {
        var trimmed = playerName.Trim();
        return trimmed.Length is >= 3 and <= 16
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string? TryResolveJava(LauncherSettings settings)
    {
        return JavaCandidates(settings)
            .FirstOrDefault(candidate => JavaMajorVersion(candidate) >= RequiredJavaMajorVersion);
    }

    private static IEnumerable<string> JavaCandidates(LauncherSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in LocalJavaCandidates(settings)
            .Concat(JavaHomeCandidates())
            .Concat(FindOnPath("java")))
        {
            if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> LocalJavaCandidates(LauncherSettings settings)
    {
        yield return Path.Combine(settings.InstallDirectory, "runtime", PortableJavaFolderName, "Contents", "Home", "bin", "java");
        yield return Path.Combine(settings.InstallDirectory, "runtime", PortableJavaFolderName, "bin", "java");
        yield return Path.Combine(settings.InstallDirectory, "runtime", "bin", "java");
        yield return Path.Combine(AppContext.BaseDirectory, "runtime", PortableJavaFolderName, "Contents", "Home", "bin", "java");
        yield return Path.Combine(AppContext.BaseDirectory, "runtime", "bin", "java");

        if (!string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            yield return settings.JavaPath;
        }
    }

    private static IEnumerable<string> JavaHomeCandidates()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return Path.Combine(javaHome, "bin", "java");
        }

        var detectedHome = RunJavaHome();
        if (!string.IsNullOrWhiteSpace(detectedHome))
        {
            yield return Path.Combine(detectedHome, "bin", "java");
        }
    }

    private static string? RunJavaHome()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/libexec/java_home",
                Arguments = $"-v {RequiredJavaMajorVersion}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static int? JavaMajorVersion(string javaPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
                return null;
            }

            return ParseJavaMajorVersion(output);
        }
        catch
        {
            return null;
        }
    }

    private static int? ParseJavaMajorVersion(string output)
    {
        var match = Regex.Match(output, "version\\s+\"(?<major>\\d+)(?:\\.(?<minor>\\d+))?");
        if (!match.Success)
        {
            match = Regex.Match(output, "openjdk\\s+(?<major>\\d+)(?:\\.(?<minor>\\d+))?", RegexOptions.IgnoreCase);
        }

        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major))
        {
            return null;
        }

        return major == 1 && int.TryParse(match.Groups["minor"].Value, out var legacyMajor)
            ? legacyMajor
            : major;
    }

    private async Task<string> DownloadPortableJavaAsync(
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = Path.Combine(settings.InstallDirectory, "runtime");
        var finalRoot = Path.Combine(runtimeRoot, PortableJavaFolderName);
        var finalJava = Path.Combine(finalRoot, "Contents", "Home", "bin", "java");
        var fallbackJava = Path.Combine(finalRoot, "bin", "java");
        if (File.Exists(finalJava) && JavaMajorVersion(finalJava) >= RequiredJavaMajorVersion)
        {
            return finalJava;
        }

        Directory.CreateDirectory(runtimeRoot);
        var workRoot = Path.Combine(runtimeRoot, ".java-download");
        var archivePath = Path.Combine(workRoot, "java21.tar.gz");
        var extractRoot = Path.Combine(workRoot, "extract");
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, true);
        }

        Directory.CreateDirectory(extractRoot);
        try
        {
            await DownloadFileWithProgressAsync(JavaDownloadUrl(), archivePath, progress, cancellationToken);
            progress?.Report("Распаковываю Java 21...");
            await using (var file = File.OpenRead(archivePath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, extractRoot, overwriteFiles: true);
            }

            var java = Directory.EnumerateFiles(extractRoot, "java", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(java))
            {
                throw new InvalidOperationException("В скачанном runtime Java не найден bin/java.");
            }

            var home = Directory.GetParent(Path.GetDirectoryName(java)!)!.FullName;
            if (home.EndsWith($"{Path.DirectorySeparatorChar}Home", StringComparison.Ordinal))
            {
                home = Directory.GetParent(Directory.GetParent(home)!.FullName)!.FullName;
            }

            if (Directory.Exists(finalRoot))
            {
                Directory.Delete(finalRoot, true);
            }

            Directory.Move(home, finalRoot);
            if (File.Exists(finalJava) && JavaMajorVersion(finalJava) >= RequiredJavaMajorVersion)
            {
                return finalJava;
            }

            if (File.Exists(fallbackJava) && JavaMajorVersion(fallbackJava) >= RequiredJavaMajorVersion)
            {
                return fallbackJava;
            }

            throw new InvalidOperationException($"Скачанная Java не подходит. Нужна Java {RequiredJavaMajorVersion}+.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, true);
                }
            }
            catch
            {
                // Temporary files can be cleaned later.
            }
        }
    }

    private static string JavaDownloadUrl()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        return $"https://api.adoptium.net/v3/binary/latest/21/ga/mac/{arch}/jre/hotspot/normal/eclipse";
    }

    private async Task DownloadFileWithProgressAsync(
        string url,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var completedBytes = 0L;
        var buffer = new byte[1024 * 128];

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            completedBytes += read;
            progress?.Report(totalBytes is > 0
                ? $"Скачивание Java 21 {Math.Clamp((int)Math.Round(completedBytes * 100d / totalBytes.Value), 0, 100)}%"
                : $"Скачивание Java 21 {completedBytes / 1024 / 1024} MB");
        }
    }

    private static string BuildArguments(
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime runtime)
    {
        var classpath = runtime.ClasspathFiles
            .Concat(string.IsNullOrWhiteSpace(runtime.ClientJarPath) ? [] : new[] { runtime.ClientJarPath })
            .Distinct(StringComparer.Ordinal)
            .Select(Quote);

        var args = new List<string>
        {
            $"-Xmx{Math.Clamp(settings.RamMb, 1024, 32768)}M",
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:InitiatingHeapOccupancyPercent=15",
            "-Djava.library.path=" + Quote(runtime.NativesDirectory),
            "-Dminecraft.launcher.brand=minivibe",
            "-Dminecraft.launcher.version=" + CurrentLauncherVersion()
        };

        args.AddRange(runtime.JvmArgs
            .Concat(manifest.Launch.JvmArgs)
            .Select(arg => QuoteIfNeededArgument(ExpandToken(arg, manifest, settings, runtime))));

        if (classpath.Any())
        {
            args.Add("-cp");
            args.Add(Quote(string.Join(Path.PathSeparator, classpath.Select(Unquote))));
        }

        args.Add(!string.IsNullOrWhiteSpace(runtime.MainClass) ? runtime.MainClass : manifest.Launch.MainClass);
        var gameArgs = manifest.Launch.GameArgs
            .Concat(runtime.GameArgs)
            .Select(arg => ExpandToken(arg, manifest, settings, runtime))
            .ToList();
        AddMissingGameArg(gameArgs, "--assetsDir", runtime.AssetsDirectory);
        AddMissingGameArg(gameArgs, "--assetIndex", runtime.AssetIndex);
        AddMissingGameArg(gameArgs, "--uuid", OfflinePlayerUuid(settings.PlayerName));
        AddMissingGameArg(gameArgs, "--accessToken", "0");
        AddMissingGameArg(gameArgs, "--userType", "legacy");
        AddMissingGameArg(gameArgs, "--versionType", manifest.Loader);
        args.AddRange(gameArgs.Select(QuoteIfNeededArgument));

        if (!string.IsNullOrWhiteSpace(settings.ExtraLaunchArguments))
        {
            args.Add(settings.ExtraLaunchArguments);
        }

        return string.Join(" ", args);
    }

    private static void AddMissingGameArg(List<string> args, string key, string value)
    {
        if (!args.Any(arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase)))
        {
            args.Add(key);
            args.Add(value);
        }
    }

    private static string ExpandToken(
        string value,
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime runtime)
    {
        return value
            .Replace("${game_directory}", settings.InstallDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${player_name}", settings.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("${player_uuid}", OfflinePlayerUuid(settings.PlayerName), StringComparison.OrdinalIgnoreCase)
            .Replace("${assets_root}", runtime.AssetsDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${assets_index_name}", runtime.AssetIndex, StringComparison.OrdinalIgnoreCase)
            .Replace("${natives_directory}", runtime.NativesDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${library_directory}", runtime.LibrariesDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${classpath_separator}", Path.PathSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("${version_name}", runtime.VersionId, StringComparison.OrdinalIgnoreCase)
            .Replace("${loader}", manifest.Loader, StringComparison.OrdinalIgnoreCase)
            .Replace("${loader_version}", manifest.LoaderVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string OfflinePlayerUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string CurrentLauncherVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string Quote(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static string QuoteIfNeededArgument(string value)
    {
        return value.Contains(' ') ? Quote(value) : value;
    }

    private static string Unquote(string value)
    {
        return value.Trim('"');
    }
}
