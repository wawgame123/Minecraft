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
    private const string PlayerNamePlaceholder = "Введите ник";

    private readonly SettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly FileSyncService _fileSyncService = new();
    private readonly GameLaunchService _gameLaunchService = new();
    private readonly BugReportService _bugReportService = new();
    private readonly LauncherUpdateService _launcherUpdateService = new();
    private readonly ObservableCollection<FileStatusItem> _fileStatuses = [];

    private LauncherSettings _settings = new();
    private LauncherManifest? _manifest;
    private CancellationTokenSource? _operationCts;
    private bool _visualControlsReady;
    private bool _gameFilesReady;
    private bool _bindingSettings;
    private bool _syncingPlayerName;

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
            if (await CheckLauncherUpdateAsync())
            {
                return;
            }

            await ShowPendingPatchNotesAsync();
            await LoadManifestAsync(repairMissingGameFiles: false);
        });
    }

    private async Task<bool> CheckLauncherUpdateAsync()
    {
        try
        {
            var progress = new Progress<string>(message => ProgressText.Text = message);
            var updating = await _launcherUpdateService.CheckAndApplyUpdateAsync(_settings, progress, CurrentToken());
            if (updating)
            {
                ProgressText.Text = "Обновление скачано. Перезапускаю лаунчер...";
                System.Windows.Application.Current.Shutdown();
                return true;
            }
        }
        catch (Exception ex)
        {
            await _bugReportService.HandleAsync(ex, "Launcher self-update", _settings, _manifest, openEmailDraft: false);
            ProgressText.Text = "Автообновление недоступно, продолжаю запуск.";
        }

        return false;
    }

    private async Task ShowPendingPatchNotesAsync()
    {
        var currentVersion = CurrentLauncherVersion();
        var update = await _launcherUpdateService.ReadPendingPatchNotesAsync(CurrentToken());
        if (update is null && !string.Equals(_settings.LastSeenLauncherVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            update = await _launcherUpdateService.LoadPatchNotesForVersionAsync(
                currentVersion,
                CurrentToken());
        }

        if (update is null)
        {
            return;
        }

        var notes = update.Notes.Count > 0
            ? string.Join(Environment.NewLine, update.Notes.Select(note => "- " + note))
            : "Патч установлен без описания изменений.";

        System.Windows.MessageBox.Show(
            $"Лаунчер обновлен до версии {update.Version}.{Environment.NewLine}{Environment.NewLine}{notes}",
            "Описание обновления",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _settings.LastSeenLauncherVersion = currentVersion;
        await _settingsService.SaveAsync(_settings);
    }

    private async Task LoadManifestAsync(bool repairMissingGameFiles = false)
    {
        SetBusy(true, "Загружаю manifest.json...");
        _manifest = await _manifestService.LoadAsync(LauncherEndpoints.ManifestUrl, CurrentToken());
        RenderManifest();
        await EnsureGameFilesReadyAsync(repairMissingGameFiles);
    }

    private async Task EnsureGameFilesReadyAsync(bool repairMissingFiles)
    {
        var statuses = await VerifyFilesAsync(downloadMissingFiles: false);
        var outdated = CountOutdated(statuses);
        if (outdated == 0)
        {
            _gameFilesReady = true;
            UpdateLaunchReadinessStatus();
            return;
        }

        _gameFilesReady = false;
        if (!repairMissingFiles)
        {
            return;
        }

        MainStatusText.Text = $"Не хватает файлов для запуска: {outdated}. Докачиваю сборку...";
        SidebarStatusText.Text = "Докачиваю сборку";
        statuses = await VerifyFilesAsync(downloadMissingFiles: true);
        outdated = CountOutdated(statuses);
        _gameFilesReady = outdated == 0;

        if (!_gameFilesReady)
        {
            throw new InvalidOperationException($"Не удалось подготовить сборку: {outdated} файлов не прошли проверку.");
        }

        UpdateLaunchReadinessStatus();
    }

    private async Task<IReadOnlyList<FileStatusItem>> VerifyFilesAsync(bool downloadMissingFiles)
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

        var outdated = CountOutdated(statuses);
        MainStatusText.Text = outdated switch
        {
            0 => "Сборка готова к запуску. Пользовательские моды не тронуты.",
            _ when downloadMissingFiles => $"После восстановления осталось проблемных файлов: {outdated}.",
            _ => $"Нужно установить файлов: {outdated}. Нажмите \"Установить\"."
        };
        SidebarStatusText.Text = outdated == 0 ? "Готово" : "Есть обновления";
        UpdatePrimaryButtonState();
        return statuses;
    }

    private async Task SaveSettingsFromUiAsync()
    {
        _settings.InstallDirectory = InstallDirectoryBox.Text.Trim();
        _settings.EnableShaders = ShadersCheckBox.IsChecked == true;
        _settings.JavaPath = JavaPathBox.Text.Trim();
        _settings.PlayerName = CurrentPlayerName();
        _settings.ExtraLaunchArguments = ExtraArgsBox.Text.Trim();
        _settings.EnableAutoUpdate = AutoUpdateCheckBox.IsChecked == true;
        _settings.BugReportEmail = BugReportEmailBox.Text.Trim();
        _settings.BugReportEndpoint = BugReportEndpointBox.Text.Trim();
        _settings.OpenEmailOnError = OpenEmailOnErrorCheckBox.IsChecked == true;
        _settings.VisualTheme = SelectedComboValue(ThemeBox, _settings.VisualTheme);
        _settings.AccentColor = SelectedComboValue(AccentBox, _settings.AccentColor);
        SaveCustomColorsFromUi();
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
        _bindingSettings = true;
        try
        {
            InstallDirectoryBox.Text = _settings.InstallDirectory;
            ShadersCheckBox.IsChecked = _settings.EnableShaders;
            RamBox.Text = _settings.RamMb.ToString();
            JavaPathBox.Text = _settings.JavaPath;
            SyncPlayerNameText(_settings.PlayerName);
            ExtraArgsBox.Text = _settings.ExtraLaunchArguments;
            AutoUpdateCheckBox.IsChecked = _settings.EnableAutoUpdate;
            BugReportEmailBox.Text = _settings.BugReportEmail;
            BugReportEndpointBox.Text = _settings.BugReportEndpoint;
            OpenEmailOnErrorCheckBox.IsChecked = _settings.OpenEmailOnError;
            SelectComboValue(ThemeBox, _settings.VisualTheme);
            SelectComboValue(AccentBox, _settings.AccentColor);
            BindCustomColorBoxes();
            DynamicBackgroundCheckBox.IsChecked = _settings.DynamicBackground;
            CompactModeCheckBox.IsChecked = _settings.CompactMode;
            PanelOpacitySlider.Value = Math.Clamp(_settings.PanelOpacity, 0.72, 1);
            UpdatePlayerPreview();
        }
        finally
        {
            _bindingSettings = false;
        }
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
        UpdatePlayerPreview();
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
                await LoadManifestAsync(repairMissingGameFiles: false);
            }

            if (!_gameFilesReady)
            {
                await InstallGameFilesAsync();
                return;
            }

            var readinessStatuses = await VerifyFilesAsync(downloadMissingFiles: false);
            var readinessOutdated = CountOutdated(readinessStatuses);
            _gameFilesReady = readinessOutdated == 0;
            if (!_gameFilesReady)
            {
                MainStatusText.Text = $"Нужно установить файлов: {readinessOutdated}. Нажмите \"Установить\".";
                SidebarStatusText.Text = "Требуется установка";
                UpdatePrimaryButtonState();
                return;
            }

            var launchIssues = _gameLaunchService.ValidateReady(_manifest!, _settings);
            if (launchIssues.Count > 0)
            {
                throw new InvalidOperationException("Minecraft не готов к запуску: " + string.Join("; ", launchIssues.Take(4)));
            }

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
                await LoadManifestAsync(repairMissingGameFiles: false);
            }

            var statuses = await VerifyFilesAsync(downloadMissingFiles: false);
            _gameFilesReady = CountOutdated(statuses) == 0;
            UpdateLaunchReadinessStatus();
            UpdatePrimaryButtonState();
        });
    }

    private async void RefreshManifestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            await LoadManifestAsync(repairMissingGameFiles: false);
        });
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            MainStatusText.Text = "Настройки сохранены локально.";
            SidebarStatusText.Text = "Настройки сохранены";
            await LoadManifestAsync(repairMissingGameFiles: false);
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

    private void PlayerNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPlayerName)
        {
            return;
        }

        if (sender is System.Windows.Controls.TextBox textBox)
        {
            SyncPlayerNameText(textBox.Text);
        }

        UpdatePlayerPreview();
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
        if (!_visualControlsReady || _bindingSettings)
        {
            return;
        }

        await SaveVisualSettingsPreviewAsync();
    }

    private async Task SaveVisualSettingsPreviewAsync()
    {
        _settings.VisualTheme = SelectedComboValue(ThemeBox, _settings.VisualTheme);
        _settings.AccentColor = SelectedComboValue(AccentBox, _settings.AccentColor);
        SaveCustomColorsFromUi();
        _settings.DynamicBackground = DynamicBackgroundCheckBox.IsChecked == true;
        _settings.CompactMode = CompactModeCheckBox.IsChecked == true;
        _settings.PanelOpacity = Math.Clamp(PanelOpacitySlider.Value, 0.72, 1);
        ApplyVisualSettings();
        await _settingsService.SaveAsync(_settings);
    }

    private void ApplyVisualSettings()
    {
        var palette = ThemePalette.From(_settings.VisualTheme, _settings.PanelOpacity);
        var background = ReadColor(_settings.CustomBackgroundColor, palette.Background);
        var sidebar = ReadColor(_settings.CustomSidebarColor, palette.Sidebar);
        var surface = ReadColor(_settings.CustomSurfaceColor, palette.Surface);
        var surfaceAlt = ReadColor(_settings.CustomSurfaceColor, palette.SurfaceAlt);
        var border = ReadColor(_settings.CustomBorderColor, palette.Border);
        var text = ReadColor(_settings.CustomTextColor, palette.Text);
        var muted = ReadColor(_settings.CustomMutedTextColor, palette.Muted);
        var accent = ReadColor(_settings.CustomAccentColor, AccentPalette.From(_settings.AccentColor));
        var resources = Resources;

        resources["AppBackgroundBrush"] = new SolidColorBrush(background);
        resources["SidebarBrush"] = new SolidColorBrush(sidebar);
        resources["SurfaceBrush"] = new SolidColorBrush(ColorWithOpacity(surface, _settings.PanelOpacity));
        resources["SurfaceAltBrush"] = new SolidColorBrush(surfaceAlt);
        resources["BorderBrush"] = new SolidColorBrush(border);
        resources["TextBrush"] = new SolidColorBrush(text);
        resources["MutedBrush"] = new SolidColorBrush(muted);
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentTextBrush"] = new SolidColorBrush(System.Windows.Media.Colors.White);
        resources["ProgressTrackBrush"] = new SolidColorBrush(surfaceAlt);
        resources["AtmosphereBrush"] = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(accent, 0),
                new(background, 0.55),
                new(surface, 1)
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

    private void BindCustomColorBoxes()
    {
        CustomBackgroundColorBox.Text = _settings.CustomBackgroundColor;
        CustomSidebarColorBox.Text = _settings.CustomSidebarColor;
        CustomSurfaceColorBox.Text = _settings.CustomSurfaceColor;
        CustomBorderColorBox.Text = _settings.CustomBorderColor;
        CustomTextColorBox.Text = _settings.CustomTextColor;
        CustomMutedTextColorBox.Text = _settings.CustomMutedTextColor;
        CustomAccentColorBox.Text = _settings.CustomAccentColor;
    }

    private void SaveCustomColorsFromUi()
    {
        _settings.CustomBackgroundColor = NormalizeHexColor(CustomBackgroundColorBox.Text);
        _settings.CustomSidebarColor = NormalizeHexColor(CustomSidebarColorBox.Text);
        _settings.CustomSurfaceColor = NormalizeHexColor(CustomSurfaceColorBox.Text);
        _settings.CustomBorderColor = NormalizeHexColor(CustomBorderColorBox.Text);
        _settings.CustomTextColor = NormalizeHexColor(CustomTextColorBox.Text);
        _settings.CustomMutedTextColor = NormalizeHexColor(CustomMutedTextColorBox.Text);
        _settings.CustomAccentColor = NormalizeHexColor(CustomAccentColorBox.Text);
    }

    private static string NormalizeHexColor(string value)
    {
        var color = value.Trim();
        if (string.IsNullOrWhiteSpace(color))
        {
            return "";
        }

        if (!color.StartsWith('#'))
        {
            color = "#" + color;
        }

        return color.Length is 4 or 7 or 9 ? color.ToUpperInvariant() : "";
    }

    private static MediaColor ReadColor(string value, MediaColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
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
            var reportPath = await _bugReportService.HandleAsync(
                ex,
                "Launcher operation",
                _settings,
                _manifest,
                openEmailDraft: true,
                CurrentToken());
            MainStatusText.Text = ex.Message;
            SidebarStatusText.Text = "Ошибка";
            System.Windows.MessageBox.Show(
                $"{ex.Message}\n\nОтчет сохранен:\n{reportPath}\n\nТекст отчета также скопирован в буфер обмена.",
                "Ошибка лаунчера",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

    private static int CountOutdated(IEnumerable<FileStatusItem> statuses)
    {
        return statuses.Count(item => item.Status != FileSyncService.StatusCurrent);
    }

    private static string CurrentLauncherVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void UpdateLaunchReadinessStatus()
    {
        if (!_gameFilesReady || _manifest is null)
        {
            return;
        }

        var launchIssues = _gameLaunchService.ValidateReady(_manifest, _settings);
        if (launchIssues.Count == 0)
        {
            MainStatusText.Text = "Minecraft готов к запуску.";
            SidebarStatusText.Text = "Готово";
            return;
        }

        MainStatusText.Text = "Файлы скачаны, но запуск требует настройки: " + string.Join("; ", launchIssues.Take(3));
        SidebarStatusText.Text = "Нужна настройка";
    }

    private async Task InstallGameFilesAsync()
    {
        var statuses = await VerifyFilesAsync(downloadMissingFiles: true);
        var outdated = CountOutdated(statuses);
        _gameFilesReady = outdated == 0;
        UpdatePrimaryButtonState();

        if (!_gameFilesReady)
        {
            throw new InvalidOperationException($"Не удалось установить сборку: {outdated} файлов не прошли проверку.");
        }

        UpdateLaunchReadinessStatus();
        MainStatusText.Text = "Сборка установлена. Теперь можно нажать \"Играть\".";
        SidebarStatusText.Text = "Установлено";
    }

    private void UpdatePlayerPreview()
    {
        var playerName = CurrentPlayerName();
        PlayerPreviewText.Text = string.IsNullOrWhiteSpace(playerName)
            ? PlayerNamePlaceholder
            : $"В игре будет: {playerName}";
    }

    private string CurrentPlayerName()
    {
        return HomePlayerNameBox.Text.Trim();
    }

    private void SyncPlayerNameText(string playerName)
    {
        _syncingPlayerName = true;
        try
        {
            if (HomePlayerNameBox.Text != playerName)
            {
                HomePlayerNameBox.Text = playerName;
            }

            if (PlayerNameBox.Text != playerName)
            {
                PlayerNameBox.Text = playerName;
            }
        }
        finally
        {
            _syncingPlayerName = false;
        }
    }

    private void UpdatePrimaryButtonState()
    {
        PlayButton.Content = _gameFilesReady ? "Играть" : "Установить";
    }

    private void SetBusy(bool busy, string message)
    {
        ProgressBar.IsIndeterminate = busy;
        ProgressText.Text = message;
        PlayButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        RefreshManifestButton.IsEnabled = !busy;
        if (!busy)
        {
            UpdatePrimaryButtonState();
        }
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
