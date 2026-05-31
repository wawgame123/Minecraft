using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerLauncher.Models;
using ServerLauncher.Services;
using MediaColor = System.Windows.Media.Color;
using WinForms = System.Windows.Forms;

namespace ServerLauncher;

public partial class MainWindow : Window
{
    private const string PlayerNamePlaceholder = "Введите ник";

    private readonly SettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly FileSyncService _fileSyncService = new();
    private readonly GameLaunchService _gameLaunchService = new();
    private readonly MinecraftRuntimeService _minecraftRuntimeService = new();
    private readonly SkinService _skinService = new();
    private readonly BugReportService _bugReportService = new();
    private readonly LauncherUpdateService _launcherUpdateService = new();
    private LauncherSettings _settings = new();
    private LauncherManifest? _manifest;
    private CancellationTokenSource? _operationCts;
    private bool _visualControlsReady;
    private bool _gameFilesReady;
    private bool _bindingSettings;
    private bool _syncingPlayerName;
    private bool _mapInitialized;
    private string? _selectedSkinPath;

    public MainWindow()
    {
        InitializeComponent();
        InitializeVisualControls();
        MapWebView.NavigationCompleted += (_, args) =>
        {
            MapFallbackPanel.Visibility = args.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
            if (!args.IsSuccess)
            {
                MapStatusText.Text = "Не удалось загрузить карту внутри лаунчера. Можно открыть ее во внешнем браузере.";
            }
        };
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
            await LoadSkinPreviewAsync(_skinService.CachedSkinPath(_settings) ?? _settings.SkinSourcePath);
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
            await _bugReportService.HandleAsync(ex, "Launcher self-update", _settings, _manifest);
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
        var statuses = await VerifyFilesAsync(downloadMissingFiles: false, verifyHashes: false);
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
        statuses = await VerifyFilesAsync(downloadMissingFiles: true, verifyHashes: true);
        outdated = CountOutdated(statuses);
        _gameFilesReady = outdated == 0;

        if (!_gameFilesReady)
        {
            throw new InvalidOperationException($"Не удалось подготовить сборку: {outdated} файлов не прошли проверку.");
        }

        UpdateLaunchReadinessStatus();
    }

