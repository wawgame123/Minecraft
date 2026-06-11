using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinivibeInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
    }
}

internal sealed class InstallerForm : Form
{
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/launcher/update.json";

    private readonly TextBox _installPathBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly CheckBox _desktopShortcutBox = new();
    private readonly CheckBox _launchAfterInstallBox = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public InstallerForm()
    {
        Text = "minivibe installer";
        ClientSize = new Size(680, 390);
        MinimumSize = new Size(640, 390);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = TryLoadIcon();

        var title = new Label
        {
            Text = "Установка minivibe",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };

        var hint = new Label
        {
            Text = "Выберите папку установки лаунчера. По умолчанию используется %APPDATA%\\.minivibe.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        _installPathBox.Text = DefaultInstallPath();
        _installPathBox.Dock = DockStyle.Fill;
        _installPathBox.Margin = new Padding(0, 4, 10, 4);

        _browseButton.Text = "Выбрать";
        _browseButton.AutoSize = true;
        _browseButton.MinimumSize = new Size(130, 36);
        _browseButton.Dock = DockStyle.Fill;
        _browseButton.Margin = new Padding(0);
        _browseButton.Click += BrowseButton_Click;

        _desktopShortcutBox.Text = "Создать ярлык на рабочем столе";
        _desktopShortcutBox.Checked = true;
        _desktopShortcutBox.AutoSize = true;
        _desktopShortcutBox.Margin = new Padding(0, 12, 0, 4);

        _launchAfterInstallBox.Text = "Запустить после установки";
        _launchAfterInstallBox.Checked = true;
        _launchAfterInstallBox.AutoSize = true;
        _launchAfterInstallBox.Margin = new Padding(0, 4, 0, 14);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Height = 18;
        _progressBar.Margin = new Padding(0, 0, 0, 10);

        _statusLabel.Text = "Готово к установке.";
        _statusLabel.AutoSize = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Margin = new Padding(0, 0, 12, 0);

        _installButton.Text = "Установить";
        _installButton.AutoSize = true;
        _installButton.MinimumSize = new Size(160, 42);
        _installButton.Dock = DockStyle.Fill;
        _installButton.Margin = new Padding(0);
        _installButton.Click += InstallButton_Click;

        var pathLayout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathLayout.Controls.Add(_installPathBox, 0, 0);
        pathLayout.Controls.Add(_browseButton, 1, 0);

        var footerLayout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 10, 0, 0)
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.Controls.Add(_statusLabel, 0, 0);
        footerLayout.Controls.Add(_installButton, 1, 0);

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(26, 22, 26, 22)
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.Controls.Add(title, 0, 0);
        rootLayout.Controls.Add(hint, 0, 1);
        rootLayout.Controls.Add(pathLayout, 0, 2);
        rootLayout.Controls.Add(_desktopShortcutBox, 0, 3);
        rootLayout.Controls.Add(_launchAfterInstallBox, 0, 4);
        rootLayout.Controls.Add(_progressBar, 0, 5);
        rootLayout.Controls.Add(footerLayout, 0, 7);
        Controls.Add(rootLayout);
    }

    private static string DefaultInstallPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minivibe");
    }

    private static Icon? TryLoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "launcher-icon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : null;
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку установки minivibe",
            SelectedPath = Directory.Exists(_installPathBox.Text) ? _installPathBox.Text : DefaultInstallPath(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installPathBox.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        await InstallAsync();
    }

    private async Task InstallAsync()
    {
        var installPath = _installPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(this, "Выберите папку установки.", "minivibe", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "Загружаю сведения об обновлении...");
        try
        {
            Directory.CreateDirectory(installPath);
            var update = await LoadUpdateManifestAsync();
            var tempZip = Path.Combine(Path.GetTempPath(), $"Minivibe-{update.Version}.zip");

            SetBusy(true, $"Скачиваю minivibe {update.Version}...");
            await DownloadFileAsync(update.Url, tempZip);

            SetBusy(true, "Проверяю SHA-256...");
            var actualHash = await Sha256Async(tempZip);
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHA-256 скачанного архива не совпал с update.json.");
            }

            SetBusy(true, "Распаковываю лаунчер...");
            ExtractZip(tempZip, installPath);
            var launcherPath = Path.Combine(installPath, "Minivibe.exe");
            if (!File.Exists(launcherPath))
            {
                throw new InvalidOperationException("После установки Minivibe.exe не найден.");
            }

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "minivibe.lnk"),
                launcherPath,
                installPath);

            if (_desktopShortcutBox.Checked)
            {
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "minivibe.lnk"),
                    launcherPath,
                    installPath);
            }

            SetBusy(false, "Установка завершена.");
            if (_launchAfterInstallBox.Checked)
            {
                Process.Start(new ProcessStartInfo(launcherPath)
                {
                    WorkingDirectory = installPath,
                    UseShellExecute = true
                });
            }

            MessageBox.Show(this, "minivibe установлен.", "minivibe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetBusy(false, "Ошибка установки.");
            MessageBox.Show(this, ex.Message, "Ошибка установки", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<LauncherUpdateManifest> LoadUpdateManifestAsync()
    {
        await using var stream = await _httpClient.GetStreamAsync(UpdateManifestUrl);
        return await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream)
            ?? throw new InvalidOperationException("update.json пустой или поврежден.");
    }

    private async Task DownloadFileAsync(string url, string path)
    {
        await using var input = await _httpClient.GetStreamAsync(url);
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractZip(string zipPath, string destinationPath)
    {
        var destinationFullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(destinationFullPath);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationFullPath, entry.FullName));
            if (!targetPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Архив содержит недопустимый путь.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetPath;
        shortcut.Save();
    }

    private void SetBusy(bool busy, string status)
    {
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        _installButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _installPathBox.Enabled = !busy;
        _statusLabel.Text = status;
    }

    private sealed class LauncherUpdateManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";
    }
}
