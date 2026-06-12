using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ServerLauncher.Models;
using ServerLauncher.Services;

namespace Minivibe.Mac;

public sealed class MainWindow : Window
{
    private const string PlayerNamePlaceholder = "Введите ник";

    private readonly MacSettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly FileSyncService _fileSyncService = new();
    private readonly MinecraftRuntimeService _runtimeService = new();
    private readonly MacGameLaunchService _launchService = new();
    private readonly MacSkinService _skinService = new();
    private readonly MinecraftServerListService _serverListService = new();
    private readonly HttpClient _newsHttpClient = new();
    private readonly HttpClient _updateHttpClient = new();

    private readonly Grid _scene = new();
    private readonly Border _dynamicLayer = new();
    private readonly Border _sidebar = new();
    private readonly Grid _contentShell = new();
    private readonly StackPanel _sidebarStack = new() { Spacing = 14 };
    private readonly List<Border> _cards = [];
    private readonly List<TextBlock> _primaryTexts = [];
    private readonly List<TextBlock> _mutedTexts = [];
    private readonly List<TextBox> _textBoxes = [];
    private readonly List<CheckBox> _checkBoxes = [];
    private readonly List<Button> _primaryButtons = [];
    private readonly List<Button> _secondaryButtons = [];
    private readonly List<Button> _navButtons = [];

    private readonly Grid _homePanel = new();
    private readonly Grid _newsPanel = new();
    private readonly Grid _mapPanel = new();
    private readonly Grid _skinsPanel = new();
    private readonly ScrollViewer _settingsPanel = new();

    private readonly TextBlock _sidebarStatusText = new();
    private readonly TextBlock _serverNameText = new();
    private readonly TextBlock _serverVersionText = new();
    private readonly TextBlock _mainStatusText = new();
    private readonly TextBlock _serverAddressText = new();
    private readonly TextBlock _playerPreviewText = new();
    private readonly TextBlock _packPreviewText = new();
    private readonly TextBlock _loaderPreviewText = new();
    private readonly TextBlock _launcherPreviewText = new();
    private readonly TextBlock _packInfoText = new();
    private readonly TextBlock _loaderInfoText = new();
    private readonly TextBlock _installInfoText = new();
    private readonly StackPanel _homeChangelogList = new() { Spacing = 6 };
    private readonly StackPanel _homeNameInputPanel = new() { Spacing = 8 };
    private readonly Border _homeNameLockedPanel = new();
    private readonly TextBox _homePlayerNameBox = new();
    private readonly TextBlock _lockedPlayerNameText = new();
    private readonly Button _confirmPlayerNameButton = new();
    private readonly Button _playButton = new();
    private readonly Button _repairButton = new();
    private readonly Button _refreshManifestButton = new();

    private readonly Button _refreshNewsButton = new();
    private readonly ListBox _newsList = new();
    private readonly TextBlock _newsTitleText = new();
    private readonly TextBlock _newsDateText = new();
    private readonly TextBlock _newsBodyText = new();
    private readonly Image _newsImage = new();
    private readonly Button _openNewsButton = new();
    private readonly MacWebViewHost _newsWebView = new();

    private readonly Button _reloadMapButton = new();
    private readonly Button _openMapButton = new();
    private readonly TextBlock _mapStatusText = new();
    private readonly TextBlock _mapUrlText = new();
    private readonly MacWebViewHost _mapWebView = new();

    private readonly MacWebViewHost _skinPreviewWebView = new();
    private readonly TextBlock _skinStatusText = new();
    private readonly Button _chooseSkinButton = new();
    private readonly Button _installSkinButton = new();

    private readonly TextBox _installDirectoryBox = new();
    private readonly Button _browseInstallDirectoryButton = new();
    private readonly CheckBox _shadersCheckBox = new();
    private readonly CheckBox _emotesCheckBox = new();
    private readonly CheckBox _gameConsoleCheckBox = new();
    private readonly CheckBox _downloadDetailsCheckBox = new();
    private readonly TextBox _ramBox = new();
    private readonly TextBox _playerNameBox = new();
    private readonly TextBox _extraArgsBox = new();
    private readonly CheckBox _autoUpdateCheckBox = new();
    private readonly CheckBox _dynamicBackgroundCheckBox = new();
    private readonly TextBox _customBackgroundColorBox = new();
    private readonly TextBox _customSidebarColorBox = new();
    private readonly TextBox _customSurfaceColorBox = new();
    private readonly TextBox _customBorderColorBox = new();
    private readonly TextBox _customTextColorBox = new();
    private readonly TextBox _customMutedTextColorBox = new();
    private readonly TextBox _customAccentColorBox = new();
    private readonly TextBox _customGradientStartColorBox = new();
    private readonly TextBox _customGradientEndColorBox = new();
    private readonly Slider _panelOpacitySlider = new();
    private readonly Slider _sidebarOpacitySlider = new();
    private readonly Slider _backgroundEffectOpacitySlider = new();
    private readonly TextBlock _panelOpacityValueText = new();
    private readonly TextBlock _sidebarOpacityValueText = new();
    private readonly TextBlock _backgroundEffectOpacityValueText = new();
    private readonly Button _saveSettingsButton = new();
    private readonly Button _openInstallDirectoryButton = new();
    private readonly TextBox _consoleBox = new();

    private readonly ProgressBar _progressBar = new();
    private readonly TextBlock _progressText = new();

    private LauncherSettings _settings = new();
    private LauncherManifest? _manifest;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _visualSaveCts;
    private bool _bindingSettings;
    private bool _syncingPlayerName;
    private bool _busy;
    private bool _gameFilesReady;
    private bool _launcherUpdateRequired;
    private string? _requiredLauncherVersion;
    private string? _selectedSkinPath;
    private Process? _minecraftProcess;
    private Button? _activeNavButton;
    private CancellationTokenSource? _newsMediaCts;

    public MainWindow()
    {
        Title = "minivibe";
        Width = 1180;
        Height = 760;
        MinWidth = 980;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildContent();
        ApplyVisualSettings();
        ShowPanel(_homePanel, _navButtons.FirstOrDefault());
        Opened += async (_, _) => await RunGuardedAsync(InitializeAsync);
        Closed += (_, _) =>
        {
            _operationCts?.Cancel();
            _visualSaveCts?.Cancel();
            _newsMediaCts?.Cancel();
            _newsHttpClient.Dispose();
            _updateHttpClient.Dispose();
        };
    }

