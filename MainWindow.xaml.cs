using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerLauncher.Models;
using ServerLauncher.Services;
using MediaColor = System.Windows.Media.Color;
using WpfComboBox = System.Windows.Controls.ComboBox;
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
    private bool _visualControlsReady;

    public MainWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = _fileStatuses;
        InitializeVisualControls();
        ApplyVisualSettings();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            _settings = await _settingsService.LoadAsync();
            BindSettingsToUi();
            ApplyVisualSettings();
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
        _settings.VisualTheme = SelectedComboValue(ThemeBox, _settings.VisualTheme);
        _settings.AccentColor = SelectedComboValue(AccentBox, _settings.AccentColor);
        _settings.DynamicBackground = DynamicBackgroundCheckBox.IsChecked == true;
        _settings.CompactMode = CompactModeCheckBox.IsChecked == true;
        _settings.PanelOpacity = Math.Clamp(PanelOpacitySlider.Value, 0.72, 1);

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
        SelectComboValue(ThemeBox, _settings.VisualTheme);
        SelectComboValue(AccentBox, _settings.AccentColor);
        DynamicBackgroundCheckBox.IsChecked = _settings.DynamicBackground;
        CompactModeCheckBox.IsChecked = _settings.CompactMode;
        PanelOpacitySlider.Value = Math.Clamp(_settings.PanelOpacity, 0.72, 1);
        PlayerPreviewText.Text = string.IsNullOrWhiteSpace(_settings.PlayerName) ? "wawgame" : _settings.PlayerName;
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
        PackPreviewText.Text = $"{_manifest.PackVersion} / Minecraft {_manifest.MinecraftVersion}";
        LoaderPreviewText.Text = $"{_manifest.Loader} {_manifest.LoaderVersion}";
        PlayerPreviewText.Text = string.IsNullOrWhiteSpace(_settings.PlayerName) ? "wawgame" : _settings.PlayerName;
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

    private void InitializeVisualControls()
    {
        ThemeBox.ItemsSource = new[]
        {
            "Obsidian",
            "Midnight",
            "Forest",
            "Frost"
        };
        AccentBox.ItemsSource = new[]
        {
            "Crimson",
            "Emerald",
            "Cyan",
            "Gold",
            "Violet"
        };
        _visualControlsReady = true;
    }

    private async void VisualSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_visualControlsReady)
        {
            return;
        }

        await SaveVisualSettingsPreviewAsync();
    }

    private async Task SaveVisualSettingsPreviewAsync()
    {
        _settings.VisualTheme = SelectedComboValue(ThemeBox, _settings.VisualTheme);
        _settings.AccentColor = SelectedComboValue(AccentBox, _settings.AccentColor);
        _settings.DynamicBackground = DynamicBackgroundCheckBox.IsChecked == true;
        _settings.CompactMode = CompactModeCheckBox.IsChecked == true;
        _settings.PanelOpacity = Math.Clamp(PanelOpacitySlider.Value, 0.72, 1);
        ApplyVisualSettings();
        await _settingsService.SaveAsync(_settings);
    }

    private void ApplyVisualSettings()
    {
        var palette = ThemePalette.From(_settings.VisualTheme, _settings.PanelOpacity);
        var accent = AccentPalette.From(_settings.AccentColor);
        var resources = Resources;

        resources["AppBackgroundBrush"] = new SolidColorBrush(palette.Background);
        resources["SidebarBrush"] = new SolidColorBrush(palette.Sidebar);
        resources["SurfaceBrush"] = new SolidColorBrush(ColorWithOpacity(palette.Surface, _settings.PanelOpacity));
        resources["SurfaceAltBrush"] = new SolidColorBrush(palette.SurfaceAlt);
        resources["BorderBrush"] = new SolidColorBrush(palette.Border);
        resources["TextBrush"] = new SolidColorBrush(palette.Text);
        resources["MutedBrush"] = new SolidColorBrush(palette.Muted);
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentTextBrush"] = new SolidColorBrush(System.Windows.Media.Colors.White);
        resources["ProgressTrackBrush"] = new SolidColorBrush(palette.ProgressTrack);
        resources["AtmosphereBrush"] = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(palette.GlowA, 0),
                new(palette.Background, 0.55),
                new(palette.GlowB, 1)
            }
        };

        DynamicLayer.Visibility = _settings.DynamicBackground ? Visibility.Visible : Visibility.Collapsed;
        ContentShell.Margin = _settings.CompactMode ? new Thickness(18, 0, 0, 0) : new Thickness(26, 0, 0, 0);
    }

    private static MediaColor ColorWithOpacity(MediaColor color, double opacity)
    {
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return color;
    }

    private static string SelectedComboValue(WpfComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem?.ToString() ?? fallback;
    }

    private static void SelectComboValue(WpfComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<object>().FirstOrDefault(item => item.ToString() == value)
            ?? comboBox.Items.Cast<object>().FirstOrDefault();
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

internal sealed record ThemePalette(
    MediaColor Background,
    MediaColor Sidebar,
    MediaColor Surface,
    MediaColor SurfaceAlt,
    MediaColor Border,
    MediaColor Text,
    MediaColor Muted,
    MediaColor ProgressTrack,
    MediaColor GlowA,
    MediaColor GlowB)
{
    public static ThemePalette From(string theme, double opacity)
    {
        return theme switch
        {
            "Midnight" => new(
                MediaColor.FromRgb(5, 10, 24),
                MediaColor.FromRgb(11, 18, 36),
                MediaColor.FromRgb(22, 31, 52),
                MediaColor.FromRgb(28, 39, 64),
                MediaColor.FromRgb(53, 68, 98),
                MediaColor.FromRgb(240, 246, 255),
                MediaColor.FromRgb(161, 174, 198),
                MediaColor.FromRgb(34, 45, 72),
                MediaColor.FromRgb(14, 34, 74),
                MediaColor.FromRgb(45, 24, 84)),
            "Forest" => new(
                MediaColor.FromRgb(7, 17, 14),
                MediaColor.FromRgb(13, 29, 23),
                MediaColor.FromRgb(25, 44, 36),
                MediaColor.FromRgb(31, 58, 47),
                MediaColor.FromRgb(55, 85, 72),
                MediaColor.FromRgb(241, 248, 243),
                MediaColor.FromRgb(166, 188, 176),
                MediaColor.FromRgb(34, 59, 49),
                MediaColor.FromRgb(15, 48, 37),
                MediaColor.FromRgb(38, 38, 20)),
            "Frost" => new(
                MediaColor.FromRgb(234, 240, 246),
                MediaColor.FromRgb(248, 250, 252),
                MediaColor.FromRgb(255, 255, 255),
                MediaColor.FromRgb(237, 242, 247),
                MediaColor.FromRgb(202, 213, 226),
                MediaColor.FromRgb(20, 28, 38),
                MediaColor.FromRgb(88, 101, 118),
                MediaColor.FromRgb(218, 227, 237),
                MediaColor.FromRgb(216, 236, 246),
                MediaColor.FromRgb(234, 222, 246)),
            _ => new(
                MediaColor.FromRgb(9, 10, 15),
                MediaColor.FromRgb(17, 19, 26),
                MediaColor.FromRgb(26, 29, 39),
                MediaColor.FromRgb(37, 41, 55),
                MediaColor.FromRgb(52, 58, 76),
                MediaColor.FromRgb(245, 247, 250),
                MediaColor.FromRgb(168, 175, 189),
                MediaColor.FromRgb(43, 48, 64),
                MediaColor.FromRgb(23, 26, 37),
                MediaColor.FromRgb(34, 16, 23))
        };
    }
}

internal static class AccentPalette
{
    public static MediaColor From(string accent)
    {
        return accent switch
        {
            "Emerald" => MediaColor.FromRgb(56, 190, 132),
            "Cyan" => MediaColor.FromRgb(54, 180, 214),
            "Gold" => MediaColor.FromRgb(218, 166, 70),
            "Violet" => MediaColor.FromRgb(148, 112, 222),
            _ => MediaColor.FromRgb(216, 76, 91)
        };
    }
}
