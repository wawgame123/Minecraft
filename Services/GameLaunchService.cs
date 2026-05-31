using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class GameLaunchService
{
    private const int RequiredJavaMajorVersion = 21;
    private const string PortableJavaFolderName = "java-21";
    private const string JavaDownloadUrl = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";

    private readonly HttpClient _httpClient = new();

    public IReadOnlyList<string> ValidateReady(
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime? runtime = null)
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
            issues.Add(JavaValidationMessage(settings));
        }

        var mainClass = !string.IsNullOrWhiteSpace(runtime?.MainClass)
            ? runtime.MainClass
            : manifest.Launch.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            issues.Add("В manifest.json не заполнен launch.mainClass.");
        }

        var shouldValidateManifestClasspath = runtime is null || string.IsNullOrWhiteSpace(runtime.MainClass);
        foreach (var relativePath in manifest.Launch.Classpath.Where(path => shouldValidateManifestClasspath && !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.Combine(settings.InstallDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                issues.Add($"Не найден файл classpath: {relativePath}");
            }
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

        var javaPath = TryResolveJava(settings);
        var args = BuildArguments(manifest, settings, runtime);
        var captureOutput = outputReceived is not null || errorReceived is not null;
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath!,
            Arguments = args,
            WorkingDirectory = settings.InstallDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = outputReceived is not null,
            RedirectStandardError = errorReceived is not null,
            CreateNoWindow = captureOutput
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
            throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");
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
            var version = JavaMajorVersion(javaPath);
            progress?.Report(version is null
                ? $"Java {RequiredJavaMajorVersion}+ найдена: {javaPath}"
                : $"Java {version} найдена: {javaPath}");
            return javaPath;
        }

        var detectedJava = BestDetectedJava(settings);
        if (detectedJava is not null)
        {
            progress?.Report($"Найдена Java {detectedJava.Value.Version}, но нужна Java {RequiredJavaMajorVersion}+. Скачиваю runtime...");
        }
        else
        {
            progress?.Report($"Java {RequiredJavaMajorVersion} не найдена, скачиваю runtime...");
        }

        javaPath = await DownloadPortableJavaAsync(settings, progress, cancellationToken);
        progress?.Report($"Java {RequiredJavaMajorVersion} готова.");
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

    private static IEnumerable<string> LocalJavaCandidates(LauncherSettings settings)
    {
        yield return Path.Combine(settings.InstallDirectory, "runtime", PortableJavaFolderName, "bin", "java.exe");
        yield return Path.Combine(settings.InstallDirectory, "runtime", "bin", "java.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "runtime", "bin", "java.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "runtime", PortableJavaFolderName, "bin", "java.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "java", "bin", "java.exe");

        var installRuntime = Path.Combine(settings.InstallDirectory, "runtime");
        foreach (var candidate in FindJavaUnderDirectory(installRuntime))
        {
            yield return candidate;
        }

        var appRuntime = Path.Combine(AppContext.BaseDirectory, "runtime");
        foreach (var candidate in FindJavaUnderDirectory(appRuntime))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> JavaCandidates(LauncherSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in LocalJavaCandidates(settings))
        {
            if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath) && seen.Add(Path.GetFullPath(settings.JavaPath)))
        {
            yield return settings.JavaPath;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaFromHome = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaFromHome) && seen.Add(Path.GetFullPath(javaFromHome)))
            {
                yield return javaFromHome;
            }
        }

        foreach (var candidate in FindOnPath("java.exe").Concat(FindOnPath("java")).Concat(FindInstalledJava()))
        {
            if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> FindOnPath(string fileName)
    {
        var candidates = new List<string>();
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return candidates;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    candidates.Add(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return candidates;
    }

    private static IEnumerable<string> FindInstalledJava()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        }.Where(path => !string.IsNullOrWhiteSpace(path));

        foreach (var root in roots)
        {
            foreach (var vendorRoot in JavaVendorRoots(root))
            {
                foreach (var java in FindJavaUnderDirectoryRecursive(vendorRoot, maxDepth: 4))
                {
                    yield return java;
                }
            }
        }

        var minecraftRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "runtime");
        foreach (var java in FindJavaUnderDirectoryRecursive(minecraftRuntime, maxDepth: 7))
        {
            yield return java;
        }

        foreach (var java in FindRegistryJava())
        {
            yield return java;
        }
    }

    private static IEnumerable<string> JavaVendorRoots(string programFiles)
    {
        yield return Path.Combine(programFiles, "Java");
        yield return Path.Combine(programFiles, "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFiles, "BellSoft");
        yield return Path.Combine(programFiles, "Zulu");
        yield return Path.Combine(programFiles, "Amazon Corretto");
        yield return Path.Combine(programFiles, "Oracle");
        yield return Path.Combine(programFiles, "Android");
    }

    private static IEnumerable<string> FindJavaUnderDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var candidates = new List<string>();

        try
        {
            candidates.Add(Path.Combine(directory, "bin", "java.exe"));
            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(child, "bin", "java.exe"));
            }
        }
        catch
        {
            yield break;
        }

        foreach (var candidate in candidates.Where(File.Exists))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> FindJavaUnderDirectoryRecursive(string directory, int maxDepth)
    {
        var results = new List<string>();
        if (!Directory.Exists(directory))
        {
            return results;
        }

        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, maxDepth));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var directJava = Path.Combine(current.Directory, "bin", "java.exe");
            if (File.Exists(directJava))
            {
                results.Add(directJava);
            }

            if (current.Depth <= 0)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current.Directory).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push((child, current.Depth - 1));
                }
            }
            catch
            {
                // Ignore inaccessible directories.
            }
        }

        return results;
    }

    private static IEnumerable<string> FindRegistryJava()
    {
        var results = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var subKeyName in JavaRegistrySubKeys())
                    {
                        using var key = baseKey.OpenSubKey(subKeyName);
                        if (key is null)
                        {
                            continue;
                        }

                        AddJavaHome(results, key);
                        if (key.GetValue("CurrentVersion") is string currentVersion)
                        {
                            using var currentKey = key.OpenSubKey(currentVersion);
                            AddJavaHome(results, currentKey);
                        }

                        foreach (var versionName in key.GetSubKeyNames())
                        {
                            using var versionKey = key.OpenSubKey(versionName);
                            AddJavaHome(results, versionKey);
                        }
                    }
                }
                catch
                {
                    // Registry layout differs between vendors; skip unreadable views.
                }
            }
        }

        return results.Where(File.Exists);
    }

    private static IEnumerable<string> JavaRegistrySubKeys()
    {
        yield return @"SOFTWARE\JavaSoft\JDK";
        yield return @"SOFTWARE\JavaSoft\JRE";
        yield return @"SOFTWARE\JavaSoft\Java Development Kit";
        yield return @"SOFTWARE\JavaSoft\Java Runtime Environment";
        yield return @"SOFTWARE\Eclipse Adoptium\JDK";
        yield return @"SOFTWARE\Eclipse Adoptium\JRE";
        yield return @"SOFTWARE\Adoptium\JDK";
        yield return @"SOFTWARE\Adoptium\JRE";
    }

    private static void AddJavaHome(List<string> results, RegistryKey? key)
    {
        if (key?.GetValue("JavaHome") is string javaHome && !string.IsNullOrWhiteSpace(javaHome))
        {
            results.Add(ToJavaExecutablePath(javaHome));
        }

        if (key?.GetValue("Path") is string javaPath && !string.IsNullOrWhiteSpace(javaPath))
        {
            results.Add(ToJavaExecutablePath(javaPath));
        }
    }

    private static string ToJavaExecutablePath(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var directCandidate = Path.Combine(path, "java.exe");
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        return Path.Combine(path, "bin", "java.exe");
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
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
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

        if (major == 1 && int.TryParse(match.Groups["minor"].Value, out var legacyMajor))
        {
            return legacyMajor;
        }

        return major;
    }

    private static string JavaValidationMessage(LauncherSettings settings)
    {
        var detected = BestDetectedJava(settings);

        if (detected is not null)
        {
            return $"Найдена Java {detected.Value.Version}, но для Minecraft 1.21.1 нужна Java {RequiredJavaMajorVersion}+.";
        }

        return $"Java {RequiredJavaMajorVersion}+ не найдена. Лаунчер скачает runtime автоматически при запуске игры.";
    }

    private static (string Path, int Version)? BestDetectedJava(LauncherSettings settings)
    {
        var detected = JavaCandidates(settings)
            .Select(candidate => new
            {
                Path = candidate,
                Version = JavaMajorVersion(candidate)
            })
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();

        return detected is null ? null : (detected.Path, detected.Version!.Value);
    }

    private async Task<string> DownloadPortableJavaAsync(
        LauncherSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = Path.Combine(settings.InstallDirectory, "runtime");
        var finalRoot = Path.Combine(runtimeRoot, PortableJavaFolderName);
        var finalJava = Path.Combine(finalRoot, "bin", "java.exe");

        if (File.Exists(finalJava) && JavaMajorVersion(finalJava) >= RequiredJavaMajorVersion)
        {
            return finalJava;
        }

        var workRoot = Path.Combine(runtimeRoot, ".java-download");
        var zipPath = Path.Combine(workRoot, "java21.zip");
        var extractRoot = Path.Combine(workRoot, "extract");

        EnsureInside(settings.InstallDirectory, runtimeRoot);
        EnsureInside(settings.InstallDirectory, finalRoot);
        EnsureInside(settings.InstallDirectory, workRoot);

        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, true);
        }

        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(extractRoot);

        try
        {
            await DownloadFileWithProgressAsync(JavaDownloadUrl, zipPath, progress, cancellationToken);
            progress?.Report("Распаковываю Java 21...");
            ZipFile.ExtractToDirectory(zipPath, extractRoot, true);

            var extractedJava = Directory.EnumerateFiles(extractRoot, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(extractedJava))
            {
                throw new InvalidOperationException("В скачанном runtime Java не найден bin\\java.exe.");
            }

            var extractedHome = Directory.GetParent(Path.GetDirectoryName(extractedJava)!)!.FullName;
            if (Directory.Exists(finalRoot))
            {
                Directory.Delete(finalRoot, true);
            }

            Directory.Move(extractedHome, finalRoot);

            if (!File.Exists(finalJava) || JavaMajorVersion(finalJava) < RequiredJavaMajorVersion)
            {
                throw new InvalidOperationException($"Скачанная Java не подходит. Нужна Java {RequiredJavaMajorVersion}+.");
            }

            return finalJava;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Не удалось автоматически скачать Java {RequiredJavaMajorVersion}. Установите Java {RequiredJavaMajorVersion} вручную или положите runtime\\bin\\java.exe рядом со сборкой. {ex.Message}", ex);
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
                // Temporary files can be cleaned on the next run.
            }
        }
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

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Round(completedBytes * 100d / totalBytes.Value);
                progress?.Report($"Скачивание Java 21 {Math.Clamp(percent, 0, 100)}%");
            }
            else
            {
                progress?.Report($"Скачивание Java 21 {completedBytes / 1024 / 1024} МБ");
            }
        }
    }

    private static void EnsureInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Недопустимый путь runtime: {path}");
        }
    }

    private static string BuildArguments(
        LauncherManifest manifest,
        LauncherSettings settings,
        MinecraftRuntime runtime)
    {
        var useRuntimeLaunch = !string.IsNullOrWhiteSpace(runtime.MainClass);
        List<string> manifestClasspath = useRuntimeLaunch
            ? []
            : manifest.Launch.Classpath
                .Select(path => Path.Combine(settings.InstallDirectory, path.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
        IEnumerable<string> runtimeClientJar = string.IsNullOrWhiteSpace(runtime.ClientJarPath)
            ? []
            : new[] { runtime.ClientJarPath };
        var classpath = runtime.ClasspathFiles
            .Concat(runtimeClientJar)
            .Concat(manifestClasspath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Quote);

        var args = new List<string>
        {
            $"-Xmx{Math.Clamp(settings.RamMb, 1024, 32768)}M",
            "-Djava.library.path=" + Quote(runtime.NativesDirectory),
            "-Dminecraft.launcher.brand=minivibe",
            "-Dminecraft.launcher.version=0.1"
        };

        args.AddRange(runtime.JvmArgs
            .Concat(manifest.Launch.JvmArgs)
            .Select(arg => QuoteIfNeededArgument(ExpandToken(arg, manifest, settings, runtime))));

        if (classpath.Any())
        {
            args.Add("-cp");
            args.Add(Quote(string.Join(Path.PathSeparator, classpath.Select(Unquote))));
        }

        args.Add(useRuntimeLaunch ? runtime.MainClass : manifest.Launch.MainClass);
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
        if (args.Any(arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        args.Add(key);
        args.Add(value);
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

    private static string Quote(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static string QuoteIfNeededArgument(string value)
    {
        return value.Contains(' ') || value.Contains('\\') ? Quote(value) : value;
    }

    private static string Unquote(string value)
    {
        return value.Trim('"');
    }
}
