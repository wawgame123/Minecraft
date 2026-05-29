using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ServerLauncher.Models;
using ServerLauncher.Services;
using WinForms = System.Windows.Forms;

namespace ServerLauncher;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly FileSyncService _fileSyncService = new();
    private readonly GameLaunchService _gameLaunchService = new();
    private readonly ObservableCollection<FileStatusItem> _fileStatuses = [];

    private LauncherSettings _settings = new();
    private LauncherManifest? _manifest;
    private CancellationTokenSource? _operationCts;

    public MainWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = _fileStatuses;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            _settings = await _settingsService.LoadAsync();
            BindSettingsToUi();
            await LoadManifestAsync();
        });
    }

    private async Task LoadManifestAsync()
    {
        SetBusy(true, "Загружаю manifest.json...");
        _manifest = await _manifestService.LoadAsync(_settings.ManifestUrl, CurrentToken());
        RenderManifest();
        await VerifyFilesAsync(downloadMissingFiles: false);
    }

    private async Task VerifyFilesAsync(bool downloadMissingFiles)
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException("Сначала загрузите manifest.json.");
        }

        SetBusy(true, downloadMissingFiles ? "Проверяю и восстанавливаю файлы..." : "Проверяю файлы...");
        var progress = new Progress<string>(message => ProgressText.Text = message);
        var statuses = await _fileSyncService.VerifyAndRepairAsync(_manifest, _settings, downloadMissingFiles, progress, CurrentToken());
        _fileStatuses.Clear();

        foreach (var item in statuses)
        {
            _fileStatuses.Add(item);
        }

        var outdated = statuses.Count(item => item.Status != "Актуален");
        MainStatusText.Text = outdated == 0
            ? "Сборка актуальна. Пользовательские моды не тронуты."
            : $"Нужно обновить файлов: {outdated}. Нажмите \"Проверить файлы\" для восстановления.";
        SidebarStatusText.Text = outdated == 0 ? "Готово" : "Есть обновления";
    }

    private async Task SaveSettingsFromUiAsync()
    {
        _settings.ManifestUrl = ManifestUrlBox.Text.Trim();
        _settings.InstallDirectory = InstallDirectoryBox.Text.Trim();
        _settings.EnableShaders = ShadersCheckBox.IsChecked == true;
        _settings.JavaPath = JavaPathBox.Text.Trim();
        _settings.PlayerName = PlayerNameBox.Text.Trim();
        _settings.ExtraLaunchArguments = ExtraArgsBox.Text.Trim();

        if (int.TryParse(RamBox.Text.Trim(), out var ram))
        {
            _settings.RamMb = Math.Clamp(ram, 1024, 32768);
        }

        await _settingsService.SaveAsync(_settings);
        RenderManifest();
    }

    private void BindSettingsToUi()
    {
        ManifestUrlBox.Text = _settings.ManifestUrl;
        InstallDirectoryBox.Text = _settings.InstallDirectory;
        ShadersCheckBox.IsChecked = _settings.EnableShaders;
        RamBox.Text = _settings.RamMb.ToString();
        JavaPathBox.Text = _settings.JavaPath;
        PlayerNameBox.Text = _settings.PlayerName;
        ExtraArgsBox.Text = _settings.ExtraLaunchArguments;
    }

    private void RenderManifest()
    {
        if (_manifest is null)
        {
            return;
        }

        Title = $"{_manifest.ServerName} Launcher";
        ServerNameText.Text = _manifest.ServerName;
        ServerVersionText.Text = $"Сборка {_manifest.PackVersion} | Minecraft {_manifest.MinecraftVersion}";
        PackInfoText.Text = $"Версия сборки: {_manifest.PackVersion}\nMinecraft: {_manifest.MinecraftVersion}";
        LoaderInfoText.Text = $"Loader: {_manifest.Loader} {_manifest.LoaderVersion}";
        InstallInfoText.Text = $"Папка игры: {_settings.InstallDirectory}";
        SidebarStatusText.Text = "Manifest загружен";

        var changelog = _manifest.Changelog.Count > 0
            ? _manifest.Changelog
            : ["История обновлений пока пуста."];
        HomeChangelogList.ItemsSource = changelog.Take(4);

        var news = _manifest.News.Count > 0
            ? _manifest.News
            : _manifest.Changelog.Count > 0 ? _manifest.Changelog : ["Новостей пока нет."];
        NewsList.ItemsSource = news;

        var managedFiles = FileSyncService.GetManagedFiles(_manifest, _settings.EnableShaders)
            .Select(file => new FileStatusItem
            {
                Path = file.Path,
                Category = file.Category,
                Required = file.Required,
                Size = file.Size,
                Status = "Не проверен"
            });
        _fileStatuses.Clear();
        foreach (var item in managedFiles)
        {
            _fileStatuses.Add(item);
        }

        NavigateMap();
    }

    private void NavigateMap()
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            return;
        }

        try
        {
            MapBrowser.Navigate(_manifest.BlueMapUrl);
        }
        catch
        {
            ProgressText.Text = "BlueMap не удалось встроить. Используйте кнопку открытия в браузере.";
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            if (_manifest is null)
            {
                await LoadManifestAsync();
            }

            await VerifyFilesAsync(downloadMissingFiles: true);
            SetBusy(true, "Запускаю Minecraft...");
            _gameLaunchService.Start(_manifest!, _settings);
            MainStatusText.Text = "Minecraft запущен.";
            SidebarStatusText.Text = "Игра запущена";
        });
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            if (_manifest is null)
            {
                await LoadManifestAsync();
            }

            await VerifyFilesAsync(downloadMissingFiles: true);
        });
    }

    private async void RefreshManifestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            await LoadManifestAsync();
        });
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            MainStatusText.Text = "Настройки сохранены локально.";
            SidebarStatusText.Text = "Настройки сохранены";
            await LoadManifestAsync();
        });
    }

    private void BrowseInstallDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку установки игры",
            SelectedPath = Directory.Exists(InstallDirectoryBox.Text) ? InstallDirectoryBox.Text : LauncherSettings.DefaultInstallDirectory(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            InstallDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseJavaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите java.exe",
            Filter = "Java executable|java.exe|Executable files|*.exe|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPathBox.Text = dialog.FileName;
        }
    }

    private void OpenInstallDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var path = InstallDirectoryBox.Text.Trim();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            System.Windows.MessageBox.Show("В manifest.json не указана ссылка blueMapUrl.", "Карта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(_manifest.BlueMapUrl) { UseShellExecute = true });
    }

    private void HomeNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
    private void NewsNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(NewsPanel);
    private void ModsNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(ModsPanel);
    private void MapNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(MapPanel);
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);

    private void ShowPanel(UIElement panel)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        NewsPanel.Visibility = Visibility.Collapsed;
        ModsPanel.Visibility = Visibility.Collapsed;
        MapPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            _operationCts?.Cancel();
            _operationCts = new CancellationTokenSource();
            await action();
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Операция отменена.";
        }
        catch (Exception ex)
        {
            MainStatusText.Text = ex.Message;
            SidebarStatusText.Text = "Ошибка";
            System.Windows.MessageBox.Show(ex.Message, "Ошибка лаунчера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, ProgressText.Text);
        }
    }

    private CancellationToken CurrentToken()
    {
        return _operationCts?.Token ?? CancellationToken.None;
    }

    private void SetBusy(bool busy, string message)
    {
        ProgressBar.IsIndeterminate = busy;
        ProgressText.Text = message;
        PlayButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        RefreshManifestButton.IsEnabled = !busy;
    }
}