    private async Task<IReadOnlyList<FileStatusItem>> VerifyFilesAsync(bool downloadMissingFiles, bool verifyHashes)
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException("Сначала загрузите manifest.json.");
        }

        SetBusy(true, downloadMissingFiles ? "Проверяю и восстанавливаю файлы..." : "Проверяю файлы...");
        var progress = new Progress<string>(message => ProgressText.Text = message);
        var statuses = await _fileSyncService.VerifyAndRepairAsync(
            _manifest,
            _settings,
            downloadMissingFiles,
            verifyHashes || downloadMissingFiles,
            progress,
            CurrentToken());

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
        var selectedInstallDirectory = InstallDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(selectedInstallDirectory))
        {
            selectedInstallDirectory = LauncherSettings.DefaultInstallDirectory();
        }

        if (!string.Equals(_settings.InstallDirectory, selectedInstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _gameFilesReady = false;
        }

        _settings.InstallDirectory = selectedInstallDirectory;
        _settings.EnableShaders = ShadersCheckBox.IsChecked == true;
        _settings.PlayerName = PlayerNameBox.Text.Trim();
        _settings.SkinSourcePath = _selectedSkinPath ?? _settings.SkinSourcePath;
        _settings.SkinServerUrl = SkinServerUrlBox.Text.Trim();
        _settings.EnableSkinServer = EnableSkinServerCheckBox.IsChecked == true;
        _settings.SkinUploadUrl = SkinUploadUrlBox.Text.Trim();
        _settings.SkinUploadSecret = SkinUploadSecretBox.Password.Trim();
        _settings.ExtraLaunchArguments = ExtraArgsBox.Text.Trim();
        _settings.EnableAutoUpdate = AutoUpdateCheckBox.IsChecked == true;
        SaveCustomColorsFromUi();
        _settings.DynamicBackground = DynamicBackgroundCheckBox.IsChecked == true;
        _settings.PanelOpacity = Math.Clamp(PanelOpacitySlider.Value, 0.72, 1);

        if (int.TryParse(RamBox.Text.Trim(), out var ram))
        {
            _settings.RamMb = Math.Clamp(ram, 1024, 32768);
        }

        await _settingsService.SaveAsync(_settings);
        await _skinService.SaveOfflineSkinsConfigAsync(_settings, CurrentToken());
        UpdatePlayerNameMode();
        UpdateSkinServerPreview();
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
            SyncPlayerNameText(_settings.PlayerName);
            _selectedSkinPath = string.IsNullOrWhiteSpace(_settings.SkinSourcePath) ? null : _settings.SkinSourcePath;
            SkinServerUrlBox.Text = _settings.SkinServerUrl;
            EnableSkinServerCheckBox.IsChecked = _settings.EnableSkinServer;
            SkinUploadUrlBox.Text = _settings.SkinUploadUrl;
            SkinUploadSecretBox.Password = _settings.SkinUploadSecret;
            ExtraArgsBox.Text = _settings.ExtraLaunchArguments;
            AutoUpdateCheckBox.IsChecked = _settings.EnableAutoUpdate;
            BindCustomColorBoxes();
            DynamicBackgroundCheckBox.IsChecked = _settings.DynamicBackground;
            PanelOpacitySlider.Value = Math.Clamp(_settings.PanelOpacity, 0.72, 1);
            UpdatePlayerPreview();
            UpdatePlayerNameMode();
            UpdateSkinStatus();
            UpdateSkinServerPreview();
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
        ServerVersionText.Text = $"Сборка {_manifest.PackVersion} | Minecraft {_manifest.MinecraftVersion} | Лаунчер {CurrentLauncherVersion()}";
        PackInfoText.Text = $"Версия сборки: {_manifest.PackVersion}\nMinecraft: {_manifest.MinecraftVersion}";
        LoaderInfoText.Text = $"Loader: {_manifest.Loader} {_manifest.LoaderVersion}";
        InstallInfoText.Text = $"Папка игры: {_settings.InstallDirectory}";
        PackPreviewText.Text = $"{_manifest.PackVersion} / Minecraft {_manifest.MinecraftVersion}";
        LoaderPreviewText.Text = $"{_manifest.Loader} {_manifest.LoaderVersion}";
        LauncherPreviewText.Text = CurrentLauncherVersion();
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

        RenderMapLink();
    }

    private void RenderMapLink()
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            MapUrlText.Text = "Ссылка на карту не указана.";
            OpenMapButton.IsEnabled = false;
            ReloadMapButton.IsEnabled = false;
            MapFallbackPanel.Visibility = Visibility.Visible;
            MapStatusText.Text = "Ссылка на карту не указана.";
            return;
        }

        MapUrlText.Text = Preferred3DMapUrl(_manifest.BlueMapUrl);
        OpenMapButton.IsEnabled = true;
        ReloadMapButton.IsEnabled = true;
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

            var readinessStatuses = await VerifyFilesAsync(downloadMissingFiles: false, verifyHashes: false);
            var readinessOutdated = CountOutdated(readinessStatuses);
            _gameFilesReady = readinessOutdated == 0;
            if (!_gameFilesReady)
            {
                MainStatusText.Text = $"Нужно установить файлов: {readinessOutdated}. Нажмите \"Установить\".";
                SidebarStatusText.Text = "Требуется установка";
                UpdatePrimaryButtonState();
                return;
            }

            SetBusy(true, "Проверяю Java 21...");
            var javaProgress = new Progress<string>(message => ProgressText.Text = message);
            var javaPath = await _gameLaunchService.EnsureCompatibleJavaAsync(_settings, javaProgress, CurrentToken());

            SetBusy(true, "Проверяю библиотеки Minecraft...");
            var runtimeProgress = new Progress<string>(message => ProgressText.Text = message);
            var minecraftRuntime = await _minecraftRuntimeService.EnsureAsync(_manifest!, _settings, javaPath, runtimeProgress, CurrentToken());

            var launchIssues = _gameLaunchService.ValidateReady(_manifest!, _settings, minecraftRuntime);
            if (launchIssues.Count > 0)
            {
                throw new InvalidOperationException("Minecraft не готов к запуску: " + string.Join("; ", launchIssues.Take(4)));
            }

            SetBusy(true, "Запускаю Minecraft...");
            var logWindow = new GameLogWindow
            {
                Owner = this
            };
            logWindow.AppendLine("Запускаю Minecraft...");
            logWindow.Show();

            var process = _gameLaunchService.Start(
                _manifest!,
                _settings,
                minecraftRuntime,
                outputReceived: logWindow.AppendLine,
                errorReceived: line => logWindow.AppendLine("[ERR] " + line),
                processExited: exitCode =>
                {
                    logWindow.MarkProcessExited(exitCode);
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MainStatusText.Text = exitCode == 0
                                ? "Minecraft закрыт."
                                : $"Minecraft завершился с кодом {exitCode}. Подробности в консоли.";
                            SidebarStatusText.Text = exitCode == 0 ? "Игра закрыта" : "Ошибка игры";
                        });
                    }
                });

            logWindow.SetProcessStarted(process.Id);
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

            var statuses = await VerifyFilesAsync(downloadMissingFiles: false, verifyHashes: true);
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

    private async void BrowseInstallDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку установки игры",
            SelectedPath = Directory.Exists(InstallDirectoryBox.Text) ? InstallDirectoryBox.Text : LauncherSettings.DefaultInstallDirectory(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            await ApplyInstallDirectoryAsync(dialog.SelectedPath);
        }
    }

    private async Task ApplyInstallDirectoryAsync(string selectedPath)
    {
        var path = selectedPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _settings.InstallDirectory = path;
        InstallDirectoryBox.Text = path;
        _gameFilesReady = false;
        RenderManifest();
        UpdatePrimaryButtonState();
        MainStatusText.Text = "Папка игры выбрана. Пользовательские configs, saves и лишние моды не удаляются.";
        SidebarStatusText.Text = "Папка выбрана";
        await _settingsService.SaveAsync(_settings);
    }

    private void ChooseColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string textBoxName }
            || FindName(textBoxName) is not System.Windows.Controls.TextBox targetBox)
        {
            return;
        }

        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        if (TryReadColor(targetBox.Text, out var currentColor))
        {
            dialog.Color = System.Drawing.Color.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B);
        }

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            targetBox.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
    }

    private async void ChooseSkinButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите скин",
                Filter = "Minecraft skin (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            SkinService.ValidateSkinImage(dialog.FileName);
            _selectedSkinPath = dialog.FileName;
            _settings.SkinSourcePath = dialog.FileName;
            await _settingsService.SaveAsync(_settings);
            UpdateSkinStatus();
            await LoadSkinPreviewAsync(dialog.FileName);
        });
    }

    private async void InstallSkinButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            var sourcePath = _selectedSkinPath ?? _settings.SkinSourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new InvalidOperationException("Выберите PNG/JPG скин.");
            }

            var installedPath = _skinService.InstallSkin(_settings, sourcePath);
            _selectedSkinPath = sourcePath;
            _settings.SkinSourcePath = sourcePath;
            await _settingsService.SaveAsync(_settings);
            await _skinService.SaveOfflineSkinsConfigAsync(_settings, CurrentToken());
            SkinStatusText.Text = $"Скин установлен для {CurrentPlayerName()}.";
            SidebarStatusText.Text = "Скин установлен";
            await LoadSkinPreviewAsync(installedPath);
        });
    }

    private async void SaveSkinServerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            SkinStatusText.Text = "Настройка сервера скинов сохранена.";
            SidebarStatusText.Text = "Скины сохранены";
        });
    }

    private async void UploadSharedSkinButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            var sourcePath = _selectedSkinPath ?? _settings.SkinSourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new InvalidOperationException("Выберите PNG/JPG скин.");
            }

            SetBusy(true, "Загружаю скин в общий каталог...");
            await _skinService.UploadSharedSkinAsync(_settings, sourcePath, CurrentToken());
            _settings.EnableSkinServer = true;
            EnableSkinServerCheckBox.IsChecked = true;
            await _settingsService.SaveAsync(_settings);
            await _skinService.SaveOfflineSkinsConfigAsync(_settings, CurrentToken());
            SkinStatusText.Text = $"Скин {CurrentPlayerName()} загружен в общий каталог.";
            SidebarStatusText.Text = "Скин загружен";
        });
    }

    private void SkinServerSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_bindingSettings)
        {
            return;
        }

        _settings.SkinServerUrl = SkinServerUrlBox.Text.Trim();
        _settings.EnableSkinServer = EnableSkinServerCheckBox.IsChecked == true;
        UpdateSkinServerPreview();
    }

    private async Task LoadSkinPreviewAsync(string? skinPath)
    {
        try
        {
            var html = File.Exists(skinPath)
                ? BuildSkinPreviewHtml("data:image/png;base64," + Convert.ToBase64String(SkinService.ReadSkinPngBytes(skinPath)))
                : BuildEmptySkinPreviewHtml();
            await SkinPreviewWebView.EnsureCoreWebView2Async();
            SkinPreviewWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            SkinStatusText.Text = "Не удалось открыть 3D-превью: " + ex.Message;
        }
    }

    private void UpdateSkinStatus()
    {
        var cached = _skinService.CachedSkinPath(_settings);
        var selected = _selectedSkinPath ?? _settings.SkinSourcePath;
        SkinStatusText.Text = File.Exists(cached)
            ? $"Установлен локальный скин для {_settings.PlayerName}."
            : File.Exists(selected)
                ? "Скин выбран, можно установить."
                : "Скин не выбран.";
    }

    private void UpdateSkinServerPreview()
    {
        var baseUrl = SkinServerUrlBox.Text.Trim().TrimEnd('/');
        SkinServerPreviewText.Text = string.IsNullOrWhiteSpace(baseUrl)
            ? "Пример: https://example.com/minivibe"
            : $"Скин игрока будет искаться по адресу: {baseUrl}/skins/%name%.png";
    }

    private static string BuildEmptySkinPreviewHtml()
    {
        return """
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{height:100%;margin:0;background:#0b0d14;color:#dce7f5;font-family:Segoe UI,Arial,sans-serif}
body{display:grid;place-items:center}.hint{opacity:.72;font-size:15px}
</style></head><body><div class="hint">Выберите PNG/JPG скин для 3D-превью</div></body></html>
""";
    }

    private static string BuildSkinPreviewHtml(string skinDataUrl)
    {
        var escapedSkin = skinDataUrl.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        return $$$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
html,body{height:100%;margin:0;overflow:hidden;background:radial-gradient(circle at 30% 20%,#20283c,#0b0d14 58%,#05060a);font-family:Segoe UI,Arial,sans-serif}
.stage{height:100%;display:grid;place-items:center;perspective:900px;cursor:grab}
.stage:active{cursor:grabbing}.model{position:relative;width:0;height:0;transform-style:preserve-3d;animation:spin 9s linear infinite}
.stage.dragging .model{animation:none}.part{position:absolute;transform-style:preserve-3d}.face{position:absolute;background-image:url('{{{escapedSkin}}}');background-size:512px 512px;image-rendering:pixelated;box-shadow:inset 0 0 0 1px rgba(255,255,255,.08)}
.overlay .face{box-shadow:inset 0 0 0 1px rgba(255,255,255,.16),0 0 8px rgba(255,255,255,.08)}
.head{transform:translate3d(-32px,-168px,0)}.body{transform:translate3d(-32px,-104px,0)}.armL{transform:translate3d(-64px,-104px,0)}.armR{transform:translate3d(32px,-104px,0)}.legL{transform:translate3d(-32px,-8px,0)}.legR{transform:translate3d(0,-8px,0)}
@keyframes spin{from{transform:rotateX(-9deg) rotateY(0deg)}to{transform:rotateX(-9deg) rotateY(360deg)}}
</style>
</head>
<body>
<div class="stage" id="stage"><div class="model" id="model"></div></div>
<script>
const S=8, skin='{{{escapedSkin}}}', model=document.getElementById('model'), stage=document.getElementById('stage');
function part(cls,w,h,d,uv,overlay=false){const p=document.createElement('div');p.className='part '+cls+(overlay?' overlay':'');model.appendChild(p);const o=overlay?1.35:0;
 const faces=[
  ['front',w,h,`translateZ(${d/2*S+o}px)`,uv.f],
  ['back',w,h,`rotateY(180deg) translateZ(${d/2*S+o}px)`,uv.b],
  ['left',d,h,`rotateY(-90deg) translateZ(${w/2*S+o}px)`,uv.l],
  ['right',d,h,`rotateY(90deg) translateZ(${w/2*S+o}px)`,uv.r],
  ['top',w,d,`rotateX(90deg) translateZ(${d/2*S+o}px)`,uv.t],
  ['bottom',w,d,`rotateX(-90deg) translateZ(${h*S-d/2*S+o}px)`,uv.o]
 ];
 for(const [n,fw,fh,tr,u] of faces){const f=document.createElement('div');f.className='face '+n;f.style.width=fw*S+'px';f.style.height=fh*S+'px';f.style.transform=tr;f.style.backgroundPosition=`-${u[0]*S}px -${u[1]*S}px`;p.appendChild(f)}
}
part('head',8,8,8,{f:[8,8],b:[24,8],l:[16,8],r:[0,8],t:[8,0],o:[16,0]});
part('body',8,12,4,{f:[20,20],b:[32,20],l:[28,20],r:[16,20],t:[20,16],o:[28,16]});
part('armL',4,12,4,{f:[36,52],b:[44,52],l:[40,52],r:[32,52],t:[36,48],o:[40,48]});
part('armR',4,12,4,{f:[44,20],b:[52,20],l:[48,20],r:[40,20],t:[44,16],o:[48,16]});
part('legL',4,12,4,{f:[20,52],b:[28,52],l:[24,52],r:[16,52],t:[20,48],o:[24,48]});
part('legR',4,12,4,{f:[4,20],b:[12,20],l:[8,20],r:[0,20],t:[4,16],o:[8,16]});
part('head',8,8,8,{f:[40,8],b:[56,8],l:[48,8],r:[32,8],t:[40,0],o:[48,0]},true);
part('body',8,12,4,{f:[20,36],b:[32,36],l:[28,36],r:[16,36],t:[20,32],o:[28,32]},true);
part('armL',4,12,4,{f:[52,52],b:[60,52],l:[56,52],r:[48,52],t:[52,48],o:[56,48]},true);
part('armR',4,12,4,{f:[44,36],b:[52,36],l:[48,36],r:[40,36],t:[44,32],o:[48,32]},true);
part('legL',4,12,4,{f:[4,52],b:[12,52],l:[8,52],r:[0,52],t:[4,48],o:[8,48]},true);
part('legR',4,12,4,{f:[4,36],b:[12,36],l:[8,36],r:[0,36],t:[4,32],o:[8,32]},true);
let down=false,lastX=0,lastY=0,ry=25,rx=-9;function apply(){model.style.transform=`rotateX(${rx}deg) rotateY(${ry}deg)`}
stage.addEventListener('pointerdown',e=>{down=true;stage.classList.add('dragging');lastX=e.clientX;lastY=e.clientY;stage.setPointerCapture(e.pointerId);apply()});
stage.addEventListener('pointermove',e=>{if(!down)return;ry+=e.clientX-lastX;rx=Math.max(-40,Math.min(30,rx-(e.clientY-lastY)*.4));lastX=e.clientX;lastY=e.clientY;apply()});
stage.addEventListener('pointerup',()=>{down=false;stage.classList.remove('dragging')});
</script>
</body>
</html>
""";
    }

    private void PlayerNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPlayerName)
        {
            return;
        }

        if (sender is System.Windows.Controls.TextBox textBox)
        {
            if (ReferenceEquals(textBox, HomePlayerNameBox))
            {
                UpdatePlayerPreview();
                ConfirmPlayerNameButton.IsEnabled = !string.IsNullOrWhiteSpace(HomePlayerNameBox.Text);
                return;
            }

            SyncPlayerNameText(textBox.Text);
        }

        UpdatePlayerPreview();
        UpdatePlayerNameMode();
    }

    private async void ConfirmPlayerNameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var playerName = HomePlayerNameBox.Text.Trim();
            if (!IsValidMinecraftName(playerName))
            {
                throw new InvalidOperationException("Ник должен быть от 3 до 16 символов: латиница, цифры или _.");
            }

            SyncPlayerNameText(playerName);
            _settings.PlayerName = playerName;
            await _settingsService.SaveAsync(_settings);
            UpdatePlayerNameMode();
            UpdatePlayerPreview();
            MainStatusText.Text = $"Ник подтвержден: {playerName}";
            SidebarStatusText.Text = "Ник подтвержден";
        });
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

        Process.Start(new ProcessStartInfo(Preferred3DMapUrl(_manifest.BlueMapUrl)) { UseShellExecute = true });
    }

    private async void ReloadMapButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadMapAsync(forceReload: true);
    }

    private async Task LoadMapAsync(bool forceReload)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            RenderMapLink();
            return;
        }

        var mapUrl = Preferred3DMapUrl(_manifest.BlueMapUrl);
        MapUrlText.Text = mapUrl;

        if (_mapInitialized && !forceReload && MapWebView.Source?.ToString() == mapUrl)
        {
            return;
        }

        try
        {
            MapFallbackPanel.Visibility = Visibility.Visible;
            MapStatusText.Text = "Загружаю 3D-карту...";
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            MapWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            MapWebView.Source = new Uri(mapUrl);
            _mapInitialized = true;
        }
        catch (Exception ex)
        {
            MapFallbackPanel.Visibility = Visibility.Visible;
            MapStatusText.Text = $"Не удалось открыть встроенную карту: {ex.Message}";
        }
    }

    private static string Preferred3DMapUrl(string mapUrl)
    {
        return mapUrl.EndsWith(":flat", StringComparison.OrdinalIgnoreCase)
            ? mapUrl[..^":flat".Length] + ":perspective"
            : mapUrl;
    }

    private void HomeNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
    private void NewsNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(NewsPanel);
    private async void MapNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(MapPanel);
        await LoadMapAsync(forceReload: false);
    }
    private async void SkinsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(SkinsPanel);
        await LoadSkinPreviewAsync(_skinService.CachedSkinPath(_settings) ?? _selectedSkinPath ?? _settings.SkinSourcePath);
    }
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);

    private void ShowPanel(UIElement panel)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        NewsPanel.Visibility = Visibility.Collapsed;
        MapPanel.Visibility = Visibility.Collapsed;
        SkinsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    private void InitializeVisualControls()
    {
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
        SaveCustomColorsFromUi();
        _settings.DynamicBackground = DynamicBackgroundCheckBox.IsChecked == true;
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
        var gradientStart = ReadColor(_settings.CustomGradientStartColor, accent);
        var gradientEnd = ReadColor(_settings.CustomGradientEndColor, surface);
        text = EnsureReadable(text, 4.5, background, sidebar, surface, surfaceAlt);
        muted = EnsureReadable(muted, 3.0, background, sidebar, surface, surfaceAlt);
        var accentText = BestReadableText(accent);
        var comboItemText = EnsureReadable(text, 4.5, surfaceAlt);
        var resources = Resources;

        resources["AppBackgroundBrush"] = new SolidColorBrush(background);
        resources["SidebarBrush"] = new SolidColorBrush(sidebar);
        resources["SurfaceBrush"] = new SolidColorBrush(ColorWithOpacity(surface, _settings.PanelOpacity));
        resources["SurfaceAltBrush"] = new SolidColorBrush(surfaceAlt);
        resources["BorderBrush"] = new SolidColorBrush(border);
        resources["TextBrush"] = new SolidColorBrush(text);
        resources["MutedBrush"] = new SolidColorBrush(muted);
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentTextBrush"] = new SolidColorBrush(accentText);
        resources["ComboItemBackgroundBrush"] = new SolidColorBrush(surfaceAlt);
        resources["ComboItemTextBrush"] = new SolidColorBrush(comboItemText);
        resources["ProgressTrackBrush"] = new SolidColorBrush(surfaceAlt);
        resources["AtmosphereBrush"] = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(gradientStart, 0),
                new(background, 0.55),
                new(gradientEnd, 1)
            }
        };

        DynamicLayer.Visibility = _settings.DynamicBackground ? Visibility.Visible : Visibility.Collapsed;
        ContentShell.Margin = new Thickness(26, 0, 0, 0);
    }

    private static MediaColor ColorWithOpacity(MediaColor color, double opacity)
    {
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return color;
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
        CustomGradientStartColorBox.Text = _settings.CustomGradientStartColor;
        CustomGradientEndColorBox.Text = _settings.CustomGradientEndColor;
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
        _settings.CustomGradientStartColor = NormalizeHexColor(CustomGradientStartColorBox.Text);
        _settings.CustomGradientEndColor = NormalizeHexColor(CustomGradientEndColorBox.Text);
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
        return TryReadColor(value, out var color) ? color : fallback;
    }

    private static bool TryReadColor(string value, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MediaColor EnsureReadable(MediaColor requested, double minimumContrast, params MediaColor[] backgrounds)
    {
        if (backgrounds.All(background => ContrastRatio(requested, background) >= minimumContrast))
        {
            return requested;
        }

        var white = System.Windows.Media.Colors.White;
        var black = System.Windows.Media.Colors.Black;
        var whiteScore = backgrounds.Min(background => ContrastRatio(white, background));
        var blackScore = backgrounds.Min(background => ContrastRatio(black, background));
        return whiteScore >= blackScore ? white : black;
    }

    private static MediaColor BestReadableText(MediaColor background)
    {
        return ContrastRatio(System.Windows.Media.Colors.White, background) >= ContrastRatio(System.Windows.Media.Colors.Black, background)
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Colors.Black;
    }

    private static double ContrastRatio(MediaColor first, MediaColor second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(MediaColor color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
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
        var statuses = await VerifyFilesAsync(downloadMissingFiles: true, verifyHashes: true);
        var outdated = CountOutdated(statuses);
        _gameFilesReady = outdated == 0;
        UpdatePrimaryButtonState();

        if (!_gameFilesReady)
        {
            throw new InvalidOperationException($"Не удалось установить сборку: {outdated} файлов не прошли проверку.");
        }

        SetBusy(true, "Проверяю Java 21...");
        var javaProgress = new Progress<string>(message => ProgressText.Text = message);
        var javaPath = await _gameLaunchService.EnsureCompatibleJavaAsync(_settings, javaProgress, CurrentToken());

        SetBusy(true, "Готовлю Minecraft runtime...");
        var runtimeProgress = new Progress<string>(message => ProgressText.Text = message);
        await _minecraftRuntimeService.EnsureAsync(_manifest!, _settings, javaPath, runtimeProgress, CurrentToken());

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
        return HomeNameInputPanel.Visibility == Visibility.Visible
            ? HomePlayerNameBox.Text.Trim()
            : PlayerNameBox.Text.Trim();
    }

    private void UpdatePlayerNameMode()
    {
        var playerName = PlayerNameBox.Text.Trim();
        var confirmed = IsValidMinecraftName(playerName);

        HomeNameInputPanel.Visibility = confirmed ? Visibility.Collapsed : Visibility.Visible;
        HomeNameLockedPanel.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        LockedPlayerNameText.Text = playerName;
        ConfirmPlayerNameButton.IsEnabled = !string.IsNullOrWhiteSpace(HomePlayerNameBox.Text);

        if (confirmed && HomePlayerNameBox.Text != playerName)
        {
            HomePlayerNameBox.Text = playerName;
        }
    }

    private static bool IsValidMinecraftName(string playerName)
    {
        var trimmed = playerName.Trim();
        return trimmed.Length is >= 3 and <= 16
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
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
