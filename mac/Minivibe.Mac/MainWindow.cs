using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ServerLauncher.Models;
using ServerLauncher.Services;

namespace Minivibe.Mac;

public sealed class MainWindow : Window
{
    private readonly MacSettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly FileSyncService _fileSyncService = new();
    private readonly MinecraftRuntimeService _runtimeService = new();
    private readonly MacGameLaunchService _launchService = new();
    private readonly MacSkinService _skinService = new();

    private readonly TextBox _playerNameBox = new();
    private readonly TextBox _installDirectoryBox = new();
    private readonly TextBox _ramBox = new();
    private readonly CheckBox _shadersBox = new();
    private readonly Button _loadButton = new();
    private readonly Button _installButton = new();
    private readonly Button _playButton = new();
    private readonly Button _browseButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _manifestText = new();
    private readonly TextBox _consoleBox = new();

    private LauncherSettings _settings = new();
    private LauncherManifest? _manifest;
    private CancellationTokenSource _operation = new();

    public MainWindow()
    {
        Title = "minivibe mac";
        Width = 980;
        Height = 680;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#10131b"));

        Content = BuildContent();
        Opened += async (_, _) => await InitializeAsync();
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("320,*")
        };

        var title = new TextBlock
        {
            Text = "minivibe",
            FontSize = 34,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };
        Grid.SetColumnSpan(title, 2);
        root.Children.Add(title);

        var settingsPanel = new Border
        {
            Margin = new Thickness(0, 64, 18, 0),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#181d29")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2c3448")),
            BorderThickness = new Thickness(1),
            Child = BuildSettingsPanel()
        };
        Grid.SetRow(settingsPanel, 1);
        root.Children.Add(settingsPanel);

        var mainPanel = new Grid
        {
            Margin = new Thickness(0, 64, 0, 0),
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };
        Grid.SetRow(mainPanel, 1);
        Grid.SetColumn(mainPanel, 1);
        root.Children.Add(mainPanel);