    private Control BuildContent()
    {
        _scene.Children.Add(_dynamicLayer);
        var layout = new Grid
        {
            Margin = new Thickness(22),
            ColumnDefinitions = new ColumnDefinitions("250,*")
        };
        _scene.Children.Add(layout);

        _sidebar.Padding = new Thickness(18);
        _sidebar.CornerRadius = new CornerRadius(8);
        _sidebar.Child = BuildSidebar();
        Grid.SetColumn(_sidebar, 0);
        layout.Children.Add(_sidebar);

        _contentShell.Margin = new Thickness(24, 0, 0, 0);
        _contentShell.RowDefinitions = new RowDefinitions("*,Auto");
        Grid.SetColumn(_contentShell, 1);
        layout.Children.Add(_contentShell);

        BuildHomePanel();
        BuildNewsPanel();
        BuildMapPanel();
        BuildSkinsPanel();
        BuildSettingsPanel();

        foreach (var panel in new Control[] { _homePanel, _newsPanel, _mapPanel, _skinsPanel, _settingsPanel })
        {
            Grid.SetRow(panel, 0);
            _contentShell.Children.Add(panel);
        }

        var progressCard = Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _progressBar,
                _progressText
            }
        });
        progressCard.Margin = new Thickness(0, 16, 0, 0);
        _progressBar.Height = 7;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressText.TextWrapping = TextWrapping.Wrap;
        RegisterMuted(_progressText);
        Grid.SetRow(progressCard, 1);
        _contentShell.Children.Add(progressCard);

        return _scene;
    }

    private Control BuildSidebar()
    {
        var logo = RegisterPrimary(new TextBlock
        {
            Text = "minivibe",
            FontSize = 30,
            FontWeight = FontWeight.Black
        });
        var version = RegisterMuted(new TextBlock
        {
            Text = "Launcher " + CurrentLauncherVersion(),
            FontSize = 13
        });

        _sidebarStack.Children.Add(logo);
        _sidebarStack.Children.Add(version);
        _sidebarStack.Children.Add(new Border { Height = 12, Opacity = 0 });
        _sidebarStack.Children.Add(NavButton("Главная", () => ShowPanel(_homePanel, _navButtons[0])));
        _sidebarStack.Children.Add(NavButton("Новости", async () =>
        {
            ShowPanel(_newsPanel, _navButtons[1]);
            await RefreshNewsGuardedAsync();
        }));
        _sidebarStack.Children.Add(NavButton("Карта", () =>
        {
            ShowPanel(_mapPanel, _navButtons[2]);
            RenderMapLink();
        }));
        _sidebarStack.Children.Add(NavButton("Скины", async () =>
        {
            ShowPanel(_skinsPanel, _navButtons[3]);
            await LoadSkinPreviewAsync(_skinService.CachedSkinPath(_settings) ?? _selectedSkinPath ?? _settings.SkinSourcePath);
        }));
        _sidebarStack.Children.Add(NavButton("Настройки", () => ShowPanel(_settingsPanel, _navButtons[4])));
        _sidebarStack.Children.Add(new Border { Height = 1, Opacity = 0 });
        _sidebarStatusText.Text = "Загрузка...";
        _sidebarStatusText.TextWrapping = TextWrapping.Wrap;
        _sidebarStatusText.FontWeight = FontWeight.SemiBold;
        _sidebarStack.Children.Add(Card(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                RegisterMuted(new TextBlock { Text = "Статус", FontSize = 12 }),
                RegisterPrimary(_sidebarStatusText)
            }
        }));

        return _sidebarStack;
    }

    private void BuildHomePanel()
    {
        _homePanel.ColumnDefinitions = new ColumnDefinitions("*,310");
        _homePanel.RowDefinitions = new RowDefinitions("Auto,*");

        var hero = Card(new StackPanel { Spacing = 16 });
        var heroStack = (StackPanel)hero.Child!;
        _serverNameText.Text = "minivibe";
        _serverNameText.FontSize = 42;
        _serverNameText.FontWeight = FontWeight.Black;
        _serverVersionText.Text = "Manifest не загружен";
        _serverVersionText.FontSize = 15;
        _mainStatusText.Text = "Проверяю сборку...";
        _mainStatusText.FontSize = 17;
        _mainStatusText.TextWrapping = TextWrapping.Wrap;
        heroStack.Children.Add(RegisterPrimary(_serverNameText));
        heroStack.Children.Add(RegisterMuted(_serverVersionText));
        heroStack.Children.Add(RegisterPrimary(_mainStatusText));

        _playButton.Content = "Установить";
        _playButton.MinWidth = 150;
        _playButton.Click += async (_, _) => await RunGuardedAsync(PlayAsync);
        _repairButton.Content = "Проверить файлы";
        _repairButton.Click += async (_, _) => await RunGuardedAsync(RepairAsync);
        _refreshManifestButton.Content = "Обновить список";
        _refreshManifestButton.Click += async (_, _) => await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            await RefreshLauncherUpdateGateAsync();
            await LoadManifestAsync(repairMissingGameFiles: false);
        });
        heroStack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                PrimaryButton(_playButton),
                SecondaryButton(_repairButton),
                SecondaryButton(_refreshManifestButton)
            }
        });
        _serverAddressText.Text = "Сервер: " + MinecraftServerListService.ServerAddress;
        _serverAddressText.TextWrapping = TextWrapping.Wrap;
        heroStack.Children.Add(RegisterMuted(_serverAddressText));
        Grid.SetColumn(hero, 0);
        _homePanel.Children.Add(hero);

        var profile = Card(new StackPanel { Spacing = 10 });
        var profileStack = (StackPanel)profile.Child!;
        profileStack.Children.Add(SectionTitle("Профиль"));
        _confirmPlayerNameButton.Content = "OK";
        _confirmPlayerNameButton.Width = 58;
        _confirmPlayerNameButton.Click += async (_, _) => await RunGuardedAsync(ConfirmPlayerNameAsync);
        _homePlayerNameBox.PlaceholderText = PlayerNamePlaceholder;
        _homePlayerNameBox.TextChanged += PlayerNameBox_TextChanged;
        RegisterTextBox(_homePlayerNameBox);
        _homeNameInputPanel.Children.Add(RegisterMuted(new TextBlock { Text = "Ник" }));
        _homeNameInputPanel.Children.Add(new DockPanel
        {
            Children =
            {
                DockRight(SecondaryButton(_confirmPlayerNameButton)),
                _homePlayerNameBox
            }
        });
        _homeNameLockedPanel.Padding = new Thickness(12);
        _homeNameLockedPanel.CornerRadius = new CornerRadius(8);
        _homeNameLockedPanel.Child = RegisterPrimary(_lockedPlayerNameText);
        profileStack.Children.Add(_homeNameInputPanel);
        profileStack.Children.Add(_homeNameLockedPanel);
        _playerPreviewText.Text = "Ник появится в игре";
        _playerPreviewText.TextWrapping = TextWrapping.Wrap;
        profileStack.Children.Add(RegisterMuted(_playerPreviewText));

        profileStack.Children.Add(RegisterMuted(new TextBlock { Text = "Сборка" }));
        _packPreviewText.Text = "ожидает manifest";
        _packPreviewText.FontSize = 18;
        _packPreviewText.FontWeight = FontWeight.SemiBold;
        _packPreviewText.TextWrapping = TextWrapping.Wrap;
        profileStack.Children.Add(RegisterPrimary(_packPreviewText));
        profileStack.Children.Add(RegisterMuted(new TextBlock { Text = "Loader" }));
        _loaderPreviewText.Text = "NeoForge";
        _loaderPreviewText.FontSize = 18;
        _loaderPreviewText.FontWeight = FontWeight.SemiBold;
        _loaderPreviewText.TextWrapping = TextWrapping.Wrap;
        profileStack.Children.Add(RegisterPrimary(_loaderPreviewText));
        profileStack.Children.Add(RegisterMuted(new TextBlock { Text = "Лаунчер" }));
        _launcherPreviewText.Text = CurrentLauncherVersion();
        _launcherPreviewText.FontSize = 18;
        _launcherPreviewText.FontWeight = FontWeight.SemiBold;
        profileStack.Children.Add(RegisterPrimary(_launcherPreviewText));
        Grid.SetColumn(profile, 1);
        _homePanel.Children.Add(profile);

        var info = Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        SectionTitle("Сборка"),
                        RegisterMuted(_packInfoText),
                        RegisterMuted(_loaderInfoText),
                        RegisterMuted(_installInfoText)
                    }
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        SectionTitle("Изменения"),
                        _homeChangelogList
                    }
                }
            }
        });
        Grid.SetColumn(((Grid)info.Child!).Children[1], 1);
        Grid.SetRow(info, 1);
        Grid.SetColumnSpan(info, 2);
        info.Margin = new Thickness(0, 16, 0, 0);
        _homePanel.Children.Add(info);
    }

    private void BuildNewsPanel()
    {
        _newsPanel.RowDefinitions = new RowDefinitions("Auto,*");
        var header = new DockPanel();
        _refreshNewsButton.Content = "Обновить";
        _refreshNewsButton.Click += async (_, _) => await RefreshNewsGuardedAsync();
        header.Children.Add(DockRight(SecondaryButton(_refreshNewsButton)));
        header.Children.Add(PageTitle("Новости"));
        _newsPanel.Children.Add(header);

        var body = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("300,*")
        };
        _newsList.SelectionChanged += (_, _) => RenderNewsItem(_newsList.SelectedItem as NewsItem);
        body.Children.Add(Card(_newsList));

        _newsTitleText.FontSize = 28;
        _newsTitleText.FontWeight = FontWeight.Bold;
        _newsTitleText.TextWrapping = TextWrapping.Wrap;
        _newsDateText.FontSize = 13;
        _newsBodyText.TextWrapping = TextWrapping.Wrap;
        _newsImage.Stretch = Stretch.Uniform;
        _newsImage.MaxHeight = 420;
        _newsWebView.Height = 460;
        _newsWebView.IsVisible = false;
        _openNewsButton.Content = "Открыть в браузере";
        _openNewsButton.IsVisible = false;
        _openNewsButton.Click += (_, _) =>
        {
            if (_newsList.SelectedItem is NewsItem { Url: { Length: > 0 } itemUrl })
            {
                OpenUrl(itemUrl);
            }
        };
        var detail = Card(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    RegisterPrimary(_newsTitleText),
                    RegisterMuted(_newsDateText),
                    _newsImage,
                    _newsWebView,
                    RegisterPrimary(_newsBodyText),
                    SecondaryButton(_openNewsButton)
                }
            }
        });
        Grid.SetColumn(detail, 1);
        detail.Margin = new Thickness(16, 0, 0, 0);
        body.Children.Add(detail);
        Grid.SetRow(body, 1);
        _newsPanel.Children.Add(body);
    }

    private void BuildMapPanel()
    {
        _mapPanel.RowDefinitions = new RowDefinitions("Auto,*");
        var header = new DockPanel();
        _openMapButton.Content = "В браузере";
        _openMapButton.Click += (_, _) => OpenMap();
        _reloadMapButton.Content = "Обновить";
        _reloadMapButton.Click += (_, _) => RenderMapLink();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { SecondaryButton(_reloadMapButton), PrimaryButton(_openMapButton) }
        };
        header.Children.Add(DockRight(actions));
        header.Children.Add(PageTitle("Карта"));
        _mapPanel.Children.Add(header);

        _mapWebView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _mapWebView.VerticalAlignment = VerticalAlignment.Stretch;
        var mapHost = new Grid
        {
            MinHeight = 420,
            Children = { _mapWebView }
        };
        var fallback = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Children =
            {
                RegisterPrimary(new TextBlock
                {
                    Text = "BlueMap",
                    FontSize = 34,
                    FontWeight = FontWeight.Black,
                    HorizontalAlignment = HorizontalAlignment.Center
                }),
                RegisterMuted(_mapStatusText),
                RegisterMuted(_mapUrlText),
                PrimaryButton(new Button
                {
                    Content = "Открыть карту",
                    HorizontalAlignment = HorizontalAlignment.Center
                }.WithClick(_ => OpenMap()))
            }
        };
        mapHost.Children.Add(fallback);
        fallback.IsHitTestVisible = false;
        fallback.Opacity = 0;
        var card = Card(mapHost);
        card.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(card, 1);
        _mapPanel.Children.Add(card);
    }

    private void BuildSkinsPanel()
    {
        _skinsPanel.RowDefinitions = new RowDefinitions("Auto,*");
        _skinsPanel.Children.Add(PageTitle("Скины"));

        _skinPreviewWebView.Width = 280;
        _skinPreviewWebView.Height = 420;
        _chooseSkinButton.Content = "Выбрать PNG/JPG";
        _chooseSkinButton.Click += async (_, _) => await RunGuardedAsync(ChooseSkinAsync);
        _installSkinButton.Content = "Сохранить скин";
        _installSkinButton.Click += async (_, _) => await RunGuardedAsync(InstallSkinAsync);
        _skinStatusText.Text = "Выберите скин и сохраните его для текущего ника.";
        _skinStatusText.TextWrapping = TextWrapping.Wrap;

        var content = Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,*"),
            Children =
            {
                new Border
                {
                    Padding = new Thickness(16),
                    CornerRadius = new CornerRadius(8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = _skinPreviewWebView
                },
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        SectionTitle("Локальный и общий скин"),
                        RegisterMuted(new TextBlock
                        {
                            Text = "Скин сохраняется в OfflineSkins-кэш и отправляется через worker в общий каталог GitHub.",
                            TextWrapping = TextWrapping.Wrap
                        }),
                        RegisterPrimary(_skinStatusText),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children =
                            {
                                PrimaryButton(_chooseSkinButton),
                                SecondaryButton(_installSkinButton)
                            }
                        }
                    }
                }
            }
        });
        Grid.SetColumn(((Grid)content.Child!).Children[1], 1);
        content.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(content, 1);
        _skinsPanel.Children.Add(content);
    }

    private void BuildSettingsPanel()
    {
        var stack = new StackPanel { Spacing = 16 };
        _settingsPanel.Content = stack;
        _settingsPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        _browseInstallDirectoryButton.Content = "Выбрать";
        _browseInstallDirectoryButton.Click += async (_, _) => await BrowseInstallDirectoryAsync();
        RegisterTextBox(_installDirectoryBox);
        stack.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SectionTitle("Игра"),
                RegisterMuted(new TextBlock { Text = "Папка игры" }),
                new DockPanel
                {
                    Children =
                    {
                        DockRight(SecondaryButton(_browseInstallDirectoryButton)),
                        _installDirectoryBox
                    }
                },
                Toggle(_shadersCheckBox, "Шейдеры"),
                new DockPanel
                {
                    Children =
                    {
                        DockRight(SecondaryButton(new Button
                        {
                            Content = "Переустановить"
                        }.WithAsyncClick(async () => await RunGuardedAsync(async () =>
                        {
                            _emotesCheckBox.IsChecked = true;
                            await SaveSettingsFromUiAsync();
                            await InstallEmotesAsync(reinstall: true);
                        })))),
                        Toggle(_emotesCheckBox, "Эмоции")
                    }
                },
                Toggle(_gameConsoleCheckBox, "Консоль Minecraft"),
                Toggle(_downloadDetailsCheckBox, "Подробности скачивания")
            }
        }));

        RegisterTextBox(_ramBox);
        RegisterTextBox(_playerNameBox);
        RegisterTextBox(_extraArgsBox);
        _playerNameBox.TextChanged += PlayerNameBox_TextChanged;
        stack.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SectionTitle("Запуск"),
                RegisterMuted(new TextBlock { Text = "Оперативная память, MB" }),
                _ramBox,
                RegisterMuted(new TextBlock { Text = "Ник" }),
                _playerNameBox,
                RegisterMuted(new TextBlock { Text = "Дополнительные аргументы" }),
                _extraArgsBox,
                Toggle(_autoUpdateCheckBox, "Автообновление лаунчера при запуске")
            }
        }));

        stack.Children.Add(BuildVisualSettingsCard());

        _saveSettingsButton.Content = "Сохранить";
        _saveSettingsButton.Click += async (_, _) => await RunGuardedAsync(async () =>
        {
            await SaveSettingsFromUiAsync();
            await LoadManifestAsync(repairMissingGameFiles: false);
            _mainStatusText.Text = "Настройки сохранены локально.";
            _sidebarStatusText.Text = "Настройки сохранены";
        });
        _openInstallDirectoryButton.Content = "Открыть папку игры";
        _openInstallDirectoryButton.Click += (_, _) => OpenInstallDirectory();
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                PrimaryButton(_saveSettingsButton),
                SecondaryButton(_openInstallDirectoryButton)
            }
        });

        RegisterTextBox(_consoleBox);
        _consoleBox.AcceptsReturn = true;
        _consoleBox.IsReadOnly = true;
        _consoleBox.TextWrapping = TextWrapping.Wrap;
        _consoleBox.FontFamily = FontFamily.Parse("Menlo,Consolas,monospace");
        _consoleBox.FontSize = 13;
        _consoleBox.MinHeight = 150;
        stack.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SectionTitle("Консоль Minecraft"),
                _consoleBox
            }
        }));
    }

    private Control BuildVisualSettingsCard()
    {
        foreach (var box in new[]
        {
            _customBackgroundColorBox,
            _customSidebarColorBox,
            _customSurfaceColorBox,
            _customBorderColorBox,
            _customTextColorBox,
            _customMutedTextColorBox,
            _customAccentColorBox,
            _customGradientStartColorBox,
            _customGradientEndColorBox
        })
        {
            RegisterTextBox(box);
            box.PlaceholderText = "#RRGGBB";
            box.TextChanged += (_, _) => VisualSettingChanged();
        }

        ConfigureOpacitySlider(_panelOpacitySlider, _panelOpacityValueText, 0, 28);
        ConfigureOpacitySlider(_sidebarOpacitySlider, _sidebarOpacityValueText, 0, 28);
        ConfigureOpacitySlider(_backgroundEffectOpacitySlider, _backgroundEffectOpacityValueText, 0, 100);
        _dynamicBackgroundCheckBox.Click += (_, _) => VisualSettingChanged();

        var visualStack = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ColorRow("Фон", _customBackgroundColorBox),
                ColorRow("Сайдбар", _customSidebarColorBox),
                ColorRow("Панели", _customSurfaceColorBox),
                ColorRow("Границы", _customBorderColorBox),
                ColorRow("Текст", _customTextColorBox),
                ColorRow("Вторичный текст", _customMutedTextColorBox),
                ColorRow("Акцент", _customAccentColorBox),
                ColorRow("Градиент от", _customGradientStartColorBox),
                ColorRow("Градиент до", _customGradientEndColorBox),
                Toggle(_dynamicBackgroundCheckBox, "Градиент"),
                SliderRow("Прозрачность панелей", _panelOpacitySlider, _panelOpacityValueText),
                SliderRow("Прозрачность сайдбара", _sidebarOpacitySlider, _sidebarOpacityValueText),
                SliderRow("Интенсивность фона", _backgroundEffectOpacitySlider, _backgroundEffectOpacityValueText)
            }
        };

        return Card(new Expander
        {
            Header = SectionTitle("Визуал"),
            IsExpanded = false,
            Content = visualStack
        });
    }

    private async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync();
        BindSettingsToUi();
        ApplyVisualSettings();
        if (await CheckAndApplyLauncherUpdateAsync())
        {
            return;
        }

        await RefreshLauncherUpdateGateAsync();
        await ShowPatchNotesIfNeededAsync();
        await LoadManifestAsync(repairMissingGameFiles: false);
        await LoadSkinPreviewAsync(_skinService.CachedSkinPath(_settings) ?? _settings.SkinSourcePath);
    }

    private async Task LoadManifestAsync(bool repairMissingGameFiles)
    {
        SetBusy(true, "Загружаю manifest.json...");
        _manifest = await _manifestService.LoadAsync(LauncherEndpoints.ManifestUrl, CurrentToken());
        RenderManifest();
        await EnsureGameFilesReadyAsync(repairMissingGameFiles);
    }

    private async Task RefreshNewsAsync()
    {
        SetBusy(true, "Обновляю новости...");
        var selectedNewsKey = NewsIdentity(_newsList.SelectedItem as NewsItem);
        _manifest = await _manifestService.LoadAsync(LauncherEndpoints.ManifestUrl, CurrentToken(), bypassCache: true);
        RenderManifest(selectedNewsKey);
        _progressText.Text = "Новости обновлены.";
    }

    private async Task RefreshNewsGuardedAsync()
    {
        if (_busy)
        {
            return;
        }

        try
        {
            await RefreshNewsAsync();
        }
        catch (Exception ex)
        {
            await SaveBugReportAsync(ex, "News refresh");
            _progressText.Text = "Не удалось обновить новости.";
        }
        finally
        {
            SetBusy(false, _progressText.Text ?? "");
        }
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
            UpdatePrimaryButtonState();
            return;
        }

        _mainStatusText.Text = $"Не хватает файлов для запуска: {outdated}. Докачиваю сборку...";
        _sidebarStatusText.Text = "Докачиваю сборку";
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
        var progress = new Progress<string>(message => _progressText.Text = message);
        var statuses = await _fileSyncService.VerifyAndRepairAsync(
            _manifest,
            _settings,
            downloadMissingFiles,
            verifyHashes || downloadMissingFiles,
            progress,
            CurrentToken(),
            includeEmotes: _settings.EnableEmotes);

        var outdated = CountOutdated(statuses);
        _mainStatusText.Text = outdated switch
        {
            0 => "Сборка готова к запуску. Пользовательские моды не тронуты.",
            _ when downloadMissingFiles => $"После восстановления осталось проблемных файлов: {outdated}.",
            _ => $"Нужно установить файлов: {outdated}. Нажмите \"Установить\"."
        };
        _sidebarStatusText.Text = outdated == 0 ? "Готово" : "Есть обновления";
        UpdatePrimaryButtonState();
        return statuses;
    }

    private async Task PlayAsync()
    {
        await SaveSettingsFromUiAsync();
        await RefreshLauncherUpdateGateAsync();
        if (_launcherUpdateRequired)
        {
            return;
        }

        if (_minecraftProcess is not null && !_minecraftProcess.HasExited)
        {
            _mainStatusText.Text = $"Minecraft уже запущен через minivibe. PID: {_minecraftProcess.Id}.";
            _sidebarStatusText.Text = "Игра уже запущена";
            return;
        }

        var activeSession = _launchService.FindActiveSession(_settings);
        if (activeSession is not null)
        {
            _mainStatusText.Text = $"Minecraft уже запущен под ником {_settings.PlayerName}. PID: {activeSession.ProcessId}.";
            _sidebarStatusText.Text = "Ник уже в игре";
            return;
        }

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
            _mainStatusText.Text = $"Нужно установить файлов: {readinessOutdated}. Нажмите \"Установить\".";
            _sidebarStatusText.Text = "Требуется установка";
            UpdatePrimaryButtonState();
            return;
        }

        SetBusy(true, "Проверяю Java 21...");
        var javaProgress = new Progress<string>(message => _progressText.Text = message);
        var javaPath = await _launchService.EnsureCompatibleJavaAsync(_settings, javaProgress, CurrentToken());

        SetBusy(true, "Проверяю библиотеки Minecraft...");
        var runtimeProgress = new Progress<string>(message => _progressText.Text = message);
        var minecraftRuntime = await _runtimeService.EnsureAsync(_manifest!, _settings, javaPath, runtimeProgress, CurrentToken());

        var launchIssues = _launchService.ValidateReady(_manifest!, _settings, minecraftRuntime);
        if (launchIssues.Count > 0)
        {
            throw new InvalidOperationException("Minecraft не готов к запуску: " + string.Join("; ", launchIssues.Take(4)));
        }

        try
        {
            await _serverListService.EnsureMinivibeServerAsync(_settings, CurrentToken());
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Не удалось автоматически добавить сервер: " + ex.Message);
            _progressText.Text = "Не удалось автоматически добавить сервер, запуск продолжается.";
        }

        try
        {
            SetBusy(true, "Синхронизирую скины игроков...");
            var skinProgress = new Progress<string>(message => _progressText.Text = message);
            var syncedSkins = await _skinService.SyncSharedSkinsAsync(_settings, skinProgress, CurrentToken());
            _progressText.Text = syncedSkins > 0
                ? $"Скины игроков обновлены: {syncedSkins}."
                : "Скины игроков уже готовы.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Не удалось обновить локальный кэш скинов: " + ex.Message);
            _progressText.Text = "Не удалось обновить локальный кэш скинов, запуск продолжается.";
        }

        SetBusy(true, "Запускаю Minecraft...");
        if (_settings.EnableGameConsole)
        {
            AppendLog("Запускаю Minecraft...");
            AppendLog(_launchService.BuildLaunchSummary(_manifest!, _settings, minecraftRuntime));
        }

        var launchedSettings = new LauncherSettings
        {
            PlayerName = _settings.PlayerName,
            InstallDirectory = _settings.InstallDirectory
        };
        var launchedProcessId = 0;
        var process = _launchService.Start(
            _manifest!,
            _settings,
            minecraftRuntime,
            outputReceived: _settings.EnableGameConsole ? line => AppendLog(line) : null,
            errorReceived: _settings.EnableGameConsole ? line => AppendLog("[ERR] " + line) : null,
            processExited: exitCode =>
            {
                if (launchedProcessId != 0)
                {
                    _launchService.ClearActiveSession(launchedSettings, launchedProcessId);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _minecraftProcess = null;
                    _mainStatusText.Text = exitCode == 0
                        ? "Minecraft закрыт."
                        : _settings.EnableGameConsole
                            ? $"Minecraft завершился с кодом {exitCode}. Подробности в консоли."
                            : $"Minecraft завершился с кодом {exitCode}. Для подробностей включите консоль Minecraft в настройках.";
                    _sidebarStatusText.Text = exitCode == 0 ? "Игра закрыта" : "Ошибка игры";
                });
                AppendLog($"Minecraft завершился с кодом {exitCode}.");
            });

        launchedProcessId = process.Id;
        _minecraftProcess = process;
        _launchService.RegisterActiveSession(launchedSettings, process);
        _mainStatusText.Text = _settings.EnableGameConsole
            ? "Minecraft запущен."
            : "Minecraft запущен без live-консоли.";
        _sidebarStatusText.Text = "Игра запущена";
    }

    private async Task RepairAsync()
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

        if (_settings.EnableEmotes)
        {
            await InstallEmotesAsync(reinstall: false);
        }

        SetBusy(true, "Проверяю Java 21...");
        var javaProgress = new Progress<string>(message => _progressText.Text = message);
        var javaPath = await _launchService.EnsureCompatibleJavaAsync(_settings, javaProgress, CurrentToken());

        SetBusy(true, "Готовлю Minecraft runtime...");
        var runtimeProgress = new Progress<string>(message => _progressText.Text = message);
        await _runtimeService.EnsureAsync(_manifest!, _settings, javaPath, runtimeProgress, CurrentToken());

        UpdateLaunchReadinessStatus();
        _mainStatusText.Text = "Сборка установлена. Теперь можно нажать \"Играть\".";
        _sidebarStatusText.Text = "Установлено";
    }

    private async Task InstallEmotesAsync(bool reinstall)
    {
        SetBusy(true, reinstall ? "Переустанавливаю эмоции..." : "Устанавливаю эмоции...");
        var progress = new Progress<string>(message => _progressText.Text = message);
        await _fileSyncService.InstallEmotesArchiveAsync(_settings, reinstall, progress, CurrentToken());

        _settings.EnableEmotes = true;
        await _settingsService.SaveAsync(_settings);
        _mainStatusText.Text = reinstall ? "Эмоции переустановлены." : "Эмоции установлены.";
        _sidebarStatusText.Text = "Эмоции готовы";
        _progressText.Text = "Эмоции готовы";
    }

    private async Task ChooseSkinAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите скин",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Minecraft skin")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg"]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].Path.LocalPath;
        MacSkinService.ValidateSkinImage(path);
        _selectedSkinPath = path;
        _settings.SkinSourcePath = path;
        await _settingsService.SaveAsync(_settings);
        UpdateSkinStatus();
        await LoadSkinPreviewAsync(path);
    }

    private async Task InstallSkinAsync()
    {
        await SaveSettingsFromUiAsync();
        var sourcePath = _selectedSkinPath ?? _settings.SkinSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new InvalidOperationException("Сначала выберите PNG/JPG скин.");
        }

        SetBusy(true, "Сохраняю скин...");
        var installedPath = _skinService.InstallSkin(_settings, sourcePath);
        _selectedSkinPath = sourcePath;
        _settings.SkinSourcePath = sourcePath;
        _settings.SkinServerUrl = LauncherSettings.DefaultSkinServerUrl;
        _settings.EnableSkinServer = true;
        await _skinService.UploadSharedSkinAsync(_settings, sourcePath, CurrentToken());
        await _settingsService.SaveAsync(_settings);
        await _skinService.SaveOfflineSkinsConfigAsync(_settings, CurrentToken());
        _skinStatusText.Text = $"Скин {CurrentPlayerName()} сохранен и загружен для всех.";
        await LoadSkinPreviewAsync(installedPath);
    }

    private async Task LoadSkinPreviewAsync(string? skinPath)
    {
        await Task.Yield();
        try
        {
            if (!string.IsNullOrWhiteSpace(skinPath) && File.Exists(skinPath))
            {
                var skinBytes = MacSkinService.ReadSkinPngBytes(skinPath);
                var skinDataUrl = "data:image/png;base64," + Convert.ToBase64String(skinBytes);
                _skinPreviewWebView.NavigateToString(SkinPreviewHtml.Build(skinDataUrl));
                _skinStatusText.Text = $"Скин готов: {Path.GetFileName(skinPath)}";
                return;
            }

            _skinPreviewWebView.NavigateToString(SkinPreviewHtml.Empty());
            UpdateSkinStatus();
        }
        catch (Exception ex)
        {
            _skinPreviewWebView.NavigateToString(SkinPreviewHtml.Empty());
            _skinStatusText.Text = "Не удалось открыть превью: " + ex.Message;
        }
    }

    private async Task BrowseInstallDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку установки игры",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            await ApplyInstallDirectoryAsync(folders[0].Path.LocalPath);
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
        _installDirectoryBox.Text = path;
        _gameFilesReady = false;
        RenderManifest();
        UpdatePrimaryButtonState();
        _mainStatusText.Text = "Папка игры выбрана. Пользовательские configs, saves и лишние моды не удаляются.";
        _sidebarStatusText.Text = "Папка выбрана";
        await _settingsService.SaveAsync(_settings);
    }

    private async Task SaveSettingsFromUiAsync()
    {
        var selectedInstallDirectory = (_installDirectoryBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(selectedInstallDirectory))
        {
            selectedInstallDirectory = MacSettingsService.DefaultInstallDirectory();
        }

        if (!string.Equals(_settings.InstallDirectory, selectedInstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _gameFilesReady = false;
        }

        _settings.InstallDirectory = selectedInstallDirectory;
        _settings.EnableShaders = _shadersCheckBox.IsChecked == true;
        _settings.EnableEmotes = _emotesCheckBox.IsChecked == true;
        _settings.EnableGameConsole = _gameConsoleCheckBox.IsChecked == true;
        _settings.ShowDownloadDetails = _downloadDetailsCheckBox.IsChecked == true;
        _settings.PlayerName = (_playerNameBox.Text ?? "").Trim();
        _settings.SkinSourcePath = _selectedSkinPath ?? _settings.SkinSourcePath;
        _settings.SkinServerUrl = LauncherSettings.DefaultSkinServerUrl;
        _settings.EnableSkinServer = true;
        _settings.ExtraLaunchArguments = (_extraArgsBox.Text ?? "").Trim();
        _settings.EnableAutoUpdate = _autoUpdateCheckBox.IsChecked == true;
        SaveCustomColorsFromUi();
        _settings.DynamicBackground = _dynamicBackgroundCheckBox.IsChecked == true;
        _settings.PanelOpacity = TransparencyPercentToOpacity(_panelOpacitySlider.Value);
        _settings.SidebarOpacity = TransparencyPercentToOpacity(_sidebarOpacitySlider.Value);
        _settings.BackgroundEffectOpacity = PercentToRatio(_backgroundEffectOpacitySlider.Value);
        UpdateOpacityValueLabels();

        if (int.TryParse((_ramBox.Text ?? "").Trim(), out var ram))
        {
            _settings.RamMb = Math.Clamp(ram, 1024, 32768);
        }

        await _settingsService.SaveAsync(_settings);
        await _skinService.SaveOfflineSkinsConfigAsync(_settings, CurrentToken());
        UpdatePlayerNameMode();
        RenderManifest();
    }

    private void BindSettingsToUi()
    {
        _bindingSettings = true;
        try
        {
            _installDirectoryBox.Text = _settings.InstallDirectory;
            _shadersCheckBox.IsChecked = _settings.EnableShaders;
            _emotesCheckBox.IsChecked = _settings.EnableEmotes;
            _gameConsoleCheckBox.IsChecked = _settings.EnableGameConsole;
            _downloadDetailsCheckBox.IsChecked = _settings.ShowDownloadDetails;
            _ramBox.Text = _settings.RamMb.ToString();
            SyncPlayerNameText(_settings.PlayerName);
            _selectedSkinPath = string.IsNullOrWhiteSpace(_settings.SkinSourcePath) ? null : _settings.SkinSourcePath;
            _extraArgsBox.Text = _settings.ExtraLaunchArguments;
            _autoUpdateCheckBox.IsChecked = _settings.EnableAutoUpdate;
            BindCustomColorBoxes();
            _dynamicBackgroundCheckBox.IsChecked = _settings.DynamicBackground;
            _panelOpacitySlider.Value = OpacityToTransparencyPercent(_settings.PanelOpacity);
            _sidebarOpacitySlider.Value = OpacityToTransparencyPercent(_settings.SidebarOpacity);
            _backgroundEffectOpacitySlider.Value = RatioToPercent(_settings.BackgroundEffectOpacity);
            UpdateOpacityValueLabels();
            UpdatePlayerPreview();
            UpdatePlayerNameMode();
            UpdateSkinStatus();
        }
        finally
        {
            _bindingSettings = false;
        }
    }

    private void RenderManifest(string? selectedNewsKey = null)
    {
        if (_manifest is null)
        {
            return;
        }

        Title = $"{_manifest.ServerName} Launcher";
        _serverNameText.Text = _manifest.ServerName;
        _serverVersionText.Text = $"Сборка {_manifest.PackVersion} | Minecraft {_manifest.MinecraftVersion} | Лаунчер {CurrentLauncherVersion()}";
        _packInfoText.Text = $"Версия сборки: {_manifest.PackVersion}\nMinecraft: {_manifest.MinecraftVersion}";
        _loaderInfoText.Text = $"Loader: {_manifest.Loader} {_manifest.LoaderVersion}";
        _installInfoText.Text = $"Папка игры: {_settings.InstallDirectory}";
        _packPreviewText.Text = $"{_manifest.PackVersion} / Minecraft {_manifest.MinecraftVersion}";
        _loaderPreviewText.Text = $"{_manifest.Loader} {_manifest.LoaderVersion}";
        _launcherPreviewText.Text = CurrentLauncherVersion();
        UpdatePlayerPreview();
        _sidebarStatusText.Text = "Manifest загружен";

        _homeChangelogList.Children.Clear();
        foreach (var item in _manifest.Changelog.Take(4))
        {
            _homeChangelogList.Children.Add(RegisterMuted(new TextBlock
            {
                Text = "- " + item,
                TextWrapping = TextWrapping.Wrap
            }));
        }

        RenderNewsList(selectedNewsKey);
        RenderMapLink();
    }

    private void RenderNewsList(string? selectedNewsKey)
    {
        _newsList.ItemsSource = _manifest?.News;
        if (_manifest is null || _manifest.News.Count == 0)
        {
            RenderNewsItem(null);
            return;
        }

        var selectedItem = _manifest.News.FirstOrDefault(item => NewsIdentity(item) == selectedNewsKey)
            ?? _manifest.News[0];
        _newsList.SelectedItem = selectedItem;
        RenderNewsItem(selectedItem);
    }

    private async void RenderNewsItem(NewsItem? item)
    {
        _newsMediaCts?.Cancel();
        _newsMediaCts?.Dispose();
        _newsMediaCts = CancellationTokenSource.CreateLinkedTokenSource(CurrentToken());
        var mediaToken = _newsMediaCts.Token;
        _newsImage.IsVisible = false;
        _newsImage.Source = null;
        _newsWebView.IsVisible = false;
        _openNewsButton.IsVisible = false;

        if (item is null)
        {
            _newsTitleText.Text = "Новостей пока нет";
            _newsDateText.Text = "";
            _newsBodyText.Text = "Здесь появятся записи из manifest.json.";
            return;
        }

        _newsTitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "Новость" : item.Title;
        _newsDateText.Text = item.Date;
        var kind = (item.Kind ?? NewsItem.TextKind).Trim().ToLowerInvariant();

        if (kind == NewsItem.ImageKind && !string.IsNullOrWhiteSpace(item.Url))
        {
            _newsBodyText.Text = string.IsNullOrWhiteSpace(item.Text) ? item.Title : item.Text;
            _newsImage.IsVisible = true;
            try
            {
                var imageUrl = AddCacheBuster(item.Url);
                var imageBytes = await _newsHttpClient.GetByteArrayAsync(imageUrl, mediaToken);
                mediaToken.ThrowIfCancellationRequested();
                using var imageStream = new MemoryStream(imageBytes);
                _newsImage.Source = new Bitmap(imageStream);
            }
            catch (OperationCanceledException) when (mediaToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _newsImage.IsVisible = false;
                _newsBodyText.Text = "Не удалось открыть картинку: " + ex.Message;
            }

            return;
        }

        if (kind == NewsItem.HtmlKind && !string.IsNullOrWhiteSpace(item.Url))
        {
            _newsBodyText.Text = item.Text;
            _newsWebView.IsVisible = true;
            _newsWebView.Navigate(AddCacheBuster(item.Url));
            _openNewsButton.IsVisible = true;
            return;
        }

        _newsBodyText.Text = string.IsNullOrWhiteSpace(item.Text) ? item.Title : item.Text;
    }

    private void RenderMapLink()
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            _mapUrlText.Text = "Ссылка на карту не указана.";
            _mapStatusText.Text = "Ссылка на карту не указана.";
            _openMapButton.IsEnabled = false;
            _reloadMapButton.IsEnabled = false;
            return;
        }

        _mapUrlText.Text = Preferred3DMapUrl(_manifest.BlueMapUrl);
        _mapStatusText.Text = "BlueMap загружена внутри лаунчера.";
        _openMapButton.IsEnabled = true;
        _reloadMapButton.IsEnabled = true;
        _mapWebView.Navigate(Preferred3DMapUrl(_manifest.BlueMapUrl));
    }

    private void OpenMap()
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.BlueMapUrl))
        {
            _mapStatusText.Text = "В manifest.json не указана ссылка blueMapUrl.";
            return;
        }

        OpenUrl(Preferred3DMapUrl(_manifest.BlueMapUrl));
    }

    private void OpenInstallDirectory()
    {
        var path = (_installDirectoryBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = MacSettingsService.DefaultInstallDirectory();
        }

        Directory.CreateDirectory(path);
        OpenUrl(path);
    }

    private void UpdateLaunchReadinessStatus()
    {
        if (!_gameFilesReady || _manifest is null)
        {
            return;
        }

        var launchIssues = _launchService.ValidateReady(_manifest, _settings);
        if (launchIssues.Count == 0)
        {
            _mainStatusText.Text = "Minecraft готов к запуску.";
            _sidebarStatusText.Text = "Готово";
            return;
        }

        _mainStatusText.Text = "Файлы скачаны, но запуск требует настройки: " + string.Join("; ", launchIssues.Take(3));
        _sidebarStatusText.Text = "Нужна настройка";
    }

    private void UpdateSkinStatus()
    {
        var cached = _skinService.CachedSkinPath(_settings);
        var selected = _selectedSkinPath ?? _settings.SkinSourcePath;
        _skinStatusText.Text = File.Exists(cached)
            ? $"Установлен локальный скин для {_settings.PlayerName}."
            : File.Exists(selected)
                ? "Скин выбран. Нажмите \"Сохранить скин\"."
                : "Выберите PNG/JPG скин.";
    }

    private async Task ConfirmPlayerNameAsync()
    {
        var playerName = (_homePlayerNameBox.Text ?? "").Trim();
        if (!IsValidMinecraftName(playerName))
        {
            _playerPreviewText.Text = "Ник должен быть 3-16 символов: латиница, цифры или _.";
            return;
        }

        SyncPlayerNameText(playerName);
        _settings.PlayerName = playerName;
        await SaveSettingsFromUiAsync();
        UpdatePlayerNameMode();
        UpdatePlayerPreview();
        _mainStatusText.Text = $"Ник {playerName} подтвержден.";
        _sidebarStatusText.Text = "Ник подтвержден";
    }

    private void PlayerNameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingPlayerName)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            if (ReferenceEquals(textBox, _homePlayerNameBox))
            {
                UpdatePlayerPreview();
                _confirmPlayerNameButton.IsEnabled = !string.IsNullOrWhiteSpace(_homePlayerNameBox.Text);
                return;
            }

            SyncPlayerNameText(textBox.Text ?? "");
        }

        UpdatePlayerPreview();
        UpdatePlayerNameMode();
    }

    private void UpdatePlayerPreview()
    {
        var playerName = CurrentPlayerName();
        _playerPreviewText.Text = string.IsNullOrWhiteSpace(playerName)
            ? PlayerNamePlaceholder
            : $"В игре будет: {playerName}";
    }

    private string CurrentPlayerName()
    {
        return _homeNameInputPanel.IsVisible
            ? (_homePlayerNameBox.Text ?? "").Trim()
            : (_playerNameBox.Text ?? "").Trim();
    }

    private void UpdatePlayerNameMode()
    {
        var playerName = (_playerNameBox.Text ?? "").Trim();
        var confirmed = IsValidMinecraftName(playerName);

        _homeNameInputPanel.IsVisible = !confirmed;
        _homeNameLockedPanel.IsVisible = confirmed;
        _lockedPlayerNameText.Text = playerName;
        _confirmPlayerNameButton.IsEnabled = !string.IsNullOrWhiteSpace(_homePlayerNameBox.Text);

        if (confirmed && _homePlayerNameBox.Text != playerName)
        {
            _homePlayerNameBox.Text = playerName;
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
            if (_homePlayerNameBox.Text != playerName)
            {
                _homePlayerNameBox.Text = playerName;
            }

            if (_playerNameBox.Text != playerName)
            {
                _playerNameBox.Text = playerName;
            }
        }
        finally
        {
            _syncingPlayerName = false;
        }
    }

    private async Task RefreshLauncherUpdateGateAsync()
    {
        try
        {
            var update = await FindAvailableLauncherUpdateAsync(CurrentToken());
            _launcherUpdateRequired = update is not null;
            _requiredLauncherVersion = update?.Version;
            if (_launcherUpdateRequired)
            {
                ShowLauncherUpdateRequiredStatus();
            }

            UpdatePrimaryButtonState();
        }
        catch
        {
            UpdatePrimaryButtonState();
        }
    }

    private async Task<LauncherUpdateManifest?> FindAvailableLauncherUpdateAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _updateHttpClient.GetStreamAsync(LauncherEndpoints.UpdateManifestUrl, cancellationToken);
        var update = await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream, cancellationToken: cancellationToken);
        if (update is null || string.IsNullOrWhiteSpace(update.Version))
        {
            return null;
        }

        return IsNewerThanCurrent(update.Version) && ResolveMacUpdateAsset(update) is not null ? update : null;
    }

    private async Task<bool> CheckAndApplyLauncherUpdateAsync()
    {
        if (!_settings.EnableAutoUpdate)
        {
            return false;
        }

        LauncherUpdateManifest? update;
        try
        {
            update = await FindAvailableLauncherUpdateAsync(CurrentToken());
        }
        catch
        {
            _progressText.Text = "Автообновление лаунчера недоступно, продолжаю запуск.";
            return false;
        }

        if (update is null)
        {
            return false;
        }

        var asset = ResolveMacUpdateAsset(update);
        if (asset is null)
        {
            return false;
        }

        SetBusy(true, $"Скачиваю обновление лаунчера {update.Version}...");
        var prepared = await PrepareMacLauncherUpdateAsync(update, asset, CurrentToken());
        _progressText.Text = "Обновление скачано. Перезапускаю лаунчер...";
        StartMacUpdaterScript(prepared.ExtractPath, prepared.TargetDirectory, prepared.ProcessId);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }

        return true;
    }

    private async Task<PreparedMacLauncherUpdate> PrepareMacLauncherUpdateAsync(
        LauncherUpdateManifest update,
        LauncherUpdateAsset asset,
        CancellationToken cancellationToken)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Minivibe",
            "Updates",
            update.Version,
            MacUpdatePlatformKey());
        Directory.CreateDirectory(updateRoot);

        var zipPath = Path.Combine(updateRoot, "launcher-update.zip");
        await DownloadFileAsync(asset.Url, zipPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(asset.Sha256))
        {
            var actualHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHA-256 обновления macOS-лаунчера не совпал.");
            }
        }

        var extractPath = Path.Combine(updateRoot, "extracted");
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractPath);
        var processPath = Environment.ProcessPath;
        var targetDirectory = !string.IsNullOrWhiteSpace(processPath)
            ? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

        return new PreparedMacLauncherUpdate(extractPath, targetDirectory, Environment.ProcessId);
    }

    private static LauncherUpdateAsset? ResolveMacUpdateAsset(LauncherUpdateManifest update)
    {
        return update.Platforms.TryGetValue(MacUpdatePlatformKey(), out var asset)
            && !string.IsNullOrWhiteSpace(asset.Url)
            ? asset
            : null;
    }

    private static string MacUpdatePlatformKey()
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac-arm64" : "mac-x64";
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = await _updateHttpClient.GetStreamAsync(url, cancellationToken);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void StartMacUpdaterScript(string sourceDirectory, string targetDirectory, int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"minivibe-updater-{Guid.NewGuid():N}.sh");
        var script = $$"""
        #!/bin/sh
        set -eu
        source={{ShellQuote(sourceDirectory)}}
        target={{ShellQuote(targetDirectory)}}
        pid={{processId}}
        attempts=0
        while kill -0 "$pid" 2>/dev/null && [ "$attempts" -lt 300 ]; do
          attempts=$((attempts + 1))
          sleep 0.2
        done
        mkdir -p "$target"
        cp -R "$source"/. "$target"/
        chmod +x "$target/MinivibeMac" "$target/Run-Minivibe.command" 2>/dev/null || true
        cd "$target"
        if [ -x "$target/MinivibeMac" ]; then
          nohup "$target/MinivibeMac" >/dev/null 2>&1 &
        elif [ -x "$target/Run-Minivibe.command" ]; then
          nohup "$target/Run-Minivibe.command" >/dev/null 2>&1 &
        fi
        rm -f "$0"
        """;
        File.WriteAllText(scriptPath, script, Encoding.ASCII);
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            ArgumentList = { scriptPath }
        });
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static bool IsNewerThanCurrent(string remoteVersion)
    {
        if (!Version.TryParse(remoteVersion, out var parsedRemoteVersion))
        {
            return false;
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var comparison = NormalizeVersion(parsedRemoteVersion).CompareTo(NormalizeVersion(currentVersion));
        if (comparison != 0)
        {
            return comparison > 0;
        }

        return CurrentLauncherVersion().Contains('-', StringComparison.Ordinal)
            && !remoteVersion.Contains('-', StringComparison.Ordinal);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    private void ShowLauncherUpdateRequiredStatus()
    {
        var version = string.IsNullOrWhiteSpace(_requiredLauncherVersion) ? "новая версия" : $"версия {_requiredLauncherVersion}";
        _mainStatusText.Text = $"Доступна {version} лаунчера. Обновите minivibe перед запуском Minecraft.";
        _sidebarStatusText.Text = "Нужно обновить лаунчер";
        _progressText.Text = "Кнопка игры заблокирована до обновления лаунчера.";
    }

    private async Task ShowPatchNotesIfNeededAsync()
    {
        var currentVersion = CurrentLauncherVersion();
        if (string.Equals(_settings.LastSeenLauncherVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.LastSeenLauncherVersion = currentVersion;
        await _settingsService.SaveAsync(_settings);
    }

    private void UpdatePrimaryButtonState()
    {
        _playButton.Content = _launcherUpdateRequired
            ? "Обновите лаунчер"
            : _gameFilesReady
                ? "Играть"
                : "Установить";
        _playButton.IsEnabled = !_busy && !_launcherUpdateRequired;
        _repairButton.IsEnabled = !_busy;
        _refreshManifestButton.IsEnabled = !_busy;
        _refreshNewsButton.IsEnabled = !_busy;
        _chooseSkinButton.IsEnabled = !_busy;
        _installSkinButton.IsEnabled = !_busy;
        _saveSettingsButton.IsEnabled = !_busy;
        _browseInstallDirectoryButton.IsEnabled = !_busy;
        if (_launcherUpdateRequired && !_busy)
        {
            ShowLauncherUpdateRequiredStatus();
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        _progressBar.IsIndeterminate = busy;
        _progressText.Text = message;
        UpdatePrimaryButtonState();
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
            _progressText.Text = "Операция отменена.";
        }
        catch (Exception ex)
        {
            var reportPath = await SaveBugReportAsync(ex, "Launcher operation");
            _mainStatusText.Text = ex.Message;
            _sidebarStatusText.Text = "Ошибка";
            _progressText.Text = "Отчет сохранен: " + reportPath;
            AppendLog("[ERR] " + ex);
        }
        finally
        {
            SetBusy(false, _progressText.Text ?? "");
        }
    }

    private CancellationToken CurrentToken()
    {
        return _operationCts?.Token ?? CancellationToken.None;
    }

    private async Task<string> SaveBugReportAsync(Exception exception, string context)
    {
        var reportsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Minivibe",
            "Reports");
        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(reportsDirectory, $"bug-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder()
            .AppendLine("minivibe launcher bug report")
            .AppendLine($"Time: {DateTimeOffset.Now:O}")
            .AppendLine($"Context: {context}")
            .AppendLine($"App version: {CurrentLauncherVersion()}")
            .AppendLine($"OS: {Environment.OSVersion}")
            .AppendLine($"User: {Environment.UserName}")
            .AppendLine($"Manifest URL: {LauncherEndpoints.ManifestUrl}")
            .AppendLine($"Install directory: {_settings.InstallDirectory}")
            .AppendLine($"Shaders enabled: {_settings.EnableShaders}")
            .AppendLine($"Auto update enabled: {_settings.EnableAutoUpdate}")
            .AppendLine(_manifest is null ? "" : $"Server: {_manifest.ServerName}")
            .AppendLine(_manifest is null ? "" : $"Pack: {_manifest.PackVersion}")
            .AppendLine(_manifest is null ? "" : $"Minecraft: {_manifest.MinecraftVersion}")
            .AppendLine(_manifest is null ? "" : $"Loader: {_manifest.Loader} {_manifest.LoaderVersion}")
            .AppendLine()
            .AppendLine("Exception:")
            .AppendLine(exception.ToString())
            .ToString();
        await File.WriteAllTextAsync(path, report, Encoding.UTF8, CurrentToken());
        return path;
    }

    private void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _consoleBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
            _consoleBox.CaretIndex = _consoleBox.Text?.Length ?? 0;
        });
    }

    private void ShowPanel(Control panel, Button? navButton)
    {
        _homePanel.IsVisible = false;
        _newsPanel.IsVisible = false;
        _mapPanel.IsVisible = false;
        _skinsPanel.IsVisible = false;
        _settingsPanel.IsVisible = false;
        panel.IsVisible = true;
        _activeNavButton = navButton;
        ApplyVisualSettings();
    }

    private void VisualSettingChanged()
    {
        if (_bindingSettings)
        {
            return;
        }

        SaveCustomColorsFromUi();
        _settings.DynamicBackground = _dynamicBackgroundCheckBox.IsChecked == true;
        _settings.PanelOpacity = TransparencyPercentToOpacity(_panelOpacitySlider.Value);
        _settings.SidebarOpacity = TransparencyPercentToOpacity(_sidebarOpacitySlider.Value);
        _settings.BackgroundEffectOpacity = PercentToRatio(_backgroundEffectOpacitySlider.Value);
        UpdateOpacityValueLabels();
        ApplyVisualSettings();
        QueueVisualSettingsSave();
    }

    private void QueueVisualSettingsSave()
    {
        _visualSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _visualSaveCts = cts;
        _ = SaveVisualSettingsDebouncedAsync(cts);
    }

    private async Task SaveVisualSettingsDebouncedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            await _settingsService.SaveAsync(_settings);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_visualSaveCts, cts))
            {
                _visualSaveCts = null;
            }

            cts.Dispose();
        }
    }

    private void UpdateOpacityValueLabels()
    {
        _panelOpacityValueText.Text = $"{Math.Round(_panelOpacitySlider.Value)}%";
        _sidebarOpacityValueText.Text = $"{Math.Round(_sidebarOpacitySlider.Value)}%";
        _backgroundEffectOpacityValueText.Text = $"{Math.Round(_backgroundEffectOpacitySlider.Value)}%";
    }

    private static double TransparencyPercentToOpacity(double value)
    {
        return 1d - Math.Clamp(value, 0, 28) / 100d;
    }

    private static double OpacityToTransparencyPercent(double value)
    {
        return Math.Clamp((1d - value) * 100d, 0, 28);
    }

    private static double PercentToRatio(double value)
    {
        return Math.Clamp(value, 0, 100) / 100d;
    }

    private static double RatioToPercent(double value)
    {
        return Math.Clamp(value, 0, 1) * 100d;
    }

    private void ApplyVisualSettings()
    {
        var palette = ThemePalette.From(_settings.VisualTheme);
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

        Background = new SolidColorBrush(background);
        _scene.Background = new SolidColorBrush(background);
        _dynamicLayer.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(gradientStart, 0),
                new GradientStop(background, 0.55),
                new GradientStop(gradientEnd, 1)
            ]
        };
        _dynamicLayer.Opacity = _settings.DynamicBackground ? Math.Clamp(_settings.BackgroundEffectOpacity, 0, 1) : 0;
        _sidebar.Background = new SolidColorBrush(ColorWithOpacity(sidebar, _settings.SidebarOpacity));
        _sidebar.BorderBrush = new SolidColorBrush(border);
        _sidebar.BorderThickness = new Thickness(1);

        foreach (var card in _cards)
        {
            card.Background = new SolidColorBrush(ColorWithOpacity(surface, _settings.PanelOpacity));
            card.BorderBrush = new SolidColorBrush(border);
            card.BorderThickness = new Thickness(1);
        }

        _homeNameLockedPanel.Background = new SolidColorBrush(surfaceAlt);
        _homeNameLockedPanel.BorderBrush = new SolidColorBrush(border);
        _homeNameLockedPanel.BorderThickness = new Thickness(1);

        foreach (var textBlock in _primaryTexts)
        {
            textBlock.Foreground = new SolidColorBrush(text);
        }

        foreach (var textBlock in _mutedTexts)
        {
            textBlock.Foreground = new SolidColorBrush(muted);
        }

        foreach (var textBox in _textBoxes)
        {
            textBox.Background = new SolidColorBrush(surfaceAlt);
            textBox.Foreground = new SolidColorBrush(text);
            textBox.BorderBrush = new SolidColorBrush(border);
        }

        foreach (var checkBox in _checkBoxes)
        {
            checkBox.Foreground = new SolidColorBrush(text);
        }

        _newsList.Background = new SolidColorBrush(surfaceAlt);
        _newsList.Foreground = new SolidColorBrush(text);
        _newsList.BorderBrush = new SolidColorBrush(border);

        foreach (var button in _primaryButtons)
        {
            button.Background = new SolidColorBrush(accent);
            button.Foreground = new SolidColorBrush(accentText);
            button.BorderBrush = new SolidColorBrush(accent);
        }

        foreach (var button in _secondaryButtons)
        {
            button.Background = new SolidColorBrush(surfaceAlt);
            button.Foreground = new SolidColorBrush(text);
            button.BorderBrush = new SolidColorBrush(border);
        }

        foreach (var button in _navButtons)
        {
            var active = ReferenceEquals(button, _activeNavButton);
            button.Background = active ? new SolidColorBrush(accent) : Brushes.Transparent;
            button.Foreground = active ? new SolidColorBrush(accentText) : new SolidColorBrush(text);
            button.BorderBrush = active ? new SolidColorBrush(accent) : Brushes.Transparent;
        }

        _progressBar.Foreground = new SolidColorBrush(accent);
        _progressBar.Background = new SolidColorBrush(surfaceAlt);
    }

    private void BindCustomColorBoxes()
    {
        _customBackgroundColorBox.Text = _settings.CustomBackgroundColor;
        _customSidebarColorBox.Text = _settings.CustomSidebarColor;
        _customSurfaceColorBox.Text = _settings.CustomSurfaceColor;
        _customBorderColorBox.Text = _settings.CustomBorderColor;
        _customTextColorBox.Text = _settings.CustomTextColor;
        _customMutedTextColorBox.Text = _settings.CustomMutedTextColor;
        _customAccentColorBox.Text = _settings.CustomAccentColor;
        _customGradientStartColorBox.Text = _settings.CustomGradientStartColor;
        _customGradientEndColorBox.Text = _settings.CustomGradientEndColor;
    }

    private void SaveCustomColorsFromUi()
    {
        _settings.CustomBackgroundColor = NormalizeHexColor(_customBackgroundColorBox.Text ?? "");
        _settings.CustomSidebarColor = NormalizeHexColor(_customSidebarColorBox.Text ?? "");
        _settings.CustomSurfaceColor = NormalizeHexColor(_customSurfaceColorBox.Text ?? "");
        _settings.CustomBorderColor = NormalizeHexColor(_customBorderColorBox.Text ?? "");
        _settings.CustomTextColor = NormalizeHexColor(_customTextColorBox.Text ?? "");
        _settings.CustomMutedTextColor = NormalizeHexColor(_customMutedTextColorBox.Text ?? "");
        _settings.CustomAccentColor = NormalizeHexColor(_customAccentColorBox.Text ?? "");
        _settings.CustomGradientStartColor = NormalizeHexColor(_customGradientStartColorBox.Text ?? "");
        _settings.CustomGradientEndColor = NormalizeHexColor(_customGradientEndColorBox.Text ?? "");
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

    private static Color ReadColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return Color.Parse(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static Color ColorWithOpacity(Color color, double opacity)
    {
        return Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);
    }

    private static Color EnsureReadable(Color requested, double minimumContrast, params Color[] backgrounds)
    {
        if (backgrounds.All(background => ContrastRatio(requested, background) >= minimumContrast))
        {
            return requested;
        }

        var white = Colors.White;
        var black = Colors.Black;
        var whiteScore = backgrounds.Min(background => ContrastRatio(white, background));
        var blackScore = backgrounds.Min(background => ContrastRatio(black, background));
        return whiteScore >= blackScore ? white : black;
    }

    private static Color BestReadableText(Color background)
    {
        return ContrastRatio(Colors.White, background) >= ContrastRatio(Colors.Black, background)
            ? Colors.White
            : Colors.Black;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
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

    private void ConfigureOpacitySlider(Slider slider, TextBlock valueText, double minimum, double maximum)
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.SmallChange = 1;
        slider.LargeChange = 4;
        slider.ValueChanged += (_, _) => VisualSettingChanged();
        RegisterMuted(valueText);
    }

    private Control ColorRow(string label, TextBox box)
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*"),
            Children =
            {
                RegisterMuted(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center
                }),
                WithColumn(box, 1)
            }
        };
    }

    private Control SliderRow(string label, Slider slider, TextBlock valueText)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        DockRight(valueText),
                        RegisterMuted(new TextBlock { Text = label })
                    }
                },
                WithRow(slider, 1)
            }
        };
        return grid;
    }

    private Border Card(Control child)
    {
        var card = new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(8),
            Child = child
        };
        _cards.Add(card);
        return card;
    }

    private TextBlock PageTitle(string text)
    {
        return RegisterPrimary(new TextBlock
        {
            Text = text,
            FontSize = 34,
            FontWeight = FontWeight.Black
        });
    }

    private TextBlock SectionTitle(string text)
    {
        return RegisterPrimary(new TextBlock
        {
            Text = text,
            FontSize = 20,
            FontWeight = FontWeight.Bold
        });
    }

    private TextBlock RegisterPrimary(TextBlock textBlock)
    {
        _primaryTexts.Add(textBlock);
        return textBlock;
    }

    private TextBlock RegisterMuted(TextBlock textBlock)
    {
        _mutedTexts.Add(textBlock);
        return textBlock;
    }

    private TextBox RegisterTextBox(TextBox textBox)
    {
        _textBoxes.Add(textBox);
        return textBox;
    }

    private CheckBox Toggle(CheckBox checkBox, string text)
    {
        checkBox.Content = text;
        _checkBoxes.Add(checkBox);
        return checkBox;
    }

    private Button PrimaryButton(Button button)
    {
        button.Padding = new Thickness(16, 9);
        _primaryButtons.Add(button);
        return button;
    }

    private Button SecondaryButton(Button button)
    {
        button.Padding = new Thickness(14, 8);
        _secondaryButtons.Add(button);
        return button;
    }

    private Button NavButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 11)
        };
        _navButtons.Add(button);
        button.Click += async (_, _) => await action();
        return button;
    }

    private Button NavButton(string text, Action action)
    {
        return NavButton(text, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private static Control DockRight(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    private static Control WithColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control WithRow(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static string NewsIdentity(NewsItem? item)
    {
        return item is null
            ? ""
            : string.Join("|", item.Title, item.Date, item.Kind, item.Url, item.Text);
    }

    private static string Preferred3DMapUrl(string mapUrl)
    {
        return mapUrl.EndsWith(":flat", StringComparison.OrdinalIgnoreCase)
            ? mapUrl[..^":flat".Length] + ":perspective"
            : mapUrl;
    }

    private static string AddCacheBuster(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + "minivibe=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static void OpenUrl(string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(target);
            Process.Start(startInfo);
        }
        catch
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    private static int CountOutdated(IEnumerable<FileStatusItem> statuses)
    {
        return statuses.Count(item => item.Status != FileSyncService.StatusCurrent);
    }

    private static string CurrentLauncherVersion()
    {
        var informationalVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0];
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return version.Build == 0
            ? $"{version.Major}.{version.Minor}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

internal static class ButtonExtensions
{
    public static Button WithClick(this Button button, Action<object?> action)
    {
        button.Click += (_, args) => action(args);
        return button;
    }

    public static Button WithAsyncClick(this Button button, Func<Task> action)
    {
        button.Click += async (_, _) => await action();
        return button;
    }
}

internal sealed record PreparedMacLauncherUpdate(
    string ExtractPath,
    string TargetDirectory,
    int ProcessId);

internal sealed record ThemePalette(
    Color Background,
    Color Sidebar,
    Color Surface,
    Color SurfaceAlt,
    Color Border,
    Color Text,
    Color Muted)
{
    public static ThemePalette From(string theme)
    {
        return theme switch
        {
            "Midnight" => new(
                Color.Parse("#050A18"),
                Color.Parse("#0B1224"),
                Color.Parse("#161F34"),
                Color.Parse("#1C2740"),
                Color.Parse("#354462"),
                Color.Parse("#F0F6FF"),
                Color.Parse("#A1AEC6")),
            _ => new(
                Color.Parse("#090A0F"),
                Color.Parse("#11131A"),
                Color.Parse("#1A1D27"),
                Color.Parse("#242838"),
                Color.Parse("#343A4C"),
                Color.Parse("#F5F7FA"),
                Color.Parse("#A8AFBD"))
        };
    }
}

internal static class AccentPalette
{
    public static Color From(string accent)
    {
        return accent switch
        {
            "Blue" => Color.Parse("#3B82F6"),
            "Emerald" => Color.Parse("#10B981"),
            "Amber" => Color.Parse("#F59E0B"),
            _ => Color.Parse("#D84C5B")
        };
    }
}
