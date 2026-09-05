using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UdpRecorder _recorder = new();
    private readonly ObservableCollection<string> _liveRows = new();
    private readonly ObservableCollection<string> _logRows = new();
    private readonly ObservableCollection<LapOption> _compareLapOptions = new();
    private readonly ObservableCollection<string> _compareRows = new();
    private readonly ObservableCollection<Control> _compareLegendItems = new();
    private readonly ObservableCollection<string> _classificationRows = new();
    private readonly ObservableCollection<string> _raceReportRows = new();
    private readonly Dictionary<int, TextBox> _aliasBoxes = new();
    private readonly Dictionary<int, TextBox> _shortAliasBoxes = new();
    private readonly List<Action> _sessionContextLoaders = new();
    private readonly List<Border> _compareSlotContainers = new();
    private readonly DispatcherTimer _timer;

    private TextBlock _statusText = null!;
    private TextBlock _packetsText = null!;
    private TextBlock _samplesText = null!;
    private TextBlock _sessionText = null!;
    private TextBlock _qualityText = null!;
    private TextBox _portText = null!;
    private TextBox _rootText = null!;
    private CheckBox _autoZipCheck = null!;
    private ComboBox _ersModeCombo = null!;
    private TextBlock _ersStatusText = null!;
    private TextBlock _raceLapsText = null!;
    private TextBlock _raceTyresText = null!;
    private TextBlock _racePitText = null!;
    private TextBlock _raceErsText = null!;
    private TextBlock _raceAdvisorConfidenceText = null!;
    private Button _raceOverlayButton = null!;
    private Button _raceOverlayLayoutButton = null!;
    private RaceEngineerOverlayWindow? _raceOverlayWindow;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private ListBox _sessionList = null!;
    private TextBlock _selectedSessionText = null!;
    private TextBlock _technicalManifestText = null!;
    private TextBlock _globalSessionContext = null!;
    private TextBlock _classificationHeading = null!;
    private TextBlock _classificationNote = null!;
    private ComboBox _compareMetric = null!;
    private CheckBox _compareCleanOnly = null!;
    private TextBlock _compareStatus = null!;
    private TextBlock _compareMetricHelp = null!;
    private TextBlock _referenceText = null!;
    private TextBox _zoomFromText = null!;
    private TextBox _zoomToText = null!;
    private SimpleLineChart _compareChart = null!;
    private TrackMapControl _trackMapControl = null!;
    private TrackDetailControl _trackDetailControl = null!;
    private ComboBox _trackMapMetric = null!;
    private ComboBox _trackMapContrast = null!;
    private TextBlock _trackMapStatus = null!;
    private ListBox _trackMapCornerList = null!;
    private ListBox _trackMapInsightList = null!;
    private TextBlock _trackDetailStatus = null!;
    private ListBox _trackDetailZoneList = null!;
    private ComboBox _raceReportDriver = null!;
    private ComboBox _raceReportView = null!;
    private CheckBox _raceReportCleanOnly = null!;
    private CheckBox _raceReportProblemsOnly = null!;
    private TextBlock _raceReportStatus = null!;
    private TextBlock _raceReportLegend = null!;
    private TextBlock _raceReportSummary = null!;
    private Button _raceReportLegendToggle = null!;
    private StackPanel _raceReportTablePanel = null!;
    private List<RaceReportDriverOption> _raceReportDrivers = new();
    private readonly ComboBox[] _compareDriverBoxes = new ComboBox[6];
    private readonly ComboBox[] _compareLapBoxes = new ComboBox[6];
    private StackPanel _driverAliasPanel = null!;
    private TextBlock _driverAliasStatus = null!;
    private TextBlock _settingsStatus = null!;
    private TextBlock _measuredRateText = null!;
    private List<DriverOption> _compareDrivers = new();
    private List<LapOption> _lastComparedLaps = new();
    private int? _zoomFromM;
    private int? _zoomToM;
    private bool _updatingCompareSlots;
    private bool _updatingTrackZoneSelection;
    private bool _aliasesDirty;
    private int _visibleCompareSlots = 2;
    private IReadOnlyList<SessionRetentionCandidate> _retentionPreview = Array.Empty<SessionRetentionCandidate>();
    private TrackMapRenderData? _lastTrackMapData;
    private bool _busy;
    private bool _closingAfterStop;
    private bool _closeStopInProgress;

    private sealed record ErsModeChoice(ErsAutopilotOperatingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    public MainWindow()
    {
        _settings = AppSettingsService.Load();
        Title = $"{AppInfo.Name} v{AppInfo.Version}";
        Width = 1440;
        Height = 860;
        MinWidth = 1150;
        MinHeight = 680;
        FontSize = 14 * _settings.UiScalePercent / 100.0;
        Background = Hex(0x101318);

        _recorder.Updated += () => Dispatcher.UIThread.Post(UpdateLiveUi);
        _recorder.Log += message => Dispatcher.UIThread.Post(() => AddLog(message));

        Content = BuildRoot();
        RefreshSessions();
        UpdateLiveUi();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateLiveUi();
        _timer.Start();
        Closing += OnWindowClosing;
        Closed += (_, _) => _raceOverlayWindow?.Close();
    }




    private static readonly Color[] ChartPalette =
    {
        Color.FromRgb(86, 156, 214),
        Color.FromRgb(244, 191, 117),
        Color.FromRgb(184, 215, 163),
        Color.FromRgb(197, 134, 192),
        Color.FromRgb(220, 90, 90),
        Color.FromRgb(78, 201, 176)
    };

    private static Color ChartColor(int index) => ChartPalette[index % ChartPalette.Length];

    private static readonly IReadOnlyDictionary<string, string> RussianUi = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Live"] = "Запись",
        ["Sessions"] = "Сессии",
        ["Analysis"] = "Анализ",
        ["Race"] = "Гонка",
        ["Settings"] = "Настройки",
        ["Lap Compare"] = "Сравнение кругов",
        ["Track"] = "Трасса",
        ["Overview"] = "Обзор",
        ["Car Setup"] = "Настройки болида",
        ["Driver Compare"] = "Сравнение гонщиков",
        ["Stints"] = "Стинты",
        ["Pits"] = "Пит-стопы",
        ["General"] = "Общие",
        ["Drivers / Aliases"] = "Гонщики / псевдонимы",
        ["Pitwall Live"] = "Запись телеметрии",
        ["Start Recording"] = "Начать запись",
        ["Stop"] = "Остановить",
        ["Refresh"] = "Обновить",
        ["Open session folder"] = "Открыть папку сессии",
        ["Analyze selected session"] = "Проанализировать сессию",
        ["Classification"] = "Классификация",
        ["Technical details"] = "Технические сведения",
        ["Save all aliases"] = "Сохранить все псевдонимы",
        ["Track Map"] = "Карта трассы",
        ["Plot from compare"] = "Построить из сравнения",
        ["Best vs YOU"] = "Лучший против вас",
        ["Clear"] = "Очистить",
        ["Race Report"] = "Отчёт по гонке",
        ["Clean only"] = "Только чистые",
        ["Problems only"] = "Только проблемы",
        ["Column help"] = "Описание столбцов",
        ["Stint Report"] = "Отчёт по стинтам",
        ["Pit Report"] = "Отчёт по пит-стопам",
        ["Build report"] = "Построить отчёт",
        ["Compare"] = "Сравнить",
        ["Comparison slots"] = "Сравниваемые круги",
        ["Clean laps only"] = "Только чистые круги",
        ["Load laps"] = "Загрузить круги",
        ["Plot"] = "Построить",
        ["Export"] = "Экспорт",
        ["Apply"] = "Применить",
        ["Reset"] = "Сбросить",
        ["+ Add comparison"] = "+ Добавить сравнение",
        ["Legend"] = "Легенда",
        ["Save settings"] = "Сохранить настройки",
        ["Preview retention"] = "Предпросмотр очистки",
        ["Delete previewed"] = "Удалить показанные",
        ["Copy logs"] = "Копировать журнал",
        ["Cars"] = "Болиды",
        ["Log"] = "Журнал",
        ["ERS Autopilot"] = "Автопилот ERS",
        ["Open ERS profiles"] = "Открыть профили ERS",
        ["ERS mode"] = "Режим ERS",
        ["Race Engineer"] = "Гоночный инженер",
        ["Last laps"] = "Последние круги",
        ["Tyres"] = "Шины",
        ["Pit stop"] = "Пит-стоп",
        ["Open overlay"] = "Открыть оверлей",
        ["Hide overlay"] = "Скрыть оверлей",
        ["Edit overlay"] = "Настроить оверлей",
        ["Export race summary"] = "Экспорт краткого Excel",
        ["Open race profiles"] = "Открыть профили гонок",
        ["Auto RAR after Stop"] = "Авто-RAR после остановки",
        ["Create RAR with session.sqlite only after Stop"] = "Создавать RAR только с session.sqlite после остановки",
        ["Open Race Engineer overlay when recording starts"] = "Открывать оверлей гоночного инженера при старте записи",
        ["WinRAR path"] = "Путь к WinRAR",
        ["Race overlay"] = "Гоночный оверлей"
    };

    private static IBrush Hex(uint rgb)
    {
        return new SolidColorBrush(Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF)));
    }

    private Control BuildRoot()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        _globalSessionContext = new TextBlock
        {
            Text = "No session selected",
            Foreground = Hex(0xD8E2F0),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8)
        };
        var context = new Border
        {
            Background = Hex(0x1D2630),
            BorderBrush = Hex(0x354255),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _globalSessionContext
        };
        root.Children.Add(context);

        var tabs = new TabControl
        {
            Margin = new Thickness(16),
            ItemsSource = new[]
            {
                new TabItem { Header = "Live", Content = BuildLiveTab() },
                new TabItem { Header = "Sessions", Content = BuildSessionsTab() },
                new TabItem { Header = "Analysis", Content = BuildAnalysisWorkspace() },
                new TabItem { Header = "Race", Content = BuildRaceWorkspace() },
                new TabItem { Header = "Settings", Content = BuildSettingsWorkspace() }
            }
        };
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        if (string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase)) LocalizeControlTree(root);
        return root;
    }

    private static void LocalizeControlTree(Control control)
    {
        if (control is TextBlock text && RussianUi.TryGetValue(text.Text ?? "", out var translatedText)) text.Text = translatedText;
        if (control is Button button && button.Content is string buttonText && RussianUi.TryGetValue(buttonText, out var translatedButton)) button.Content = translatedButton;
        if (control is CheckBox checkBox && checkBox.Content is string checkText && RussianUi.TryGetValue(checkText, out var translatedCheck)) checkBox.Content = translatedCheck;
        if (control is TabItem tab && tab.Header is string tabText && RussianUi.TryGetValue(tabText, out var translatedTab)) tab.Header = translatedTab;
        if (control is Expander expander && expander.Header is string expanderText && RussianUi.TryGetValue(expanderText, out var translatedExpander)) expander.Header = translatedExpander;

        if (control is Panel panel)
            foreach (var child in panel.Children) LocalizeControlTree(child);
        if (control is Border border && border.Child is Control borderChild) LocalizeControlTree(borderChild);
        if (control is ScrollViewer scroll && scroll.Content is Control scrollChild) LocalizeControlTree(scrollChild);
        if (control is ContentControl content && content.Content is Control contentChild) LocalizeControlTree(contentChild);
        if (control is ItemsControl items && items.ItemsSource is System.Collections.IEnumerable source)
            foreach (var item in source) if (item is Control itemControl) LocalizeControlTree(itemControl);
    }

    private Control BuildAnalysisWorkspace() => new TabControl
    {
        ItemsSource = new[]
        {
            new TabItem { Header = "Lap Compare", Content = BuildCompareTab() },
            new TabItem { Header = "Track", Content = BuildTrackMapTab() }
        }
    };

    private Control BuildRaceWorkspace() => new TabControl
    {
        ItemsSource = new[]
        {
            new TabItem { Header = "Overview", Content = BuildRaceReportTab() },
            new TabItem { Header = "Car Setup", Content = BuildCarSetupTab() },
            new TabItem { Header = "Driver Compare", Content = BuildDriverCompareTab() },
            new TabItem { Header = "Stints", Content = BuildStintReportTab() },
            new TabItem { Header = "Pits", Content = BuildPitReportTab() }
        }
    };

    private Control BuildSettingsWorkspace() => new TabControl
    {
        ItemsSource = new[]
        {
            new TabItem { Header = "General", Content = BuildSettingsTab() },
            new TabItem { Header = "Drivers / Aliases", Content = BuildDriversTab() }
        }
    };

    private Control BuildLiveTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("3*,2*")
        };

        var title = new TextBlock
        {
            Text = "Pitwall Live",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetColumnSpan(title, 2);
        grid.Children.Add(title);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(controls, 1);
        Grid.SetColumnSpan(controls, 2);

        _portText = new TextBox { Text = _settings.Port.ToString(CultureInfo.InvariantCulture), Width = 80, PlaceholderText = "Port" };
        _rootText = new TextBox { Text = _settings.RootFolder, Width = 320, PlaceholderText = "Root folder" };
        _autoZipCheck = new CheckBox { Content = "Auto RAR after Stop", IsChecked = _settings.AutoZip, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var ersChoices = ErsModeChoices();
        _ersModeCombo = new ComboBox { ItemsSource = ersChoices, Width = 145, HorizontalAlignment = HorizontalAlignment.Left };
        var configuredErsMode = ErsAutopilotOptions.ParseOperatingMode(_settings.ErsAutopilotMode);
        _ersModeCombo.SelectedItem = ersChoices.First(choice => choice.Mode == configuredErsMode);
        _startButton = new Button { Content = "Start Recording", Width = 150 };
        _stopButton = new Button { Content = "Stop", Width = 100, IsEnabled = false };
        _startButton.Click += (_, _) => StartRecording();
        _stopButton.Click += async (_, _) => await StopRecordingAsync();

        controls.Children.Add(new TextBlock { Text = "Port", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_portText);
        controls.Children.Add(new TextBlock { Text = "Root", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_rootText);
        controls.Children.Add(_autoZipCheck);
        controls.Children.Add(new TextBlock { Text = "ERS mode", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_ersModeCombo);
        controls.Children.Add(_startButton);
        controls.Children.Add(_stopButton);
        grid.Children.Add(controls);

        var raceEngineer = BuildRaceEngineerPanel();
        Grid.SetRow(raceEngineer, 2);
        Grid.SetColumnSpan(raceEngineer, 2);
        grid.Children.Add(raceEngineer);

        var livePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        Grid.SetRow(livePanel, 3);
        Grid.SetColumn(livePanel, 0);
        livePanel.Children.Add(BuildCards());
        var carsTitle = new TextBlock { Text = "Cars", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        Grid.SetRow(carsTitle, 1);
        livePanel.Children.Add(carsTitle);
        var liveList = new ListBox
        {
            ItemsSource = _liveRows,
            FontFamily = FontFamily.Parse("Consolas"),
            Background = Hex(0x161B22),
            Foreground = Brushes.White
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(liveList, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(liveList, ScrollBarVisibility.Auto);
        Grid.SetRow(liveList, 2);
        livePanel.Children.Add(liveList);
        grid.Children.Add(livePanel);

        var logPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetRow(logPanel, 3);
        Grid.SetColumn(logPanel, 1);
        var logHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        logHeader.Children.Add(new TextBlock { Text = "Log", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var copyLogs = new Button { Content = "Copy logs", Width = 110 };
        copyLogs.Click += async (_, _) => await CopyLogsAsync();
        logHeader.Children.Add(copyLogs);
        logPanel.Children.Add(logHeader);
        var logList = new ListBox
        {
            ItemsSource = _logRows,
            FontFamily = FontFamily.Parse("Consolas"),
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            ItemTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock
            {
                Text = value,
                FontFamily = FontFamily.Parse("Consolas"),
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 4, 2)
            }, true)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(logList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(logList, ScrollBarVisibility.Auto);
        Grid.SetRow(logList, 1);
        logPanel.Children.Add(logList);
        grid.Children.Add(logPanel);

        var openProfiles = new Button { Content = "Open ERS profiles", Width = 170, VerticalAlignment = VerticalAlignment.Center };
        openProfiles.Click += (_, _) => OpenErsProfilesFolder();
        _ersStatusText = new TextBlock
        {
            Text = ErsAutopilotStatus.Initial(configuredErsMode).Display,
            Foreground = Hex(0xC7D4E8),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var ersPanel = new Border
        {
            Background = Hex(0x1D2630),
            BorderBrush = Hex(0x354255),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 14,
                Children =
                {
                    new TextBlock { Text = "ERS Autopilot", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    _ersStatusText,
                    openProfiles
                }
            }
        };
        Grid.SetColumn(_ersStatusText, 1);
        Grid.SetColumn(openProfiles, 2);
        Grid.SetRow(ersPanel, 4);
        Grid.SetColumnSpan(ersPanel, 2);
        grid.Children.Add(ersPanel);

        return grid;
    }

    private Control BuildRaceEngineerPanel()
    {
        _raceLapsText = AdvisorValue();
        _raceTyresText = AdvisorValue();
        _racePitText = AdvisorValue();
        _raceErsText = AdvisorValue();
        _raceAdvisorConfidenceText = new TextBlock
        {
            Foreground = Hex(0xAEBBD0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _raceOverlayButton = new Button { Content = "Open overlay", Width = 125 };
        _raceOverlayButton.Click += (_, _) => ToggleRaceEngineerOverlay();
        _raceOverlayLayoutButton = new Button { Content = "Edit overlay", Width = 125 };
        _raceOverlayLayoutButton.Click += (_, _) => ToggleRaceEngineerOverlayLayout();
        var openProfiles = new Button { Content = "Open race profiles", Width = 150 };
        openProfiles.Click += (_, _) => OpenRaceProfilesFolder();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(new TextBlock
        {
            Text = "Race Engineer",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_raceAdvisorConfidenceText, 1);
        header.Children.Add(_raceAdvisorConfidenceText);
        var headerActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            Children = { openProfiles, _raceOverlayButton, _raceOverlayLayoutButton }
        };
        Grid.SetColumn(headerActions, 2);
        header.Children.Add(headerActions);

        var cards = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 8,
            Children =
            {
                AdvisorCard("Last laps", _raceLapsText),
                AdvisorCard("Tyres", _raceTyresText),
                AdvisorCard("Pit stop", _racePitText),
                AdvisorCard("ERS", _raceErsText)
            }
        };
        Grid.SetColumn(cards.Children[1], 1);
        Grid.SetColumn(cards.Children[2], 2);
        Grid.SetColumn(cards.Children[3], 3);
        return new Border
        {
            Background = Hex(0x1D2630),
            BorderBrush = Hex(0x354255),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel { Spacing = 8, Children = { header, cards } }
        };
    }

    private static TextBlock AdvisorValue() => new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 38
    };

    private static Border AdvisorCard(string title, TextBlock value) => new()
    {
        Background = Hex(0x161B22),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(9),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, Foreground = Hex(0xF4BF75), FontWeight = FontWeight.SemiBold },
                value
            }
        }
    };

    private IReadOnlyList<ErsModeChoice> ErsModeChoices()
    {
        var russian = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase);
        return new[]
        {
            new ErsModeChoice(ErsAutopilotOperatingMode.Off, russian ? "Выключен" : "Off"),
            new ErsModeChoice(ErsAutopilotOperatingMode.DryRun, russian ? "Тест без ввода" : "Dry-run"),
            new ErsModeChoice(ErsAutopilotOperatingMode.Live, russian ? "Управление" : "Live control")
        };
    }

    private Control BuildCards()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        _statusText = Card("Status", "Idle");
        _packetsText = Card("Packets", "0");
        _samplesText = Card("Car samples", "0");
        _sessionText = Card("Session", "None");
        _qualityText = Card("Data quality", "Good");
        AddCard(grid, _statusText, 0);
        AddCard(grid, _packetsText, 1);
        AddCard(grid, _samplesText, 2);
        AddCard(grid, _sessionText, 3);
        AddCard(grid, _qualityText, 4);
        return grid;
    }

    private static TextBlock Card(string label, string value)
    {
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        valueText.Tag = label;
        return valueText;
    }

    private static void AddCard(Grid grid, TextBlock valueText, int column)
    {
        var border = new Border
        {
            Background = Hex(0x1D2630),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = valueText.Tag?.ToString() ?? "", Foreground = Brushes.LightGray, FontSize = 12 },
                    valueText
                }
            }
        };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private Control BuildSessionsTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("420,*"),
            Margin = new Thickness(0)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(new TextBlock { Text = "Sessions", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        var refresh = new Button { Content = "Refresh", Width = 100 };
        refresh.Click += (_, _) => RefreshSessions();
        top.Children.Add(refresh);
        Grid.SetColumnSpan(top, 2);
        grid.Children.Add(top);

        _sessionList = new ListBox
        {
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            ItemTemplate = new FuncDataTemplate<SessionListItem>((value, _) => BuildSessionCard(value), true)
        };
        _sessionList.SelectionChanged += (_, _) => UpdateSelectedSession();
        Grid.SetRow(_sessionList, 1);
        Grid.SetColumn(_sessionList, 0);
        grid.Children.Add(_sessionList);

        var details = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            RowSpacing = 10,
            Margin = new Thickness(16, 0, 0, 0)
        };
        _selectedSessionText = new TextBlock
        {
            Text = "Select a session on the left.",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        details.Children.Add(new Border
        {
            Background = Hex(0x1D2630),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = _selectedSessionText
        });
        var openFolder = new Button { Content = "Open session folder", Width = 180 };
        openFolder.Click += (_, _) => OpenSelectedSessionFolder();
        var analyze = new Button { Content = "Analyze selected session", Width = 210 };
        analyze.Click += async (_, _) => await AnalyzeSelectedSessionAsync();
        var exportSummary = new Button { Content = "Export race summary", Width = 190 };
        exportSummary.Click += async (_, _) => await ExportSelectedRaceSummaryAsync();
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 8,
            Children = { openFolder, analyze, exportSummary }
        };
        Grid.SetRow(actions, 1);
        details.Children.Add(actions);

        _classificationHeading = new TextBlock { Text = "Classification", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold };
        Grid.SetRow(_classificationHeading, 2);
        details.Children.Add(_classificationHeading);
        _classificationNote = new TextBlock
        {
            Text = "Classification source has not been evaluated.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_classificationNote, 3);
        details.Children.Add(_classificationNote);
        var classificationList = new ListBox
        {
            ItemsSource = _classificationRows,
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            FontFamily = FontFamily.Parse("Consolas"),
            ItemTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock
            {
                Text = value,
                FontFamily = FontFamily.Parse("Consolas"),
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 4, 2)
            }, true)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(classificationList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(classificationList, ScrollBarVisibility.Auto);
        Grid.SetRow(classificationList, 4);
        details.Children.Add(classificationList);

        _technicalManifestText = new TextBlock
        {
            Text = "No database details loaded.",
            Foreground = Brushes.LightGray,
            FontFamily = FontFamily.Parse("Consolas"),
            TextWrapping = TextWrapping.Wrap
        };
        var technical = new Expander
        {
            Header = "Technical details",
            Content = new ScrollViewer
            {
                Content = _technicalManifestText,
                MaxHeight = 220,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        Grid.SetRow(technical, 5);
        details.Children.Add(technical);
        Grid.SetRow(details, 1);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        return grid;
    }

    private Control BuildSessionCard(SessionListItem? session)
    {
        if (session is null) return new TextBlock { Text = "Unavailable session", Foreground = Brushes.LightGray };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = session.SessionName,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = session.SummaryLine, Foreground = Hex(0xB8C4D6), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = session.QualityLine, Foreground = Hex(0xA8C7A8), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = $"{session.AnalysisState} | classification: {session.ClassificationSource} | setup snapshots: {session.SetupSnapshots:N0}",
            Foreground = Brushes.LightGray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        return new Border
        {
            Background = Hex(0x1A2028),
            BorderBrush = Hex(0x303846),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10),
            Child = stack
        };
    }


    private Control BuildDriversTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(0)
        };

        var title = new TextBlock
        {
            Text = "Drivers / Aliases",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        grid.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(controls, 1);
        var refresh = new Button { Content = "Load standings", Width = 140 };
        refresh.Click += (_, _) => LoadDriverAliasEditor();
        var saveAll = new Button { Content = "Save all aliases", Width = 150 };
        saveAll.Click += (_, _) => SaveAllAliases();
        _driverAliasStatus = new TextBlock
        {
            Text = "Select an analyzed session. Driver aliases load automatically.",
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        controls.Children.Add(refresh);
        controls.Children.Add(saveAll);
        controls.Children.Add(_driverAliasStatus);
        grid.Children.Add(controls);

        _driverAliasPanel = new StackPanel { Spacing = 8 };
        var scroller = new ScrollViewer
        {
            Content = _driverAliasPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Hex(0x161B22)
        };
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);
        _sessionContextLoaders.Add(LoadDriverAliasEditor);
        return grid;
    }



    private Control BuildTrackMapTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,3*,2*"),
            ColumnDefinitions = new ColumnDefinitions("*,320"),
            RowSpacing = 10,
            Margin = new Thickness(0)
        };

        var title = new TextBlock
        {
            Text = "Track Map",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetColumnSpan(title, 2);
        grid.Children.Add(title);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(controls, 1);
        Grid.SetColumnSpan(controls, 2);
        _trackMapMetric = new ComboBox { Width = 170, ItemsSource = TrackMapDataService.Metrics, SelectedIndex = 0 };
        _trackMapMetric.SelectionChanged += (_, _) => ReplotTrackMapIfPossible();
        _trackMapContrast = new ComboBox { Width = 88, ItemsSource = new[] { "x0.5", "x1", "x2", "x4", "x8", "x16" }, SelectedIndex = 2 };
        _trackMapContrast.SelectionChanged += (_, _) => ReplotTrackMapIfPossible();
        var plot = new Button { Content = "Plot from compare", Width = 150 };
        plot.Click += (_, _) => PlotTrackMapFromCompare();
        var useTop = new Button { Content = "Best vs YOU", Width = 120 };
        useTop.Click += (_, _) => PlotTrackMapYouVsFastest();
        var clear = new Button { Content = "Clear", Width = 80 };
        clear.Click += (_, _) => ClearTrackViews();
        controls.Children.Add(new TextBlock { Text = "Mode", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_trackMapMetric);
        controls.Children.Add(new TextBlock { Text = "Contrast", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_trackMapContrast);
        controls.Children.Add(plot);
        controls.Children.Add(useTop);
        controls.Children.Add(clear);
        controls.Children.Add(new TextBlock { Text = "Select a zone to inspect it below. Blue = gain, white = neutral, red = loss.", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(controls);

        _trackMapControl = new TrackMapControl { MinHeight = 320 };
        Grid.SetRow(_trackMapControl, 2);
        Grid.SetColumn(_trackMapControl, 0);
        grid.Children.Add(_trackMapControl);

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetRow(right, 2);
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = "Map status", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold });
        _trackMapStatus = new TextBlock
        {
            Text = "Select a session and two laps in Lap Compare, then choose Plot from compare.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        right.Children.Add(_trackMapStatus);
        right.Children.Add(new TextBlock { Text = "Top zones", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold });
        _trackMapInsightList = BuildInsightListBox(245);
        _trackMapInsightList.SelectionChanged += (_, _) =>
        {
            if (_updatingTrackZoneSelection) return;
            if (_trackMapInsightList.SelectedItem is TrackMapInsight insight) SelectTrackMapInsight(insight);
        };
        right.Children.Add(_trackMapInsightList);
        right.Children.Add(new TextBlock { Text = "Corner labels", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        _trackMapCornerList = new ListBox
        {
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            Height = 260,
            FontFamily = FontFamily.Parse("Consolas")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_trackMapCornerList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_trackMapCornerList, ScrollBarVisibility.Auto);
        right.Children.Add(_trackMapCornerList);
        grid.Children.Add(right);

        var detail = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(detail, 3);
        Grid.SetColumnSpan(detail, 2);
        _trackDetailStatus = new TextBlock
        {
            Text = "Select a gain or loss zone to open aligned trajectory detail.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumnSpan(_trackDetailStatus, 2);
        detail.Children.Add(_trackDetailStatus);

        _trackDetailZoneList = BuildInsightListBox(230);
        _trackDetailZoneList.SelectionChanged += (_, _) =>
        {
            if (_updatingTrackZoneSelection) return;
            if (_trackDetailZoneList.SelectedItem is TrackMapInsight insight) SelectTrackMapInsight(insight);
        };
        var zonePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
        zonePanel.Children.Add(new TextBlock { Text = "Selected-zone navigation", Foreground = Brushes.White, FontWeight = FontWeight.Bold });
        Grid.SetRow(_trackDetailZoneList, 1);
        zonePanel.Children.Add(_trackDetailZoneList);
        Grid.SetRow(zonePanel, 1);
        detail.Children.Add(zonePanel);

        _trackDetailControl = new TrackDetailControl { MinHeight = 230 };
        Grid.SetRow(_trackDetailControl, 1);
        Grid.SetColumn(_trackDetailControl, 1);
        detail.Children.Add(_trackDetailControl);
        grid.Children.Add(detail);

        return grid;
    }


    private Control BuildRaceReportTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("*"),
            Margin = new Thickness(0)
        };

        var title = new TextBlock
        {
            Text = "Race Report",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        grid.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(controls, 1);
        _raceReportDriver = new ComboBox { Width = 330 };
        _raceReportDriver.SelectionChanged += (_, _) => LoadRaceReportRows();
        _raceReportView = new ComboBox { Width = 125, ItemsSource = RaceReportDataService.Views, SelectedIndex = 0 };
        _raceReportView.SelectionChanged += (_, _) => LoadRaceReportRows();
        _raceReportCleanOnly = new CheckBox { Content = "Clean only", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _raceReportCleanOnly.Click += (_, _) => LoadRaceReportRows();
        _raceReportProblemsOnly = new CheckBox { Content = "Problems only", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _raceReportProblemsOnly.Click += (_, _) => LoadRaceReportRows();
        var load = new Button { Content = "Refresh", Width = 95 };
        load.Click += (_, _) => LoadRaceReportDrivers();
        _raceReportLegendToggle = new Button { Content = "Column help", Width = 115 };
        _raceReportLegendToggle.Click += (_, _) => ToggleRaceReportLegend();
        controls.Children.Add(new TextBlock { Text = "Driver", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_raceReportDriver);
        controls.Children.Add(new TextBlock { Text = "View", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_raceReportView);
        controls.Children.Add(_raceReportCleanOnly);
        controls.Children.Add(_raceReportProblemsOnly);
        controls.Children.Add(load);
        controls.Children.Add(_raceReportLegendToggle);
        grid.Children.Add(controls);

        _raceReportStatus = new TextBlock
        {
            Text = "Select a session. Analyzed driver data loads automatically; use Refresh only after external changes.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_raceReportStatus, 2);
        grid.Children.Add(_raceReportStatus);

        _raceReportSummary = new TextBlock
        {
            Text = "Summary: load a driver to build the lap summary.",
            Foreground = Hex(0xB8D7A3),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_raceReportSummary, 3);
        grid.Children.Add(_raceReportSummary);

        _raceReportLegend = new TextBlock
        {
            Text = RaceReportDataService.CompactLegendForView("Overview"),
            Foreground = Hex(0xA8B3C7),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(_raceReportLegend, 4);
        grid.Children.Add(_raceReportLegend);

        _raceReportTablePanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        var tableScroll = new ScrollViewer
        {
            Content = _raceReportTablePanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var tableBorder = new Border
        {
            Background = Hex(0x161B22),
            Child = tableScroll
        };
        Grid.SetRow(tableBorder, 5);
        grid.Children.Add(tableBorder);

        BuildRaceReportTable(Array.Empty<RaceLapReportRow>(), "Overview");
        _sessionContextLoaders.Add(LoadRaceReportDrivers);
        return grid;
    }

    private void ToggleRaceReportLegend()
    {
        if (_raceReportLegend is null || _raceReportLegendToggle is null) return;
        _raceReportLegend.IsVisible = !_raceReportLegend.IsVisible;
        _raceReportLegendToggle.Content = _raceReportLegend.IsVisible ? "Hide help" : "Column help";
    }

    private void BuildRaceReportTable(IReadOnlyList<RaceLapReportRow> rows, string view)
    {
        if (_raceReportTablePanel is null) return;
        _raceReportTablePanel.Children.Clear();

        var columns = RaceReportDataService.ColumnsForView(view);
        var table = new Grid { Background = Hex(0x161B22), Margin = new Thickness(0) };
        foreach (var column in columns)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns.Count; c++)
            AddRaceReportCell(table, 0, c, columns[c].Header, columns[c], null, true, 0);

        if (rows.Count == 0)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = new Border
            {
                Padding = new Thickness(12),
                Background = Hex(0x161B22),
                BorderBrush = Hex(0x303846),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new TextBlock
                {
                    Text = "No Race Report rows loaded yet.",
                    Foreground = Brushes.LightGray,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, Math.Max(1, columns.Count));
            table.Children.Add(empty);
        }
        else
        {
            for (var r = 0; r < rows.Count; r++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var c = 0; c < columns.Count; c++)
                {
                    var column = columns[c];
                    var text = RaceReportDataService.CellText(rows[r], column.Key);
                    AddRaceReportCell(table, r + 1, c, text, column, rows[r], false, r);
                }
            }
        }

        _raceReportTablePanel.Children.Add(table);
    }

    private void AddRaceReportCell(Grid table, int rowIndex, int columnIndex, string text, RaceReportColumn column, RaceLapReportRow? row, bool isHeader, int dataRowIndex)
    {
        var groupBorder = column.GroupStart && columnIndex > 0 ? 2 : 0;
        var border = new Border
        {
            Padding = isHeader ? new Thickness(7, 6, 7, 6) : new Thickness(7, 7, 7, 7),
            MinHeight = isHeader ? 34 : 36,
            Background = isHeader ? Hex(0x202632) : RaceReportRowBackground(row, dataRowIndex),
            BorderBrush = isHeader ? Hex(0x4B5A6D) : Hex(0x2A313B),
            BorderThickness = new Thickness(groupBorder, 0, 1, 1)
        };

        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = FontFamily.Parse(isHeader ? "Segoe UI" : "Consolas"),
            FontSize = isHeader ? 12 : 13,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isHeader ? Brushes.White : RaceReportCellForeground(row, column),
            TextAlignment = column.AlignRight ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = column.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = column.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        if (!string.IsNullOrWhiteSpace(column.Help)) ToolTip.SetTip(border, column.Help);
        border.Child = textBlock;
        Grid.SetRow(border, rowIndex);
        Grid.SetColumn(border, columnIndex);
        table.Children.Add(border);
    }

    private IBrush RaceReportRowBackground(RaceLapReportRow? row, int dataRowIndex)
    {
        if (row is null) return Hex(0x161B22);
        if (!row.CleanLap || row.LapInvalid || row.RewindCount > 0) return Hex(0x221D2A);
        if (row.DamageDeltaMax >= 10) return Hex(0x2A1E1E);
        if (row.PitThisLap) return Hex(0x1E2430);
        if (row.PersonalBest) return Hex(0x18261E);
        return dataRowIndex % 2 == 0 ? Hex(0x161B22) : Hex(0x14191F);
    }

    private IBrush RaceReportCellForeground(RaceLapReportRow? row, RaceReportColumn column)
    {
        if (row is null) return Brushes.White;
        if (column.Key == "notes")
        {
            if (row.Notes.Contains("New damage", StringComparison.OrdinalIgnoreCase)) return Hex(0xFFB8B8);
            if (row.Notes.Contains("Low ERS", StringComparison.OrdinalIgnoreCase) || row.Notes.Contains("High tyre wear", StringComparison.OrdinalIgnoreCase)) return Hex(0xFFD9A0);
            if (row.Notes.Contains("Dirty", StringComparison.OrdinalIgnoreCase) || row.Notes.Contains("Invalid", StringComparison.OrdinalIgnoreCase)) return Hex(0xC9B8FF);
        }
        if (column.Key == "clean" && !row.CleanLap) return Hex(0xFFD9A0);
        if (column.Key == "damageDelta" && row.DamageDeltaMax > 0) return row.DamageDeltaMax >= 10 ? Hex(0xFFB8B8) : Hex(0xFFD9A0);
        if ((column.Key.StartsWith("ers", StringComparison.OrdinalIgnoreCase) || column.Key == "ersEnd") && row.ErsEnd > 0 && row.ErsEnd <= 400_000) return Hex(0xFFD9A0);
        if ((column.Key.StartsWith("wearDelta", StringComparison.OrdinalIgnoreCase) || column.Key == "wearDeltaAvg") && row.TyreWearAvgDelta >= 3.0) return Hex(0xFFD9A0);
        if (row.PersonalBest && column.Key == "time") return Hex(0xB8F0C8);
        return Brushes.White;
    }


    private Control BuildDriverCompareTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,260,*"),
            ColumnDefinitions = new ColumnDefinitions("*"),
            Margin = new Thickness(0)
        };
        grid.Children.Add(new TextBlock { Text = "Driver Compare", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(controls, 1);
        var driverA = new ComboBox { Width = 260 };
        var driverB = new ComboBox { Width = 260 };
        var driverC = new ComboBox { Width = 260 };
        var mode = new ComboBox { Width = 120, ItemsSource = RaceAnalysisDataService.CompareModes, SelectedIndex = 0 };
        var group = new ComboBox { Width = 115, ItemsSource = RaceAnalysisDataService.MetricGroups, SelectedIndex = 0 };
        var load = new Button { Content = "Refresh", Width = 95 };
        var compare = new Button { Content = "Compare", Width = 100 };
        controls.Children.Add(new TextBlock { Text = "A", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driverA);
        controls.Children.Add(new TextBlock { Text = "B", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driverB);
        controls.Children.Add(new TextBlock { Text = "C", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driverC);
        controls.Children.Add(new TextBlock { Text = "By", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(mode);
        controls.Children.Add(new TextBlock { Text = "Group", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(group);
        controls.Children.Add(load);
        controls.Children.Add(compare);
        grid.Children.Add(controls);

        var status = new TextBlock { Text = "Select an analyzed session. Two drivers are loaded and compared automatically.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        var legend = new TextBlock { Text = "", Foreground = Hex(0xA8B3C7), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(legend, 3);
        grid.Children.Add(legend);

        var chart = new LapSeriesChart { Height = 250 };
        Grid.SetRow(chart, 4);
        grid.Children.Add(new Border { Background = Hex(0x161B22), Child = chart, Margin = new Thickness(0, 0, 0, 10) }.WithGridRow(4));

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 5);
        grid.Children.Add(new Border { Background = Hex(0x161B22), Child = scroll }.WithGridRow(5));

        void LoadDrivers()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var drivers = RaceReportDataService.LoadDrivers(folder);
                driverA.ItemsSource = drivers;
                driverB.ItemsSource = drivers;
                driverC.ItemsSource = drivers;
                driverA.SelectedItem = drivers.FirstOrDefault(x => x.IsPlayer) ?? drivers.FirstOrDefault();
                driverB.SelectedItem = drivers.FirstOrDefault(x => !x.IsPlayer) ?? drivers.Skip(1).FirstOrDefault();
                driverC.SelectedItem = null;
                status.Text = $"Loaded {drivers.Count:N0} drivers. Select two or three drivers, then compare.";
            }
            catch (Exception ex)
            {
                status.Text = "Driver Compare load failed: " + ex.Message;
                AddLog(status.Text);
            }
        }

        void RunCompare()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var selected = new[] { driverA.SelectedItem as RaceReportDriverOption, driverB.SelectedItem as RaceReportDriverOption, driverC.SelectedItem as RaceReportDriverOption }
                    .Where(x => x is not null).Cast<RaceReportDriverOption>().GroupBy(x => x.CarIndex).Select(g => g.First()).ToList();
                var selectedMode = mode.SelectedItem?.ToString() ?? RaceAnalysisDataService.CompareModes[0];
                var selectedGroup = group.SelectedItem?.ToString() ?? RaceAnalysisDataService.MetricGroups[0];
                var result = RaceAnalysisDataService.BuildDriverCompare(folder, selected, selectedMode, selectedGroup);
                BuildAnalysisTable(panel, result);
                status.Text = result.Status;
                legend.Text = result.Legend;
                var chartData = RaceAnalysisDataService.BuildDriverCompareChart(folder, selected, selectedGroup);
                var title = RaceAnalysisDataService.ChartTitle(selectedGroup);
                chart.SetData(chartData, title.Title, title.Unit);
            }
            catch (Exception ex)
            {
                status.Text = "Driver Compare failed: " + ex.Message;
                AddLog(status.Text);
            }
        }

        load.Click += (_, _) => LoadDrivers();
        compare.Click += (_, _) => RunCompare();
        group.SelectionChanged += (_, _) => { if (driverA.SelectedItem is not null && driverB.SelectedItem is not null) RunCompare(); };
        mode.SelectionChanged += (_, _) => { if (driverA.SelectedItem is not null && driverB.SelectedItem is not null) RunCompare(); };
        BuildAnalysisTable(panel, new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "No compare yet.", ""));
        _sessionContextLoaders.Add(() =>
        {
            LoadDrivers();
            if (driverA.SelectedItem is not null && driverB.SelectedItem is not null) RunCompare();
        });
        return grid;
    }

    private Control BuildStintReportTab()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), ColumnDefinitions = new ColumnDefinitions("*"), Margin = new Thickness(0) };
        grid.Children.Add(new TextBlock { Text = "Stint Report", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(controls, 1);
        var driver = new ComboBox { Width = 330 };
        var load = new Button { Content = "Refresh", Width = 95 };
        var build = new Button { Content = "Build report", Width = 120 };
        controls.Children.Add(new TextBlock { Text = "Driver", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driver);
        controls.Children.Add(load);
        controls.Children.Add(build);
        grid.Children.Add(controls);
        var status = new TextBlock { Text = "Groups laps into stints by compound, pit stop and tyre-age reset.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        var legend = new TextBlock { Text = "", Foreground = Hex(0xA8B3C7), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(legend, 3);
        grid.Children.Add(legend);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(new Border { Background = Hex(0x161B22), Child = scroll }.WithGridRow(4));
        void LoadDrivers()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var drivers = RaceReportDataService.LoadDrivers(folder);
                driver.ItemsSource = drivers;
                driver.SelectedItem = drivers.FirstOrDefault(x => x.IsPlayer) ?? drivers.FirstOrDefault();
                status.Text = $"Loaded {drivers.Count:N0} drivers.";
            }
            catch (Exception ex) { status.Text = "Stint driver load failed: " + ex.Message; AddLog(status.Text); }
        }
        void Build()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var result = RaceAnalysisDataService.BuildStintReport(folder, driver.SelectedItem as RaceReportDriverOption);
                BuildAnalysisTable(panel, result);
                status.Text = result.Status;
                legend.Text = result.Legend;
            }
            catch (Exception ex) { status.Text = "Stint report failed: " + ex.Message; AddLog(status.Text); }
        }
        load.Click += (_, _) => LoadDrivers();
        build.Click += (_, _) => Build();
        BuildAnalysisTable(panel, new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "No stint report yet.", ""));
        _sessionContextLoaders.Add(() => { LoadDrivers(); if (driver.SelectedItem is not null) Build(); });
        return grid;
    }

    private Control BuildPitReportTab()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), ColumnDefinitions = new ColumnDefinitions("*"), Margin = new Thickness(0) };
        grid.Children.Add(new TextBlock { Text = "Pit Report", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(controls, 1);
        var driver = new ComboBox { Width = 330 };
        var load = new Button { Content = "Refresh", Width = 95 };
        var build = new Button { Content = "Build report", Width = 120 };
        controls.Children.Add(new TextBlock { Text = "Driver", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driver);
        controls.Children.Add(load);
        controls.Children.Add(build);
        grid.Children.Add(controls);
        var status = new TextBlock { Text = "Pit stop and compound-change analysis. Detection combines pit status, stop count, compound changes and tyre-age resets.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        var legend = new TextBlock { Text = "", Foreground = Hex(0xA8B3C7), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(legend, 3);
        grid.Children.Add(legend);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(new Border { Background = Hex(0x161B22), Child = scroll }.WithGridRow(4));
        void LoadDrivers()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var drivers = RaceReportDataService.LoadDrivers(folder);
                driver.ItemsSource = drivers;
                driver.SelectedItem = drivers.FirstOrDefault(x => x.IsPlayer) ?? drivers.FirstOrDefault();
                status.Text = $"Loaded {drivers.Count:N0} drivers.";
            }
            catch (Exception ex) { status.Text = "Pit driver load failed: " + ex.Message; AddLog(status.Text); }
        }
        void Build()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) { status.Text = "Select a session first."; return; }
                var result = RaceAnalysisDataService.BuildPitReport(folder, driver.SelectedItem as RaceReportDriverOption);
                BuildAnalysisTable(panel, result);
                status.Text = result.Status;
                legend.Text = result.Legend;
            }
            catch (Exception ex) { status.Text = "Pit report failed: " + ex.Message; AddLog(status.Text); }
        }
        load.Click += (_, _) => LoadDrivers();
        build.Click += (_, _) => Build();
        BuildAnalysisTable(panel, new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "No pit report yet.", ""));
        _sessionContextLoaders.Add(() => { LoadDrivers(); if (driver.SelectedItem is not null) Build(); });
        return grid;
    }

    private Control BuildCarSetupTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            RowSpacing = 10,
            ColumnSpacing = 14
        };
        var title = new TextBlock
        {
            Text = "Car Setup",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 2)
        };
        Grid.SetColumnSpan(title, 2);
        grid.Children.Add(title);

        var driver = new ComboBox { Width = 360 };
        var refresh = new Button { Content = "Refresh", Width = 95 };
        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8 };
        controls.Children.Add(new TextBlock { Text = "Driver", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(driver);
        controls.Children.Add(refresh);
        Grid.SetRow(controls, 1);
        Grid.SetColumnSpan(controls, 2);
        grid.Children.Add(controls);

        var status = new TextBlock
        {
            Text = "Setup changes are extracted from UDP packet 5 during analysis and stored in session.sqlite.",
            Foreground = Hex(0xA8B3C7),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(status, 2);
        Grid.SetColumnSpan(status, 2);
        grid.Children.Add(status);

        var changes = new ListBox
        {
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            FontFamily = FontFamily.Parse("Consolas"),
            ItemTemplate = new FuncDataTemplate<CarSetupViewRow>((value, _) => new TextBlock
            {
                Text = value?.Label ?? "",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 4)
            }, true)
        };
        Grid.SetRow(changes, 3);
        grid.Children.Add(changes);

        var detail = new TextBlock
        {
            Text = "Select a setup snapshot.",
            Foreground = Brushes.White,
            FontFamily = FontFamily.Parse("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var detailScroll = new ScrollViewer
        {
            Content = detail,
            Background = Hex(0x161B22),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(detailScroll, 3);
        Grid.SetColumn(detailScroll, 1);
        grid.Children.Add(detailScroll);

        void LoadSetups()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null || driver.SelectedItem is not RaceReportDriverOption selected)
                {
                    changes.ItemsSource = Array.Empty<CarSetupViewRow>();
                    detail.Text = "Select a session and driver.";
                    return;
                }
                var rows = CarSetupDataService.LoadChanges(folder, selected.CarIndex);
                changes.ItemsSource = rows;
                changes.SelectedItem = rows.LastOrDefault();
                detail.Text = rows.LastOrDefault()?.Detail ?? "No Car Setup rows were found. Re-analyze the session with v0.7.1; the original raw packet 5 data is retained.";
                status.Text = rows.Count == 0
                    ? "No setup snapshots for this driver. Re-analyze the session if it was recorded by an older version."
                    : $"{rows.Count:N0} initial/change snapshot(s). Unchanged 2 Hz packets are intentionally deduplicated; all fields are stored in session.sqlite.";
            }
            catch (Exception ex)
            {
                status.Text = "Car Setup load failed: " + ex.Message;
                AddLog(status.Text);
            }
        }

        void LoadDrivers()
        {
            try
            {
                var folder = GetSelectedSessionFolder();
                if (folder is null) return;
                var drivers = RaceReportDataService.LoadDrivers(folder);
                driver.ItemsSource = drivers;
                driver.SelectedItem = drivers.FirstOrDefault(x => x.IsPlayer) ?? drivers.FirstOrDefault();
                LoadSetups();
            }
            catch (Exception ex)
            {
                status.Text = "Car Setup driver load failed: " + ex.Message;
            }
        }

        driver.SelectionChanged += (_, _) => LoadSetups();
        changes.SelectionChanged += (_, _) => detail.Text = (changes.SelectedItem as CarSetupViewRow)?.Detail ?? "Select a setup snapshot.";
        refresh.Click += (_, _) => LoadDrivers();
        _sessionContextLoaders.Add(LoadDrivers);
        return grid;
    }

    private void BuildAnalysisTable(StackPanel panel, AnalysisTableResult result)
    {
        panel.Children.Clear();
        var columns = result.Columns;
        var table = new Grid { Background = Hex(0x161B22) };
        if (columns.Count == 0)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(800) });
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = new Border
            {
                Padding = new Thickness(12),
                Background = Hex(0x161B22),
                BorderBrush = Hex(0x303846),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new TextBlock { Text = result.Status, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap }
            };
            table.Children.Add(empty);
            panel.Children.Add(table);
            return;
        }
        foreach (var c in columns) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Width) });
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns.Count; c++) AddAnalysisCell(table, 0, c, columns[c].Header, columns[c], null, true, 0);
        for (var r = 0; r < result.Rows.Count; r++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns.Count; c++)
            {
                var col = columns[c];
                var text = result.Rows[r].Values.TryGetValue(col.Key, out var value) ? value : "-";
                AddAnalysisCell(table, r + 1, c, text, col, result.Rows[r], false, r);
            }
        }
        panel.Children.Add(table);
    }

    private void AddAnalysisCell(Grid table, int rowIndex, int columnIndex, string text, AnalysisTableColumn column, AnalysisTableRow? row, bool header, int dataRowIndex)
    {
        var border = new Border
        {
            Padding = header ? new Thickness(7, 6, 7, 6) : new Thickness(7, 7, 7, 7),
            MinHeight = header ? 34 : 36,
            Background = header ? Hex(0x202632) : AnalysisRowBackground(row, dataRowIndex),
            BorderBrush = header ? Hex(0x4B5A6D) : Hex(0x2A313B),
            BorderThickness = new Thickness(column.GroupStart && columnIndex > 0 ? 2 : 0, 0, 1, 1)
        };
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = FontFamily.Parse(header ? "Segoe UI" : "Consolas"),
            FontSize = header ? 12 : 13,
            FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = header ? Brushes.White : AnalysisCellForeground(row, text),
            TextAlignment = column.AlignRight ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = column.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = column.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        if (!string.IsNullOrWhiteSpace(column.Help)) ToolTip.SetTip(border, column.Help);
        border.Child = textBlock;
        Grid.SetRow(border, rowIndex);
        Grid.SetColumn(border, columnIndex);
        table.Children.Add(border);
    }

    private IBrush AnalysisRowBackground(AnalysisTableRow? row, int dataRowIndex)
    {
        if (row is null) return Hex(0x161B22);
        if (row.Severity == "bad") return Hex(0x2A1E1E);
        if (row.Severity == "warn") return Hex(0x221D2A);
        return dataRowIndex % 2 == 0 ? Hex(0x161B22) : Hex(0x14191F);
    }

    private IBrush AnalysisCellForeground(AnalysisTableRow? row, string text)
    {
        if (text.Contains("Low ERS", StringComparison.OrdinalIgnoreCase) || text.Contains("High tyre", StringComparison.OrdinalIgnoreCase) || text.Contains("+", StringComparison.OrdinalIgnoreCase)) return Hex(0xFFD9A0);
        if (text.Contains("New damage", StringComparison.OrdinalIgnoreCase) || text.Contains("Invalid", StringComparison.OrdinalIgnoreCase)) return Hex(0xFFB8B8);
        if (text.Contains("Personal best", StringComparison.OrdinalIgnoreCase)) return Hex(0xB8F0C8);
        return Brushes.White;
    }

    private ListBox BuildInsightListBox(double height)
    {
        var list = new ListBox
        {
            Background = Hex(0x161B22),
            Foreground = Brushes.White,
            Height = height,
            FontFamily = FontFamily.Parse("Consolas"),
            ItemTemplate = new FuncDataTemplate<TrackMapInsight>((value, _) => new TextBlock
            {
                Text = value?.Label ?? "",
                FontFamily = FontFamily.Parse("Consolas"),
                Foreground = value is null ? Brushes.White : (value.Kind == "LOSS" ? Hex(0xFFB8B8) : Hex(0xB8DCFF)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 3, 6, 3)
            }, true)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        return list;
    }

    private Control BuildCompareTab()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("360,*,260"),
            Margin = new Thickness(0)
        };

        var title = new TextBlock
        {
            Text = "Lap Compare",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetColumnSpan(title, 3);
        grid.Children.Add(title);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(controls, 1);
        Grid.SetColumnSpan(controls, 3);
        _compareMetric = new ComboBox { Width = 115, ItemsSource = CompareDataService.Metrics, SelectedIndex = 0 };
        _compareMetric.SelectionChanged += (_, _) =>
        {
            UpdateCompareMetricHelp();
            if (_lastComparedLaps.Count > 0) PlotLaps(_lastComparedLaps);
        };
        _compareCleanOnly = new CheckBox { Content = "Clean laps only", IsChecked = true, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var load = new Button { Content = "Load laps", Width = 105 };
        load.Click += (_, _) => LoadCompareLaps();
        var plot = new Button { Content = "Plot", Width = 80 };
        plot.Click += (_, _) => PlotCurrentCompareSlots();
        var top6 = new Button { Content = "Top 6", Width = 80 };
        top6.Click += (_, _) => PlotTopBestCompareLaps();
        var youTop = new Button { Content = "YOU vs 5", Width = 95 };
        youTop.Click += (_, _) => PlotYouVsTopCompareLaps();
        var allBest = new Button { Content = "All best", Width = 85 };
        allBest.Click += (_, _) => ApplyBestLapToAllSlots();
        var sameLap = new Button { Content = "Same lap", Width = 95 };
        sameLap.Click += (_, _) => ApplyReferenceLapNumberToAllSlots();
        _zoomFromText = new TextBox { Width = 65, PlaceholderText = "from m" };
        _zoomToText = new TextBox { Width = 65, PlaceholderText = "to m" };
        var applyZoom = new Button { Content = "Apply", Width = 75 };
        applyZoom.Click += (_, _) => ApplyCompareZoom();
        var resetZoom = new Button { Content = "Reset", Width = 70 };
        resetZoom.Click += (_, _) => ResetCompareZoom();

        controls.Children.Add(new TextBlock { Text = "Metric", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_compareMetric);
        controls.Children.Add(_compareCleanOnly);
        controls.Children.Add(load);
        controls.Children.Add(plot);
        controls.Children.Add(top6);
        controls.Children.Add(youTop);
        controls.Children.Add(allBest);
        controls.Children.Add(sameLap);
        controls.Children.Add(new TextBlock { Text = "Zoom", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16,0,0,0) });
        controls.Children.Add(_zoomFromText);
        controls.Children.Add(_zoomToText);
        controls.Children.Add(applyZoom);
        controls.Children.Add(resetZoom);
        grid.Children.Add(controls);

        var left = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(new TextBlock { Text = "Comparison slots", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold });
        for (var i = 0; i < 6; i++)
        {
            var slot = BuildCompareSlot(i);
            slot.IsVisible = i < _visibleCompareSlots;
            left.Children.Add(slot);
        }
        var addComparison = new Button { Content = "+ Add comparison", HorizontalAlignment = HorizontalAlignment.Left };
        addComparison.Click += (_, _) => ShowNextCompareSlot();
        left.Children.Add(addComparison);
        _compareStatus = new TextBlock
        {
            Text = "Slot 1 is the reference. Select a second lap, then add more series only when needed.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        left.Children.Add(_compareStatus);
        var leftScroll = new ScrollViewer
        {
            Content = left,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(leftScroll, 2);
        Grid.SetColumn(leftScroll, 0);
        grid.Children.Add(leftScroll);

        var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(middle, 2);
        Grid.SetColumn(middle, 1);
        _referenceText = new TextBlock
        {
            Text = "Reference: not selected",
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_referenceText, 0);
        middle.Children.Add(_referenceText);
        _compareChart = new SimpleLineChart { MinHeight = 390 };
        Grid.SetRow(_compareChart, 1);
        middle.Children.Add(_compareChart);
        _compareMetricHelp = new TextBlock
        {
            Text = CompareMetricHelp("speed"),
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(_compareMetricHelp, 2);
        middle.Children.Add(_compareMetricHelp);
        grid.Children.Add(middle);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8, Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetRow(right, 2);
        Grid.SetColumn(right, 2);
        right.Children.Add(new TextBlock { Text = "Legend", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold });
        var legend = new ListBox
        {
            ItemsSource = _compareLegendItems,
            Background = Hex(0x161B22),
            Foreground = Brushes.White
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(legend, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(legend, ScrollBarVisibility.Auto);
        Grid.SetRow(legend, 1);
        right.Children.Add(legend);
        grid.Children.Add(right);

        _sessionContextLoaders.Add(LoadCompareLaps);
        return grid;
    }

    private Border BuildCompareSlot(int slotIndex)
    {
        var label = new TextBlock
        {
            Text = slotIndex == 0 ? "Slot 1: REFERENCE" : $"Slot {slotIndex + 1}: Compare {slotIndex}",
            Foreground = slotIndex == 0 ? Brushes.White : Brushes.LightGray,
            FontWeight = slotIndex == 0 ? FontWeight.Bold : FontWeight.Normal,
            Margin = new Thickness(0, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var clearSlot = new Button
        {
            Content = slotIndex == 0 ? "Reset" : "None",
            Width = 64,
            Height = 26,
            IsEnabled = true
        };
        clearSlot.Click += (_, _) => ClearCompareSlot(slotIndex);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(clearSlot, Dock.Right);
        header.Children.Add(clearSlot);
        header.Children.Add(label);

        var driver = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var lap = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _compareDriverBoxes[slotIndex] = driver;
        _compareLapBoxes[slotIndex] = lap;

        driver.SelectionChanged += (_, _) =>
        {
            if (_updatingCompareSlots) return;
            UpdateLapComboForSlot(slotIndex);
        };
        lap.SelectionChanged += (_, _) =>
        {
            if (_updatingCompareSlots) return;
            if (slotIndex == 0) UpdateReferenceText();
        };

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(header);
        stack.Children.Add(driver);
        stack.Children.Add(lap);

        var border = new Border
        {
            Background = Hex(slotIndex == 0 ? 0x243142u : 0x161B22u),
            BorderBrush = Hex(slotIndex == 0 ? 0x586B82u : 0x242A33u),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = stack
        };
        _compareSlotContainers.Add(border);
        return border;
    }

    private void ShowNextCompareSlot()
    {
        if (_visibleCompareSlots >= _compareSlotContainers.Count)
        {
            _compareStatus.Text = "All six comparison slots are already visible.";
            return;
        }
        _visibleCompareSlots++;
        _compareSlotContainers[_visibleCompareSlots - 1].IsVisible = true;
        _compareStatus.Text = $"Comparison slot {_visibleCompareSlots} added.";
    }

    private void EnsureVisibleCompareSlots(int count)
    {
        _visibleCompareSlots = Math.Clamp(Math.Max(2, count), 2, _compareSlotContainers.Count);
        for (var i = 0; i < _compareSlotContainers.Count; i++) _compareSlotContainers[i].IsVisible = i < _visibleCompareSlots;
    }

    private void UpdateCompareMetricHelp()
    {
        if (_compareMetricHelp is null) return;
        _compareMetricHelp.Text = CompareMetricHelp(_compareMetric?.SelectedItem?.ToString() ?? "speed");
    }

    private static string CompareMetricHelp(string metric) => metric switch
    {
        "speed" => "Speed in km/h. Move the pointer over the plot for distance and per-series values.",
        "throttle_%" => "Throttle pedal input in percent. 100% means full throttle.",
        "brake_%" => "Brake pedal input in percent. Higher values mean stronger braking input.",
        "steer" => "Normalized steering input. Negative and positive values indicate opposite directions.",
        "gear" => "Selected gear by track distance.",
        "delta_ms" => "Accumulated time relative to the reference. +300 ms means 0.300 s slower at that point.",
        _ => "Metric values plotted against normalized track distance."
    };

    private Control BuildSettingsTab()
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = "Recommended game configuration: UDP Telemetry On, 2026 format, 127.0.0.1, the port below, and 60 Hz send rate. Measured rate is shown separately from the recommendation.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase)
                ? "Прототип ERS: выключите ERS Assist в игре, назначьте уменьшение стандартного режима ERS на F7, увеличение на F8. Начните с теста без ввода. Управление заблокировано в онлайне, отправляет клавиши только при активном окне F1 25, аварийная остановка - F12."
                : "ERS prototype: turn the in-game ERS Assist off, bind standard ERS mode decrease to F7 and increase to F8. Start with Dry-run. Live control is blocked online, sends keys only while F1 25 is foreground, and F12 is the emergency stop.",
            Foreground = Hex(0xF4BF75),
            TextWrapping = TextWrapping.Wrap
        });

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,420"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 10,
            ColumnSpacing = 12
        };
        var port = new TextBox { Text = _settings.Port.ToString(CultureInfo.InvariantCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var root = new TextBox { Text = _settings.RootFolder, HorizontalAlignment = HorizontalAlignment.Stretch };
        var autoZip = new CheckBox { Content = "Create RAR with session.sqlite only after Stop", IsChecked = _settings.AutoZip, Foreground = Brushes.White };
        var retention = new TextBox { Text = _settings.RetentionDays.ToString(CultureInfo.InvariantCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var language = new ComboBox { ItemsSource = new[] { "English (en)", "Русский (ru)" }, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        language.SelectedIndex = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var scale = new ComboBox { ItemsSource = new[] { "80%", "90%", "100%", "110%", "125%", "150%", "175%" }, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        scale.SelectedItem = $"{_settings.UiScalePercent}%";
        if (scale.SelectedItem is null) scale.SelectedItem = "100%";
        var winRar = new TextBox
        {
            Text = _settings.WinRarPath,
            PlaceholderText = @"Auto-detect or C:\Program Files\WinRAR\WinRAR.exe",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var autoOverlay = new CheckBox
        {
            Content = "Open Race Engineer overlay when recording starts",
            IsChecked = _settings.OpenRaceEngineerOverlayOnStart,
            Foreground = Brushes.White
        };

        AddSettingRow(form, 0, "UDP port", port);
        AddSettingRow(form, 1, "Storage root", root);
        AddSettingRow(form, 2, "Automatic packaging", autoZip);
        AddSettingRow(form, 3, "Retention, days", retention);
        AddSettingRow(form, 4, "UI language", language);
        AddSettingRow(form, 5, "UI scale", scale);
        AddSettingRow(form, 6, "WinRAR path", winRar);
        AddSettingRow(form, 7, "Race overlay", autoOverlay);
        panel.Children.Add(new Border
        {
            Background = Hex(0x1D2630),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = form
        });

        _measuredRateText = new TextBlock
        {
            Text = "Measured telemetry rate: select a recorded session.",
            Foreground = Hex(0xB8CBE4),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_measuredRateText);

        var save = new Button { Content = "Save settings", Width = 140 };
        var previewRetention = new Button { Content = "Preview retention", Width = 150 };
        var confirmDelete = new CheckBox { Content = "Confirm deletion of previewed session folders", Foreground = Brushes.White };
        var deleteRetention = new Button { Content = "Delete previewed", Width = 150, IsEnabled = false };
        confirmDelete.Click += (_, _) => deleteRetention.IsEnabled = confirmDelete.IsChecked == true && _retentionPreview.Count > 0;

        save.Click += (_, _) =>
        {
            if (!int.TryParse(port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portValue) || portValue is < 1 or > 65_535)
            {
                _settingsStatus.Text = "Port must be between 1 and 65535.";
                return;
            }
            if (!int.TryParse(retention.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) || retentionDays is < 0 or > 3_650)
            {
                _settingsStatus.Text = "Retention must be between 0 and 3650 days. Use 0 to keep all sessions.";
                return;
            }
            var scaleText = scale.SelectedItem?.ToString()?.TrimEnd('%') ?? "100";
            if (!int.TryParse(scaleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scaleValue)) scaleValue = 100;
            _settings.Port = portValue;
            _settings.RootFolder = string.IsNullOrWhiteSpace(root.Text) ? AppSettingsService.DefaultRootFolder() : root.Text.Trim();
            _settings.AutoZip = autoZip.IsChecked == true;
            _settings.RetentionDays = retentionDays;
            _settings.Language = language.SelectedIndex == 1 ? "ru" : "en";
            _settings.UiScalePercent = scaleValue;
            _settings.WinRarPath = winRar.Text?.Trim() ?? "";
            _settings.OpenRaceEngineerOverlayOnStart = autoOverlay.IsChecked == true;
            try
            {
                AppSettingsService.Save(_settings);
                _portText.Text = _settings.Port.ToString(CultureInfo.InvariantCulture);
                _rootText.Text = _settings.RootFolder;
                _autoZipCheck.IsChecked = _settings.AutoZip;
                FontSize = 14 * _settings.UiScalePercent / 100.0;
                RefreshSessions();
                _settingsStatus.Text = "Settings saved. Language changes apply after restart; scale is applied immediately.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _settingsStatus.Text = "Settings save failed: " + ex.Message;
            }
        };

        previewRetention.Click += (_, _) =>
        {
            if (!int.TryParse(retention.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) || retentionDays <= 0)
            {
                _retentionPreview = Array.Empty<SessionRetentionCandidate>();
                deleteRetention.IsEnabled = false;
                _settingsStatus.Text = "Set retention above 0 days to preview cleanup. The selected session is always excluded.";
                return;
            }
            try
            {
                _retentionPreview = SessionRetentionService.Preview(root.Text ?? _settings.RootFolder, retentionDays, GetSelectedSessionFolder());
                var bytes = _retentionPreview.Sum(x => x.SizeBytes);
                _settingsStatus.Text = _retentionPreview.Count == 0
                    ? "No session folders match the retention rule."
                    : $"Preview: {_retentionPreview.Count:N0} session folder(s), {FormatBytes(bytes)}. Check confirmation before deleting.";
                deleteRetention.IsEnabled = confirmDelete.IsChecked == true && _retentionPreview.Count > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _settingsStatus.Text = "Retention preview failed: " + ex.Message;
            }
        };

        deleteRetention.Click += (_, _) =>
        {
            if (confirmDelete.IsChecked != true || _retentionPreview.Count == 0) return;
            try
            {
                var removed = SessionRetentionService.Delete(root.Text ?? _settings.RootFolder, _retentionPreview);
                _retentionPreview = Array.Empty<SessionRetentionCandidate>();
                confirmDelete.IsChecked = false;
                deleteRetention.IsEnabled = false;
                RefreshSessions();
                _settingsStatus.Text = $"Removed {removed:N0} previewed session folder(s). This operation cannot be undone.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _settingsStatus.Text = "Retention cleanup failed: " + ex.Message;
            }
        };

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8 };
        actions.Children.Add(save);
        actions.Children.Add(previewRetention);
        actions.Children.Add(confirmDelete);
        actions.Children.Add(deleteRetention);
        panel.Children.Add(actions);

        _settingsStatus = new TextBlock
        {
            Text = "Retention 0 keeps all sessions. Cleanup only removes folders shown by Preview retention and requires explicit confirmation.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_settingsStatus);
        panel.Children.Add(new TextBlock
        {
            Text = $"v{AppInfo.Version} | schema {AppInfo.DatabaseSchemaVersion} | automatic data stays in session.sqlite; optional export is one small Excel workbook.",
            Foreground = Hex(0xA8B3C7),
            TextWrapping = TextWrapping.Wrap
        });

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static void AddSettingRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1_073_741_824) return $"{value / 1_073_741_824d:0.0} GB";
        if (value >= 1_048_576) return $"{value / 1_048_576d:0.0} MB";
        if (value >= 1_024) return $"{value / 1_024d:0.0} KB";
        return $"{value:N0} B";
    }

    private static Control BuildPlaceholder(string title, string text)
    {
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                new Border
                {
                    Background = Hex(0x1D2630),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16),
                    Child = new TextBlock { Text = text, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
    }

    private void StartRecording()
    {
        if (_busy)
        {
            AddLog("Busy: wait until analysis/stop finishes.");
            return;
        }
        if (!int.TryParse(_portText.Text, out var port))
        {
            AddLog("Invalid UDP port.");
            return;
        }
        try
        {
            var root = string.IsNullOrWhiteSpace(_rootText.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
            Directory.CreateDirectory(root);
            _settings.Port = port;
            _settings.RootFolder = root;
            _settings.AutoZip = _autoZipCheck.IsChecked == true;
            var ersMode = (_ersModeCombo.SelectedItem as ErsModeChoice)?.Mode ?? ErsAutopilotOperatingMode.DryRun;
            _settings.ErsAutopilotMode = ErsAutopilotOptions.ToSettingValue(ersMode);
            try { AppSettingsService.Save(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("Settings persistence warning: " + ex.Message); }
            _recorder.Start(port, root, ErsAutopilotOptions.FromSettings(_settings), _settings.WinRarPath);
            _startButton.IsEnabled = false;
            _stopButton.IsEnabled = true;
            _ersModeCombo.IsEnabled = false;
            if (_settings.OpenRaceEngineerOverlayOnStart) ShowRaceEngineerOverlay();
        }
        catch (Exception ex)
        {
            AddLog("Start failed: " + ex.Message);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_busy)
        {
            AddLog("Busy: another operation is already running.");
            return;
        }

        _busy = true;
        try
        {
            _stopButton.IsEnabled = false;
            _startButton.IsEnabled = false;
            await _recorder.StopAsync(_autoZipCheck.IsChecked == true);
            RefreshSessions();
        }
        catch (Exception ex)
        {
            AddLog("Stop failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            _startButton.IsEnabled = true;
            _stopButton.IsEnabled = _recorder.IsRecording;
            _ersModeCombo.IsEnabled = !_recorder.IsRecording;
        }
    }

    private void UpdateLiveUi()
    {
        var quality = _recorder.Quality;
        var raceEngineer = _recorder.RaceEngineer;
        var raceDisplay = RaceEngineerText.Format(raceEngineer, string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase));
        _statusText.Text = _recorder.Status;
        _packetsText.Text = _recorder.PacketsSeen.ToString("N0");
        _samplesText.Text = _recorder.CarSamplesSeen.ToString("N0");
        _sessionText.Text = _recorder.CurrentSession?.TrackName ?? "None";
        _qualityText.Text = $"{quality.Rating}\nDrops {quality.QueueDrops:N0} | Queue {quality.QueueDepth:N0}/{quality.QueueHighWatermark:N0}";
        _ersStatusText.Text = _recorder.ErsStatus.Display;
        _raceLapsText.Text = raceDisplay.Laps;
        _raceTyresText.Text = raceDisplay.Tyres;
        _racePitText.Text = raceDisplay.Pit;
        _raceErsText.Text = raceDisplay.Ers;
        _raceAdvisorConfidenceText.Text = raceDisplay.Confidence;
        ToolTip.SetTip(_raceTyresText, raceEngineer.Tyres.Reason);
        ToolTip.SetTip(_racePitText, raceEngineer.Pit.Reason);
        ToolTip.SetTip(_raceErsText, raceEngineer.Ers.Reason);
        _raceOverlayWindow?.UpdateSnapshot(raceEngineer);

        _liveRows.Clear();
        foreach (var row in _recorder.LiveCars)
            _liveRows.Add(row.Display);
    }

    private void ToggleRaceEngineerOverlay()
    {
        if (_raceOverlayWindow?.IsVisible == true)
        {
            _raceOverlayWindow.Hide();
            _raceOverlayButton.Content = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "Открыть оверлей" : "Open overlay";
            return;
        }
        ShowRaceEngineerOverlay();
    }

    private void ShowRaceEngineerOverlay()
    {
        if (_raceOverlayWindow is null)
        {
            _raceOverlayWindow = new RaceEngineerOverlayWindow(string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase));
            _raceOverlayWindow.Closed += (_, _) =>
            {
                _raceOverlayWindow = null;
                if (_raceOverlayButton is not null)
                    _raceOverlayButton.Content = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "Открыть оверлей" : "Open overlay";
            };
        }
        _raceOverlayWindow.UpdateSnapshot(_recorder.RaceEngineer);
        // Keep the overlay independent from the main window. A Win32 owned window can be
        // pushed behind a borderless game when its owner loses focus.
        _raceOverlayWindow.Show();
        _raceOverlayButton.Content = string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "Скрыть оверлей" : "Hide overlay";
    }

    private void ToggleRaceEngineerOverlayLayout()
    {
        if (_raceOverlayWindow?.IsVisible != true) ShowRaceEngineerOverlay();
        _raceOverlayWindow?.ToggleEditMode();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterStop)
        {
            _timer.Stop();
            return;
        }

        if (_closeStopInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (!_recorder.IsActive)
        {
            _timer.Stop();
            return;
        }

        e.Cancel = true;
        _closeStopInProgress = true;
        _startButton.IsEnabled = false;
        _stopButton.IsEnabled = false;
        AddLog("Window close requested. Finishing the active recording safely...");
        try
        {
            await _recorder.StopAsync(_autoZipCheck.IsChecked == true);
            _closingAfterStop = true;
            _timer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            AddLog("Safe close failed: " + ex.Message);
            _closeStopInProgress = false;
            _startButton.IsEnabled = !_recorder.IsRecording;
            _stopButton.IsEnabled = _recorder.IsRecording;
        }
    }

    private void AddLog(string message)
    {
        _logRows.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_logRows.Count > 200) _logRows.RemoveAt(_logRows.Count - 1);
    }

    private void OpenErsProfilesFolder()
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(_rootText.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
            var folder = ErsProfileStore.EnsureDefaultProfiles(root);
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AddLog("Open ERS profiles failed: " + ex.Message);
        }
    }

    private void OpenRaceProfilesFolder()
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(_rootText.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
            var folder = RaceEngineerProfileStore.EnsureDefaultProfiles(root);
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AddLog("Open race profiles failed: " + ex.Message);
        }
    }


    private async Task CopyLogsAsync()
    {
        try
        {
            var text = string.Join(Environment.NewLine, _logRows.Reverse());
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                AddLog("Clipboard is not available.");
                return;
            }
            await clipboard.SetTextAsync(text);
            AddLog("Logs copied to clipboard.");
        }
        catch (Exception ex)
        {
            AddLog("Copy logs failed: " + ex.Message);
        }
    }

    private void RefreshSessions()
    {
        if (_sessionList is null) return;
        var selectedFolder = (_sessionList.SelectedItem as SessionListItem)?.FolderPath;
        try
        {
            var root = string.IsNullOrWhiteSpace(_rootText?.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
            var packs = Path.Combine(root, "telemetry_packs");
            Directory.CreateDirectory(packs);
            var sessions = Directory.GetDirectories(packs)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(SessionSummaryService.Load)
                .ToList();
            _sessionList.ItemsSource = sessions;
            _sessionList.SelectedItem = sessions.FirstOrDefault(x => string.Equals(x.FolderPath, selectedFolder, StringComparison.OrdinalIgnoreCase))
                                        ?? sessions.FirstOrDefault();
            if (sessions.Count == 0)
            {
                _selectedSessionText.Text = "No recorded sessions found in the selected storage root.";
                _globalSessionContext.Text = "No session selected";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _selectedSessionText.Text = "Session list could not be loaded: " + ex.Message;
            AddLog("Session refresh failed: " + ex.Message);
        }
    }

    private void UpdateSelectedSession()
    {
        if (_sessionList.SelectedItem is not SessionListItem session) return;
        var folder = session.FolderPath;
        var rar = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.rar").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var quality = RecordingQualityService.Load(folder);
        _selectedSessionText.Text =
            $"{session.SessionName}\n" +
            $"Track: {session.TrackName} | Started: {session.DateLabel} | Duration: {session.DurationLabel}\n" +
            $"State: {session.AnalysisState} | Classification: {session.ClassificationSource} | Laps: {session.TotalLaps:N0}\n" +
            $"Capture: {session.CaptureQuality} | Completeness: {session.Completeness} | Analysis: {session.AnalysisConfidence}\n" +
            $"Car Setup changes: {session.SetupSnapshots:N0} | Stored size: {session.SizeLabel}";
        _technicalManifestText.Text =
            $"Physical folder: {folder}\nSQLite: {Path.Combine(folder, "session.sqlite")}\nRAR: {(rar is not null ? rar : "not created")}\n\nDatabase details:\n{session.TechnicalDetails}";
        _globalSessionContext.Text =
            $"Session: {session.SessionName} | Track: {session.TrackName} | Driver: {session.PlayerName} | {session.AnalysisState} | " +
            $"Capture {session.CaptureQuality} / Completeness {session.Completeness} / Analysis {session.AnalysisConfidence}";
        if (_measuredRateText is not null)
            _measuredRateText.Text = quality?.MeasuredTelemetryRateHz is double rate
                ? $"Measured telemetry packet rate: {rate:0.0} Hz. Recommended game setting: 60 Hz."
                : "Measured telemetry packet rate: not available for this session. Recommended game setting: 60 Hz.";
        LoadFinalClassification(folder);
        foreach (var loader in _sessionContextLoaders)
        {
            try { loader(); }
            catch (Exception ex) { AddLog("Session context refresh failed: " + ex.Message); }
        }
    }

    private void LoadFinalClassification(string folder)
    {
        _classificationRows.Clear();
        var db = Path.Combine(folder, "session.sqlite");
        if (!File.Exists(db))
        {
            if (_classificationHeading is not null) _classificationHeading.Text = "Classification unavailable";
            if (_classificationNote is not null) _classificationNote.Text = "session.sqlite not found.";
            _classificationRows.Add("session.sqlite not found");
            return;
        }
        try
        {
            using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
            con.Open();
            if (!TableExists(con, "final_classification"))
            {
                if (_classificationHeading is not null) _classificationHeading.Text = "Classification unavailable";
                if (_classificationNote is not null) _classificationNote.Text = "Run analysis to determine whether UDP packet 8 was recorded.";
                _classificationRows.Add("Run Analyze selected session to build final_classification.");
                return;
            }
            var source = "legacy analysis";
            if (ColumnExists(con, "final_classification", "classification_source"))
            {
                using var sourceCommand = con.CreateCommand();
                sourceCommand.CommandText = "SELECT classification_source FROM final_classification LIMIT 1";
                source = Convert.ToString(sourceCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? source;
            }
            var lastPacket = "time unavailable";
            if (TableExists(con, "raw_packets"))
            {
                using var timeCommand = con.CreateCommand();
                timeCommand.CommandText = "SELECT MAX(received_at) FROM raw_packets";
                var rawTime = Convert.ToString(timeCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (DateTimeOffset.TryParse(rawTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime))
                    lastPacket = parsedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            }
            if (_classificationHeading is not null)
            {
                _classificationHeading.Text = string.Equals(source, "official_udp", StringComparison.OrdinalIgnoreCase)
                    ? $"Official classification (UDP packet 8, last packet {lastPacket})"
                    : $"PROVISIONAL classification (UDP packet 8 absent, last packet {lastPacket})";
            }
            var classificationIsOfficial = string.Equals(source, "official_udp", StringComparison.OrdinalIgnoreCase);
            var classificationNote = classificationIsOfficial
                ? "Official final positions and result fields were read from UDP packet 8."
                : "UDP packet 8 was not recorded. Positions are reconstructed from the latest Lap Data and may be incomplete. Official points, result reasons, total race time and tyre stints are unavailable.";
            if (ColumnExists(con, "final_classification", "classification_note"))
            {
                using var noteCommand = con.CreateCommand();
                noteCommand.CommandText = "SELECT classification_note FROM final_classification WHERE classification_note IS NOT NULL AND classification_note <> '' LIMIT 1";
                classificationNote = Convert.ToString(noteCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? classificationNote;
            }
            if (_classificationNote is not null)
            {
                _classificationNote.Text = classificationNote;
                _classificationNote.Foreground = classificationIsOfficial ? Brushes.LightGreen : Brushes.Orange;
            }
            using var cmd = con.CreateCommand();
            var nameColumn = ColumnExists(con, "final_classification", "display_name") ? "display_name" : "name";
            var shortColumn = ColumnExists(con, "final_classification", "short_name") ? "short_name" : "''";
            cmd.CommandText = $"""
            SELECT position, car_idx, is_player, {nameColumn}, {shortColumn}, lap_num, last_lap_time_ms, best_lap_ms, penalties, warnings
            FROM final_classification
            ORDER BY CASE WHEN position IS NULL OR position <= 0 THEN 1 ELSE 0 END, position, car_idx
            """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pos = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                var car = reader.GetInt32(1);
                var isPlayer = reader.GetInt32(2) == 1;
                var name = CleanDisplayName(reader.IsDBNull(3) ? "" : reader.GetString(3));
                if (isPlayer) name = "YOU";
                var code = reader.IsDBNull(4) ? (isPlayer ? "YOU" : $"C{car:00}") : DriverAliasRow.SafeShort(reader.GetString(4), car, isPlayer);
                var lap = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                var last = reader.IsDBNull(6) ? 0 : Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture);
                var best = reader.IsDBNull(7) ? 0 : Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture);
                var pen = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                var warn = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                var position = pos > 0 ? $"P{pos,2}" : "P ?";
                _classificationRows.Add($"{position}  #{car:00}  {code,-4}  {name,-22}  Lap {lap}  Best {LapOption.FormatLapTime(best)}  Last {LapOption.FormatLapTime(last)}  Pen {pen}s  W {warn}");
            }
            if (_classificationRows.Count == 0) _classificationRows.Add("No classification rows found.");
        }
        catch (Exception ex)
        {
            _classificationRows.Add("Classification load failed: " + ex.Message);
        }
    }

    private void LoadDriverAliasEditor()
    {
        if (_driverAliasPanel is null) return;
        _driverAliasPanel.Children.Clear();
        _aliasBoxes.Clear();
        _shortAliasBoxes.Clear();
        _aliasesDirty = false;
        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            _driverAliasStatus.Text = "Select a session first.";
            return;
        }

        try
        {
            var rows = DriverAliasService.LoadRows(folder);
            if (rows.Count == 0)
            {
                _driverAliasStatus.Text = "No classification data. Run Analyze selected session.";
                _driverAliasPanel.Children.Add(new TextBlock
                {
                    Text = "Run Analyze selected session first.",
                    Foreground = Brushes.White,
                    Margin = new Thickness(8)
                });
                return;
            }

            _driverAliasStatus.Text = $"Loaded {rows.Count} cars. Display name is used in reports; code is the compact legend label.";
            _driverAliasPanel.Children.Add(BuildAliasHeader());
            foreach (var row in rows)
            {
                _driverAliasPanel.Children.Add(BuildAliasRow(row));
            }
        }
        catch (Exception ex)
        {
            _driverAliasStatus.Text = "Driver aliases load failed: " + ex.Message;
            AddLog("Driver aliases load failed: " + ex.Message);
        }
    }

    private Control BuildAliasHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,2*,2*,110"),
            Margin = new Thickness(8, 8, 8, 2)
        };
        AddAliasText(grid, "Pos / Car", 0, bold: true);
        AddAliasText(grid, "Original name", 1, bold: true);
        AddAliasText(grid, "Display name", 2, bold: true);
        AddAliasText(grid, "Code", 3, bold: true);
        return grid;
    }

    private Control BuildAliasRow(DriverAliasRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,2*,2*,110"),
            Margin = new Thickness(8, 2, 8, 2)
        };
        var bg = row.IsPlayer ? Hex(0x213044) : Hex(0x1A2028);
        var border = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = grid
        };

        AddAliasText(grid, $"P{row.Position}\n#{row.CarIndex:00}", 0);
        AddAliasText(grid, row.OriginalName, 1);
        var box = new TextBox
        {
            Text = row.DisplayName,
            MinWidth = 220,
            PlaceholderText = "Display name"
        };
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);
        _aliasBoxes[row.CarIndex] = box;
        box.TextChanged += (_, _) => MarkAliasesDirty();

        var shortBox = new TextBox
        {
            Text = row.ShortName,
            Width = 90,
            PlaceholderText = "HAM"
        };
        Grid.SetColumn(shortBox, 3);
        grid.Children.Add(shortBox);
        _shortAliasBoxes[row.CarIndex] = shortBox;
        shortBox.TextChanged += (_, _) => MarkAliasesDirty();
        return border;
    }

    private void MarkAliasesDirty()
    {
        if (_aliasesDirty) return;
        _aliasesDirty = true;
        if (_driverAliasStatus is not null) _driverAliasStatus.Text = "Unsaved alias changes. Use Save all aliases to apply them.";
    }

    private static void AddAliasText(Grid grid, string text, int column, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Margin = new Thickness(4)
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private void SaveAliasRow(DriverAliasRow row, string displayName, string shortName)
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null) return;
        try
        {
            DriverAliasService.SaveAlias(folder, row.CarIndex, row.OriginalName, displayName, shortName);
            AddLog($"Alias saved: #{row.CarIndex:00} = {shortName} / {displayName}");
            LoadFinalClassification(folder);
            LoadCompareLaps();
        }
        catch (Exception ex)
        {
            AddLog("Alias save failed: " + ex.Message);
            _driverAliasStatus.Text = "Alias save failed: " + ex.Message;
        }
    }

    private void SaveAllAliases()
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            _driverAliasStatus.Text = "Select a session first.";
            return;
        }
        try
        {
            var rows = DriverAliasService.LoadRows(folder);
            foreach (var row in rows)
            {
                if (_aliasBoxes.TryGetValue(row.CarIndex, out var box))
                {
                    var shortName = _shortAliasBoxes.TryGetValue(row.CarIndex, out var shortBox) ? shortBox.Text ?? row.ShortName : row.ShortName;
                    DriverAliasService.SaveAlias(folder, row.CarIndex, row.OriginalName, box.Text ?? row.DisplayName, shortName);
                }
            }
            AddLog("All driver aliases saved.");
            _driverAliasStatus.Text = "Aliases saved. Lap Compare/Legend will use Display name after reload.";
            _aliasesDirty = false;
            LoadFinalClassification(folder);
            LoadCompareLaps();
        }
        catch (Exception ex)
        {
            AddLog("Save all aliases failed: " + ex.Message);
            _driverAliasStatus.Text = "Save all aliases failed: " + ex.Message;
        }
    }

    private static bool ColumnExists(SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }


    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static string CleanDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "F1 Generic";
        if (name.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "F1 Generic";
        var trimmed = name.Trim();
        return trimmed.Length > 18 ? trimmed[..18] : trimmed;
    }

    private static string? ReadPreferredSessionName(string folder)
    {
        try
        {
            var database = Path.Combine(folder, "session.sqlite");
            if (!File.Exists(database)) return null;
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Private");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM session_metadata WHERE key='session_name' LIMIT 1";
            var text = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { }
        return null;
    }



    private void LoadRaceReportDrivers()
    {
        if (_raceReportDriver is null) return;
        if (_busy)
        {
            _raceReportStatus.Text = "Wait for analysis to finish before loading reports.";
            return;
        }

        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _raceReportStatus.Text = "Select a session first.";
                return;
            }

            _raceReportDrivers = RaceReportDataService.LoadDrivers(folder);
            _raceReportDriver.ItemsSource = _raceReportDrivers;
            var player = _raceReportDrivers.FirstOrDefault(x => x.IsPlayer);
            _raceReportDriver.SelectedItem = player ?? _raceReportDrivers.FirstOrDefault();
            _raceReportStatus.Text = _raceReportDrivers.Count == 0
                ? "No driver data. Analyze the selected session first."
                : $"Loaded {_raceReportDrivers.Count:N0} drivers. Select a driver and table view.";
            LoadRaceReportRows();
        }
        catch (Exception ex)
        {
            _raceReportStatus.Text = "Race Report load failed: " + ex.Message;
            AddLog("Race Report load failed: " + ex.Message);
        }
    }

    private void LoadRaceReportRows()
    {
        if (_raceReportDriver is null || _raceReportView is null) return;
        var driver = _raceReportDriver.SelectedItem as RaceReportDriverOption;
        if (driver is null) return;

        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null) return;
            var view = _raceReportView.SelectedItem?.ToString() ?? RaceReportDataService.Views[0];
            var rows = RaceReportDataService.LoadRows(folder, driver.CarIndex);
            if (_raceReportCleanOnly?.IsChecked == true) rows = rows.Where(x => x.CleanLap).ToList();
            if (_raceReportProblemsOnly?.IsChecked == true) rows = rows.Where(x => x.HasProblem).ToList();

            if (_raceReportLegend is not null) _raceReportLegend.Text = RaceReportDataService.CompactLegendForView(view);
            if (_raceReportSummary is not null) _raceReportSummary.Text = RaceAnalysisDataService.BuildRaceSummary(rows);
            BuildRaceReportTable(rows, view);

            var problemCount = rows.Count(x => x.HasProblem);
            var pitCount = rows.Count(x => x.PitThisLap);
            _raceReportStatus.Text = $"{driver.Identity}: {rows.Count:N0} laps shown, {pitCount:N0} pit laps, {problemCount:N0} flagged laps. Table: {view}.";
        }
        catch (Exception ex)
        {
            _raceReportStatus.Text = "Race Report rows failed: " + ex.Message;
            AddLog("Race Report rows failed: " + ex.Message);
        }
    }

    private async Task AnalyzeSelectedSessionAsync()
    {
        if (_recorder.IsRecording)
        {
            AddLog("Stop recording before analysis so all pending database writes can be drained safely.");
            return;
        }
        if (_busy)
        {
            AddLog("Busy: wait until the current operation finishes.");
            return;
        }

        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            AddLog("Select a session first.");
            return;
        }
        _busy = true;
        try
        {
            AddLog("Analyzing selected session...");
            var result = await AnalysisEngine.AnalyzeSessionAsync(folder, message => Dispatcher.UIThread.Post(() => AddLog(message)));
            AddLog(result.Summary);
            try
            {
                var learning = RaceProfileLearningService.Learn(folder, _settings.RootFolder);
                AddLog(learning.Summary);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or InvalidOperationException)
            {
                AddLog("Race profile learning skipped: " + ex.Message);
            }
            var dbPath = Path.Combine(folder, "session.sqlite");
            if (File.Exists(dbPath))
            {
                try
                {
                    var rar = await Task.Run(() => SessionPackager.CreateRar(folder, dbPath, ReadPreferredSessionName(folder), _settings.WinRarPath));
                    AddLog("RAR refreshed: " + rar);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    AddLog("Analysis completed, but RAR failed: " + ex.Message);
                }
            }
            InvalidateCompareAfterAnalysis();
            RefreshSessions();
            if (_raceReportDriver is not null) LoadRaceReportDrivers();
        }
        catch (Exception ex)
        {
            AddLog("Analyze failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ExportSelectedRaceSummaryAsync()
    {
        if (_recorder.IsRecording)
        {
            AddLog("Stop recording before exporting the workbook.");
            return;
        }
        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            AddLog("Select a session first.");
            return;
        }
        try
        {
            var path = await Task.Run(() => RaceSummaryWorkbookExporter.Export(folder));
            AddLog("Race summary Excel created: " + path);
            RefreshSessions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            AddLog("Race summary Excel failed: " + ex.Message);
        }
    }


    private string? GetSelectedSessionFolder()
    {
        if (_sessionList?.SelectedItem is SessionListItem session) return session.FolderPath;
        var name = _sessionList?.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var root = string.IsNullOrWhiteSpace(_rootText.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
        return Path.Combine(root, "telemetry_packs", name);
    }

    private void InvalidateCompareAfterAnalysis()
    {
        try
        {
            _compareDrivers = new List<DriverOption>();
            _lastComparedLaps.Clear();
            _compareLapOptions.Clear();
            _compareRows.Clear();
            _compareLegendItems.Clear();
            _compareChart?.SetData(Array.Empty<CompareSeries>(), _compareMetric?.SelectedItem?.ToString() ?? "speed");

            _updatingCompareSlots = true;
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    if (_compareDriverBoxes[i] is not null)
                    {
                        _compareDriverBoxes[i].ItemsSource = null;
                        _compareDriverBoxes[i].SelectedItem = null;
                    }
                    if (_compareLapBoxes[i] is not null)
                    {
                        _compareLapBoxes[i].ItemsSource = null;
                        _compareLapBoxes[i].SelectedItem = null;
                    }
                }
            }
            finally
            {
                _updatingCompareSlots = false;
            }

            UpdateReferenceText();
            if (_compareStatus is not null)
                _compareStatus.Text = "Analysis refreshed. The previous plot was cleared to avoid mixing data versions.";
        }
        catch
        {
            // UI refresh must never break a completed analysis.
        }
    }

    private void LoadCompareLaps()
    {
        if (_busy)
        {
            _compareStatus.Text = "Wait for analysis to finish before loading comparison data.";
            AddLog("Compare load blocked: analysis/stop is still running.");
            return;
        }

        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _compareStatus.Text = "Select a session first.";
                return;
            }

            var requestedCleanOnly = _compareCleanOnly.IsChecked == true;
            _compareDrivers = CompareDataService.LoadDrivers(folder, requestedCleanOnly);
            if (_compareDrivers.Count == 0 && requestedCleanOnly)
            {
                _compareCleanOnly.IsChecked = false;
                _compareDrivers = CompareDataService.LoadDrivers(folder, cleanOnly: false);
                AddLog("No clean laps found. Clean laps only disabled for this comparison.");
            }
            if (_compareDrivers.Count == 0)
            {
                _compareStatus.Text = "No laps are available. Analyze the selected session first.";
                return;
            }

            _updatingCompareSlots = true;
            try
            {
                var player = _compareDrivers.FirstOrDefault(x => x.IsPlayer);
                var ordered = _compareDrivers.OrderBy(x => x.BestCleanLapMs).ToList();
                for (var i = 0; i < 6; i++)
                {
                    _compareDriverBoxes[i].ItemsSource = _compareDrivers;
                    DriverOption? pick = null;
                    if (i == 0) pick = player ?? ordered.FirstOrDefault();
                    else pick = ordered.Where(x => player is null || x.CarIndex != player.CarIndex).Skip(i - 1).FirstOrDefault();
                    _compareDriverBoxes[i].SelectedItem = pick;
                    UpdateLapComboForSlot(i, selectBest: true);
                }
            }
            finally
            {
                _updatingCompareSlots = false;
            }

            _compareStatus.Text = $"Loaded {_compareDrivers.Count:N0} drivers. Slot 1 is Reference.";
            UpdateReferenceText();
        }
        catch (Exception ex)
        {
            _compareStatus.Text = "Load drivers/laps failed: " + ex.Message;
            AddLog("Compare load failed: " + ex.Message);
        }
    }

    private void ClearCompareSlot(int slotIndex)
    {
        _updatingCompareSlots = true;
        try
        {
            _compareDriverBoxes[slotIndex].SelectedItem = null;
            _compareLapBoxes[slotIndex].ItemsSource = null;
            _compareLapBoxes[slotIndex].SelectedItem = null;
        }
        finally
        {
            _updatingCompareSlots = false;
        }
        if (slotIndex == 0)
        {
            _lastComparedLaps.Clear();
            _compareChart.SetData(Array.Empty<CompareSeries>(), _compareMetric.SelectedItem?.ToString() ?? "speed");
            _compareLegendItems.Clear();
            _compareStatus.Text = "Reference cleared. Select a new lap in Slot 1 before plotting.";
        }
        else
        {
            _compareStatus.Text = $"Slot {slotIndex + 1} disabled.";
            if (_lastComparedLaps.Count > 0) PlotCurrentCompareSlots();
        }
        UpdateReferenceText();
    }

    private void UpdateLapComboForSlot(int slotIndex, bool selectBest = true)
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null) return;
        if (_compareDriverBoxes[slotIndex].SelectedItem is not DriverOption driver)
        {
            _compareLapBoxes[slotIndex].ItemsSource = null;
            return;
        }

        var laps = CompareDataService.LoadLapOptionsForDriver(folder, driver.CarIndex, _compareCleanOnly.IsChecked == true);
        _compareLapBoxes[slotIndex].ItemsSource = laps;
        if (selectBest && laps.Count > 0) _compareLapBoxes[slotIndex].SelectedIndex = 0;
        UpdateReferenceText();
    }

    private List<LapOption> GetSelectedCompareLaps()
    {
        var laps = new List<LapOption>();
        for (var i = 0; i < _visibleCompareSlots; i++)
        {
            if (_compareLapBoxes[i].SelectedItem is LapOption lap) laps.Add(lap);
        }
        return laps;
    }

    private void PlotCurrentCompareSlots()
    {
        if (_compareLapBoxes[0].SelectedItem is not LapOption)
        {
            _compareStatus.Text = "Select the reference lap in Slot 1 first.";
            return;
        }
        var selected = GetSelectedCompareLaps();
        if (selected.Count == 0)
        {
            _compareStatus.Text = "Select at least the reference lap.";
            return;
        }
        PlotLaps(selected);
    }

    private void PlotSelectedCompareLaps()
    {
        PlotCurrentCompareSlots();
    }

    private void PlotTopBestCompareLaps()
    {
        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _compareStatus.Text = "Select a session first.";
                return;
            }
            var best = CompareDataService.LoadBestCleanLaps(folder, 6);
            if (best.Count == 0)
            {
                best = CompareDataService.LoadBestAvailableLaps(folder, 6);
                AddLog("No clean laps found. Top 6 best uses dirty/best available laps.");
            }
            if (best.Count == 0)
            {
                _compareStatus.Text = "No laps are available. Analyze the selected session first.";
                return;
            }
            FillCompareSlots(best);
            PlotLaps(best);
        }
        catch (Exception ex)
        {
            _compareStatus.Text = "Plot top 6 failed: " + ex.Message;
            AddLog("Compare top 6 failed: " + ex.Message);
        }
    }

    private void PlotYouVsTopCompareLaps()
    {
        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _compareStatus.Text = "Select a session first.";
                return;
            }
            var laps = CompareDataService.LoadYouVsTop(folder, 6);
            if (laps.Count == 0)
            {
                laps = CompareDataService.LoadYouVsTopAvailable(folder, 6);
                AddLog("No clean laps found. YOU vs top 5 uses dirty/best available laps.");
            }
            if (laps.Count == 0)
            {
                _compareStatus.Text = "No laps are available for YOU vs top 5. Analyze the selected session first.";
                return;
            }
            FillCompareSlots(laps);
            PlotLaps(laps);
        }
        catch (Exception ex)
        {
            _compareStatus.Text = "YOU vs top 5 failed: " + ex.Message;
            AddLog("YOU vs top 5 failed: " + ex.Message);
        }
    }

    private void FillCompareSlots(IReadOnlyList<LapOption> laps)
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null) return;
        if (_compareDrivers.Count == 0) _compareDrivers = CompareDataService.LoadDrivers(folder, _compareCleanOnly.IsChecked == true);

        EnsureVisibleCompareSlots(laps.Count);
        _updatingCompareSlots = true;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                _compareDriverBoxes[i].ItemsSource = _compareDrivers;
                if (i >= laps.Count)
                {
                    _compareDriverBoxes[i].SelectedItem = null;
                    _compareLapBoxes[i].ItemsSource = null;
                    continue;
                }

                var lap = laps[i];
                var driver = _compareDrivers.FirstOrDefault(x => x.CarIndex == lap.CarIndex);
                _compareDriverBoxes[i].SelectedItem = driver;
                var driverLaps = CompareDataService.LoadLapOptionsForDriver(folder, lap.CarIndex, _compareCleanOnly.IsChecked == true);
                _compareLapBoxes[i].ItemsSource = driverLaps;
                _compareLapBoxes[i].SelectedItem = driverLaps.FirstOrDefault(x => x.CarIndex == lap.CarIndex && x.LapNum == lap.LapNum) ?? driverLaps.FirstOrDefault();
            }
        }
        finally
        {
            _updatingCompareSlots = false;
        }
        UpdateReferenceText();
    }


    private static Control BuildLegendItem(string tag, string name, Color color)
    {
        var swatch = new Border
        {
            Width = 18,
            Height = 10,
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 8, 0)
        };
        var text = new TextBlock
        {
            Text = $"{tag}  {name}",
            Foreground = Brushes.White,
            FontFamily = FontFamily.Parse("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 255
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 5, 6, 5),
            Children = { swatch, text }
        };
    }

    private void ApplyBestLapToAllSlots()
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            _compareStatus.Text = "Select a session first.";
            return;
        }
        for (var i = 0; i < _visibleCompareSlots; i++)
        {
            if (_compareDriverBoxes[i].SelectedItem is not DriverOption driver) continue;
            var laps = CompareDataService.LoadLapOptionsForDriver(folder, driver.CarIndex, _compareCleanOnly.IsChecked == true);
            if (laps.Count == 0 && _compareCleanOnly.IsChecked == true)
                laps = CompareDataService.LoadLapOptionsForDriver(folder, driver.CarIndex, cleanOnly: false);
            _compareLapBoxes[i].ItemsSource = laps;
            if (laps.Count > 0) _compareLapBoxes[i].SelectedIndex = 0;
        }
        UpdateReferenceText();
        _compareStatus.Text = "Selected each driver's best available lap in all slots.";
        PlotCurrentCompareSlots();
    }

    private void ApplyReferenceLapNumberToAllSlots()
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null)
        {
            _compareStatus.Text = "Select a session first.";
            return;
        }
        if (_compareLapBoxes[0].SelectedItem is not LapOption reference)
        {
            _compareStatus.Text = "Select a reference lap in Slot 1 first.";
            return;
        }

        var changed = 0;
        var missed = 0;
        for (var i = 0; i < _visibleCompareSlots; i++)
        {
            if (_compareDriverBoxes[i].SelectedItem is not DriverOption driver) continue;
            var laps = CompareDataService.LoadLapOptionsForDriver(folder, driver.CarIndex, _compareCleanOnly.IsChecked == true);
            if (laps.Count == 0 && _compareCleanOnly.IsChecked == true)
                laps = CompareDataService.LoadLapOptionsForDriver(folder, driver.CarIndex, cleanOnly: false);
            _compareLapBoxes[i].ItemsSource = laps;
            var same = laps.FirstOrDefault(x => x.LapNum == reference.LapNum);
            if (same is not null)
            {
                _compareLapBoxes[i].SelectedItem = same;
                changed++;
            }
            else
            {
                if (laps.Count > 0) _compareLapBoxes[i].SelectedIndex = 0;
                missed++;
            }
        }
        UpdateReferenceText();
        _compareStatus.Text = $"Applied Lap {reference.LapNum} to {changed} slot(s). {missed} slot(s) had no such lap and kept best available.";
        PlotCurrentCompareSlots();
    }

    private void ApplyCompareZoom()
    {
        _zoomFromM = TryParseDistance(_zoomFromText.Text);
        _zoomToM = TryParseDistance(_zoomToText.Text);
        if (_zoomFromM is not null && _zoomToM is not null && _zoomToM <= _zoomFromM)
        {
            _compareStatus.Text = "Zoom end must be greater than zoom start.";
            return;
        }
        _compareChart.SetZoom(_zoomFromM, _zoomToM);
        if (_lastComparedLaps.Count > 0) PlotLaps(_lastComparedLaps);
    }

    private void ResetCompareZoom()
    {
        _zoomFromM = null;
        _zoomToM = null;
        _zoomFromText.Text = "";
        _zoomToText.Text = "";
        _compareChart.SetZoom(null, null);
        if (_lastComparedLaps.Count > 0) PlotLaps(_lastComparedLaps);
    }

    private static int? TryParseDistance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), out var value) ? value : null;
    }

    private void UpdateReferenceText()
    {
        if (_referenceText is null) return;
        var reference = _compareLapBoxes[0]?.SelectedItem as LapOption;
        _referenceText.Text = reference is null
            ? "Reference: not selected"
            : $"Reference: #{reference.CarIndex:00} {reference.Identity} | Lap {reference.LapNum} | {LapOption.FormatLapTime(reference.LapTimeMs)} | {(reference.CleanLap ? "clean" : "dirty")}";
    }

    private void PlotLaps(List<LapOption> laps)
    {
        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null) return;
            var metric = _compareMetric.SelectedItem?.ToString() ?? "speed";
            var series = CompareDataService.LoadSeries(folder, laps, metric, _zoomFromM, _zoomToM);
            _compareChart.SetZoom(_zoomFromM, _zoomToM);
            _compareChart.SetData(series, metric);
            _lastComparedLaps = laps.ToList();
            _compareLegendItems.Clear();
            for (var i = 0; i < series.Count; i++)
            {
                var tag = i == 0 ? "REF" : $"C{i}";
                _compareLegendItems.Add(BuildLegendItem(tag, series[i].Name, ChartColor(i)));
            }
            UpdateReferenceText();
            var zoom = _zoomFromM is null && _zoomToM is null ? "full lap" : $"zoom {_zoomFromM?.ToString() ?? "start"}-{_zoomToM?.ToString() ?? "end"}m";
            _compareStatus.Text = $"Plotted {laps.Count} lap(s), metric: {metric}, {zoom}.";
        }
        catch (Exception ex)
        {
            _compareStatus.Text = "Plot failed: " + ex.Message;
            AddLog("Compare plot failed: " + ex.Message);
        }
    }

    private void PlotTrackMapFromCompare()
    {
        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _trackMapStatus.Text = "Select a session first.";
                return;
            }
            var selected = GetSelectedCompareLaps();
            if (selected.Count < 2 && _lastComparedLaps.Count >= 2) selected = _lastComparedLaps.Take(2).ToList();
            if (selected.Count < 2)
            {
                _trackMapStatus.Text = "Select at least two laps: Slot 1 Reference and Slot 2 Compare.";
                return;
            }
            PlotTrackMap(folder, selected[0], selected[1]);
        }
        catch (Exception ex)
        {
            _trackMapStatus.Text = "Track map failed: " + ex.Message;
            AddLog("Track map failed: " + ex.Message);
        }
    }

    private void PlotTrackMapYouVsFastest()
    {
        try
        {
            var folder = GetSelectedSessionFolder();
            if (folder is null)
            {
                _trackMapStatus.Text = "Select a session first.";
                return;
            }
            var laps = LoadBestReferenceVsYou(folder);
            if (laps.Count < 2)
            {
                _trackMapStatus.Text = "Two laps for Best vs YOU were not found. Analyze the selected session first.";
                return;
            }
            FillCompareSlots(laps);
            PlotLaps(laps);
            PlotTrackMap(folder, laps[0], laps[1]);
        }
        catch (Exception ex)
        {
            _trackMapStatus.Text = "YOU vs fastest map failed: " + ex.Message;
            AddLog("YOU vs fastest map failed: " + ex.Message);
        }
    }

    private void ReplotTrackMapIfPossible()
    {
        if (_trackMapMetric is null || _trackMapContrast is null) return;
        var folder = GetSelectedSessionFolder();
        if (folder is null) return;
        var selected = GetSelectedCompareLaps();
        if (selected.Count < 2 && _lastComparedLaps.Count >= 2) selected = _lastComparedLaps.Take(2).ToList();
        if (selected.Count >= 2) PlotTrackMap(folder, selected[0], selected[1]);
    }

    private void ClearTrackViews()
    {
        _lastTrackMapData = null;
        _trackMapControl.SetData(null);
        _trackDetailControl?.SetData(null);
        _trackMapStatus.Text = "Track map cleared.";
        if (_trackDetailStatus is not null) _trackDetailStatus.Text = "Track detail cleared.";
        if (_trackMapInsightList is not null) _trackMapInsightList.ItemsSource = Array.Empty<TrackMapInsight>();
        if (_trackDetailZoneList is not null) _trackDetailZoneList.ItemsSource = Array.Empty<TrackMapInsight>();
    }

    private void PlotTrackMap(string folder, LapOption reference, LapOption compare)
    {
        var root = string.IsNullOrWhiteSpace(_rootText.Text) ? DefaultRootFolder() : _rootText.Text.Trim();
        var metric = _trackMapMetric.SelectedItem?.ToString() ?? "segment_loss_ms";
        var contrast = ParseTrackMapContrast();
        var data = TrackMapDataService.Build(root, folder, reference, compare, metric);
        _lastTrackMapData = data;
        _trackMapControl.SetData(data, contrast);
        _trackDetailControl?.SetData(data, contrast, data.Insights.FirstOrDefault());
        _trackMapStatus.Text = data.Status + "\n" +
            $"Heatmap: blue = compare gains, white = neutral, red = compare loses time. Contrast x{contrast:0.##}.\n" +
            "Austria uses embedded Racenet spline boundaries: surface, white lines, track limits and run-off. " +
            "Top-zones are highlighted directly on the track.";
        UpdateTrackMapCorners(data.Profile);
        UpdateTrackMapInsights(data);
    }

    private double ParseTrackMapContrast()
    {
        var text = _trackMapContrast?.SelectedItem?.ToString() ?? "x2";
        text = text.Trim().TrimStart('x', 'X');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.25, 32.0) : 2.0;
    }

    private static List<LapOption> LoadBestReferenceVsYou(string folder)
    {
        var laps = CompareDataService.LoadYouVsTop(folder, 6);
        if (laps.Count == 0) laps = CompareDataService.LoadYouVsTopAvailable(folder, 6);
        var you = laps.FirstOrDefault(x => x.IsPlayer);
        var best = laps.Where(x => !x.IsPlayer).OrderBy(x => x.LapTimeMs).FirstOrDefault();
        if (you is null || best is null) return laps.Take(2).ToList();
        return new List<LapOption> { best, you };
    }

    private void UpdateTrackMapInsights(TrackMapRenderData data)
    {
        if (_trackMapInsightList is null) return;
        _updatingTrackZoneSelection = true;
        try
        {
            _trackMapInsightList.ItemsSource = data.Insights;
            if (_trackDetailZoneList is not null) _trackDetailZoneList.ItemsSource = data.Insights;
            _trackMapInsightList.SelectedItem = data.Insights.FirstOrDefault();
            if (_trackDetailZoneList is not null) _trackDetailZoneList.SelectedItem = data.Insights.FirstOrDefault();
        }
        finally
        {
            _updatingTrackZoneSelection = false;
        }

        if (data.Insights.Count == 0)
        {
            if (_trackDetailStatus is not null) _trackDetailStatus.Text = "No meaningful gain/loss zones.";
            return;
        }

        SelectTrackMapInsight(data.Insights[0]);
    }

    private void SelectTrackMapInsight(TrackMapInsight? insight)
    {
        if (insight is null) return;
        _updatingTrackZoneSelection = true;
        try
        {
            if (_trackMapInsightList is not null && !Equals(_trackMapInsightList.SelectedItem, insight)) _trackMapInsightList.SelectedItem = insight;
            if (_trackDetailZoneList is not null && !Equals(_trackDetailZoneList.SelectedItem, insight)) _trackDetailZoneList.SelectedItem = insight;
        }
        finally
        {
            _updatingTrackZoneSelection = false;
        }

        _trackMapControl.SetSelectedInsight(insight);
        _trackDetailControl?.SetSelectedInsight(insight);
        if (_trackDetailStatus is not null) _trackDetailStatus.Text = $"Selected zone: {insight.Label}. Closest context: {insight.NearestCornerLabel}.";
    }

    private void UpdateTrackMapCorners(TrackProfile? profile)
    {
        if (_trackMapCornerList is null) return;
        if (profile is null)
        {
            _trackMapCornerList.ItemsSource = new[] { "No track profile loaded." };
            return;
        }
        _trackMapCornerList.ItemsSource = profile.Corners
            .OrderBy(c => c.DistanceM)
            .Select(c => $"{c.DistanceM,6:0}m  {c.Label}{(c.IsEstimated ? " *" : "")}")
            .ToList();
    }

    private void OpenSelectedSessionFolder()
    {
        var folder = GetSelectedSessionFolder();
        if (folder is null) return;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private static string DefaultRootFolder() => AppSettingsService.DefaultRootFolder();
}