        var statusPanel = new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#181d29")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2c3448")),
            BorderThickness = new Thickness(1),
            Child = BuildStatusPanel()
        };
        mainPanel.Children.Add(statusPanel);

        _progressBar.Height = 8;
        _progressBar.Margin = new Thickness(0, 16, 0, 14);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.IsIndeterminate = false;
        Grid.SetRow(_progressBar, 1);
        mainPanel.Children.Add(_progressBar);

        _consoleBox.AcceptsReturn = true;
        _consoleBox.IsReadOnly = true;
        _consoleBox.TextWrapping = TextWrapping.Wrap;
        _consoleBox.Background = new SolidColorBrush(Color.Parse("#0c0f16"));
        _consoleBox.Foreground = new SolidColorBrush(Color.Parse("#dbe7ff"));
        _consoleBox.BorderBrush = new SolidColorBrush(Color.Parse("#2c3448"));
        _consoleBox.FontFamily = FontFamily.Parse("Menlo,Consolas,monospace");
        _consoleBox.FontSize = 13;
        Grid.SetRow(_consoleBox, 2);
        mainPanel.Children.Add(_consoleBox);

        return root;
    }

    private Control BuildSettingsPanel()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Header("Профиль"));
        panel.Children.Add(Label("Ник"));
        panel.Children.Add(_playerNameBox);

        panel.Children.Add(Label("Папка игры"));
        var pathRow = new DockPanel();
        _browseButton.Content = "Выбрать";
        _browseButton.Width = 94;
        _browseButton.Margin = new Thickness(8, 0, 0, 0);
        _browseButton.Click += BrowseButton_Click;
        DockPanel.SetDock(_browseButton, Dock.Right);
        pathRow.Children.Add(_browseButton);
        pathRow.Children.Add(_installDirectoryBox);
        panel.Children.Add(pathRow);

        panel.Children.Add(Label("RAM, MB"));
        _ramBox.Text = "4096";
        panel.Children.Add(_ramBox);

        _shadersBox.Content = "Включить шейдеры";
        _shadersBox.Foreground = Brushes.White;
        panel.Children.Add(_shadersBox);

        _loadButton.Content = "Загрузить manifest";
        _loadButton.Click += async (_, _) => await RunGuardedAsync(LoadManifestAsync);
        panel.Children.Add(_loadButton);

        _installButton.Content = "Установить / проверить";
        _installButton.Click += async (_, _) => await RunGuardedAsync(InstallAsync);
        panel.Children.Add(_installButton);

        _playButton.Content = "Играть";
        _playButton.Click += async (_, _) => await RunGuardedAsync(PlayAsync);
        panel.Children.Add(_playButton);

        return panel;
    }

    private Control BuildStatusPanel()
    {
        var panel = new StackPanel { Spacing = 8 };
        _manifestText.Text = "Manifest не загружен";
        _manifestText.Foreground = new SolidColorBrush(Color.Parse("#9fb0cf"));
        _statusText.Text = "Готов к настройке.";
        _statusText.Foreground = Brushes.White;
        _statusText.FontSize = 18;
        panel.Children.Add(_manifestText);
        panel.Children.Add(_statusText);
        return panel;
    }

    private async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync();
        _playerNameBox.Text = _settings.PlayerName;
        _installDirectoryBox.Text = _settings.InstallDirectory;
        _ramBox.Text = _settings.RamMb.ToString();
        _shadersBox.IsChecked = _settings.EnableShaders;
        await RunGuardedAsync(LoadManifestAsync);
    }

    private async void BrowseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку Minecraft",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].Path.IsFile)
        {
            _installDirectoryBox.Text = folders[0].Path.LocalPath;
        }
    }

    private async Task LoadManifestAsync()
    {
        SetBusy("Загружаю manifest...");
        _manifest = await _manifestService.LoadAsync(LauncherEndpoints.ManifestUrl, _operation.Token);
        _manifestText.Text = $"{_manifest.PackVersion} / Minecraft {_manifest.MinecraftVersion} / {_manifest.Loader} {_manifest.LoaderVersion}";
        _statusText.Text = "Manifest загружен.";
    }

    private async Task InstallAsync()
    {
        RequireManifest();
        await SaveSettingsAsync();

        SetBusy("Проверяю файлы сборки...");
        var progress = new Progress<string>(SetStatus);
        var statuses = await _fileSyncService.VerifyAndRepairAsync(
            _manifest!,
            _settings,
            downloadMissingFiles: true,
            verifyHashes: true,
            progress,
            _operation.Token);

        var outdated = statuses.Count(status => status.Status != FileSyncService.StatusCurrent);
        if (outdated > 0)
        {
            throw new InvalidOperationException($"После установки осталось проблемных файлов: {outdated}.");
        }

        SetBusy("Проверяю Java 21...");
        var javaPath = await _launchService.EnsureCompatibleJavaAsync(_settings, progress, _operation.Token);

        SetBusy("Готовлю Minecraft runtime...");
        await _runtimeService.EnsureAsync(_manifest!, _settings, javaPath, progress, _operation.Token);
        _statusText.Text = "Сборка готова. Можно запускать.";
    }

    private async Task PlayAsync()
    {
        RequireManifest();
        await SaveSettingsAsync();
        await _skinService.SaveOfflineSkinsConfigAsync(_settings, _operation.Token);

        SetBusy("Проверяю Java и runtime...");
        var progress = new Progress<string>(SetStatus);
        var javaPath = await _launchService.EnsureCompatibleJavaAsync(_settings, progress, _operation.Token);
        var runtime = await _runtimeService.EnsureAsync(_manifest!, _settings, javaPath, progress, _operation.Token);

        AppendLog("Запускаю Minecraft...");
        _launchService.Start(
            _manifest!,
            _settings,
            runtime,
            line => AppendLog(line),
            line => AppendLog("[ERR] " + line),
            code => AppendLog($"Minecraft завершился с кодом {code}."));
        _statusText.Text = "Minecraft запущен.";
    }

    private async Task SaveSettingsAsync()
    {
        _settings.PlayerName = (_playerNameBox.Text ?? "").Trim();
        _settings.InstallDirectory = string.IsNullOrWhiteSpace(_installDirectoryBox.Text)
            ? MacSettingsService.DefaultInstallDirectory()
            : _installDirectoryBox.Text.Trim();
        _settings.EnableShaders = _shadersBox.IsChecked == true;
        _settings.SkinServerUrl = LauncherSettings.DefaultSkinServerUrl;
        _settings.EnableSkinServer = true;

        if (int.TryParse(_ramBox.Text, out var ram))
        {
            _settings.RamMb = Math.Clamp(ram, 1024, 32768);
        }

        await _settingsService.SaveAsync(_settings);
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            SetEnabled(false);
            _operation.Cancel();
            _operation.Dispose();
            _operation = new CancellationTokenSource();
            await action();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
            AppendLog("[ERR] " + ex);
        }
        finally
        {
            _progressBar.IsIndeterminate = false;
            SetEnabled(true);
        }
    }

    private void SetBusy(string message)
    {
        _progressBar.IsIndeterminate = true;
        SetStatus(message);
    }

    private void SetStatus(string message)
    {
        Dispatcher.UIThread.Post(() => _statusText.Text = message);
    }

    private void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _consoleBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
            _consoleBox.CaretIndex = _consoleBox.Text?.Length ?? 0;
        });
    }

    private void SetEnabled(bool enabled)
    {
        _loadButton.IsEnabled = enabled;
        _installButton.IsEnabled = enabled;
        _playButton.IsEnabled = enabled;
        _browseButton.IsEnabled = enabled;
        _playerNameBox.IsEnabled = enabled;
        _installDirectoryBox.IsEnabled = enabled;
        _ramBox.IsEnabled = enabled;
        _shadersBox.IsEnabled = enabled;
    }

    private void RequireManifest()
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException("Сначала загрузите manifest.");
        }
    }

    private static TextBlock Header(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse("#9fb0cf"))
        };
    }
}
