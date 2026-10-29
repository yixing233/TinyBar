using System.Text.Json;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskbarInfo;
using Windows.Media.Control;

namespace TinyBar.Settings;

public sealed partial class MainWindow : Window
{
    private const string SharedSettingsAppliedEventName = "TinyBar.Settings.Apply";
    private static readonly string[] FontWeightOptions = ["常规", "细体", "半粗", "粗体"];
    private sealed record DisplayOption(string DeviceName, string Label);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr monitorRect, IntPtr data);
    private const uint MonitorInfofPrimary = 0x00000001;
    private const int DefaultWidth = 920;
    private const int DefaultHeight = 640;
    private const int MinimumWidthDip = 620;
    private const int MinimumHeightDip = 540;
    private const int MaximumWidthDip = 1120;
    private const int MaximumHeightDip = 760;
    private const string LyricsComponentTag = "Lyrics";
    private const int GwlStyle = -16;
    private const long WsMaximizeBox = 0x00010000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint HtCaption = 0x0002;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int DwmwaBorderColor = 34;
    private const int LightWindowBorderColor = 0x00E6E6E6;
    private const int DarkWindowBorderColor = 0x00404040;
    private static readonly UIntPtr WindowSizeSubclassId = new(1);
    private static readonly uint SettingsNavigateMessage =
        RegisterWindowMessage("TinyBar.Settings.Navigate");

    private readonly SettingsDocument _settings;
    private readonly string _settingsPath;
    private readonly UpdateService _updateService = new();
    private readonly DispatcherQueueTimer _successInfoBarTimer;
    private readonly DispatcherQueueTimer _applySettingsTimer;
    private readonly SubclassProc _windowSizeSubclassProc;
    private readonly bool _keepAlive;
    private readonly string? _updateEventName;
    private bool _didSave;
    private bool _changedTaskbarLyricOffset;
    private bool _resetTaskbarPerformancePosition;
    private bool _resetTaskbarTranslateButtonPosition;
    private bool _resetTaskbarWaterReminderPosition;
    private bool _resetDesktopWidgetPosition;
    private ContentControl? _waterReminderStatisticsHost;
    private IntPtr _windowHandle;
    private bool _windowSizeSubclassInstalled;
    private string _lastLyricsTab = "Typography";
    private string _lastGeneralTab = "General";
    private string _lastQuickTranslateTab = "General";

    public MainWindow(bool keepAlive = false, string? updateEventName = null)
    {
        _keepAlive = keepAlive;
        _updateEventName = updateEventName;
        _windowSizeSubclassProc = WindowSizeSubclassProc;
        _settingsPath = ResolveSettingsPath();
        _settings = SettingsDocument.Load(_settingsPath);
        InitializeComponent();
        ApplyApplicationTheme();
        ApplyWindowMaterial();
        ApplyWindowIcon();
        _successInfoBarTimer = DispatcherQueue.CreateTimer();
        _successInfoBarTimer.Interval = TimeSpan.FromSeconds(3);
        _successInfoBarTimer.IsRepeating = false;
        _successInfoBarTimer.Tick += (_, _) => ErrorInfoBar.IsOpen = false;
        _applySettingsTimer = DispatcherQueue.CreateTimer();
        _applySettingsTimer.Interval = TimeSpan.FromMilliseconds(300);
        _applySettingsTimer.IsRepeating = false;
        _applySettingsTimer.Tick += (_, _) => ApplySettings(showSuccessMessage: false);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindowChrome();
        InstallWindowSizeConstraints();
        ResizeToInitialSize();
        AppWindow.Closing += AppWindow_Closing;

        string initialPage = ResolveInitialPage();
        NavigateTo(initialPage);
        Closed += MainWindow_Closed;
    }

    private void NavigateTo(string tag)
    {
        string navigationTag = IsLyricSubPageTag(tag) ? LyricsComponentTag : tag;
        if (IsGeneralSubPageTag(tag) || tag == "About")
        {
            navigationTag = "GeneralSettings";
        }
        else if (IsTranslateSubPageTag(tag))
        {
            navigationTag = "QuickTranslate";
        }
        Navigate(navigationTag);
        IEnumerable<NavigationViewItem> navigationItems = NavMenu.MenuItems
            .Concat(NavMenu.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .SelectMany(item => item.MenuItems.OfType<NavigationViewItem>().Append(item));
        NavMenu.SelectedItem = navigationItems.FirstOrDefault(item => item.Tag as string == navigationTag)
            ?? navigationItems.FirstOrDefault(item => item.Tag is string);

        if (IsLyricSubPageTag(tag) && ContentFrame.Content is LyricsSettingsPage lyricsPage)
        {
            lyricsPage.SelectTab(tag);
        }
        else if (IsGeneralSubPageTag(tag) && ContentFrame.Content is GeneralSettingsPage generalPage)
        {
            generalPage.SelectTab(tag);
        }
        else if (tag == "About" && ContentFrame.Content is GeneralSettingsPage generalAboutPage)
        {
            generalAboutPage.SelectTab("AboutTab");
        }
        else if (IsTranslateSubPageTag(tag) && ContentFrame.Content is QuickTranslateSettingsPage translatePage)
        {
            translatePage.SelectTab(tag is "TranslateProviders" or "QuickTranslateProviders" or "Providers" ? "Providers" : "General");
        }
    }

    private static bool IsLyricSubPageTag(string? tag) =>
        tag is "Typography" or "Visual" or "Floating" or "DesktopWidget" or "Applications";

    private static bool IsGeneralSubPageTag(string? tag) =>
        tag is "General" or "AboutTab";

    private static bool IsTranslateSubPageTag(string? tag) =>
        tag is "TranslateGeneral" or "TranslateProviders" or "QuickTranslateProviders" or "Providers";

    public void HideForReuse()
    {
        if (_keepAlive) AppWindow.Hide();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_keepAlive) return;

        args.Cancel = true;
        AppWindow.Hide();
    }

    private void ApplyWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TinyBar.ico");
        if (!File.Exists(iconPath)) iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LyricsX.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
    }

    private void ConfigureWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_windowHandle == IntPtr.Zero) return;

        long style = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
        SetWindowLongPtr(_windowHandle, GwlStyle, new IntPtr(style & ~WsMaximizeBox));
        SetWindowPos(_windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
        ApplyWindowBorderTheme();
    }

    private void InstallWindowSizeConstraints()
    {
        if (_windowHandle == IntPtr.Zero) return;

        _windowSizeSubclassInstalled = SetWindowSubclass(
            _windowHandle,
            _windowSizeSubclassProc,
            WindowSizeSubclassId,
            UIntPtr.Zero);
    }

    private void ResizeToInitialSize()
    {
        Windows.Graphics.SizeInt32 minimum = GetMinimumSize();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Max(DefaultWidth, minimum.Width),
            Math.Max(DefaultHeight, minimum.Height)));
    }

    private Windows.Graphics.SizeInt32 GetMinimumSize()
    {
        WindowTrackSizeBounds bounds = GetWindowTrackSizeBounds();
        return new Windows.Graphics.SizeInt32(bounds.MinimumWidth, bounds.MinimumHeight);
    }

    private Windows.Graphics.SizeInt32 GetMaximumSize()
    {
        WindowTrackSizeBounds bounds = GetWindowTrackSizeBounds();
        return new Windows.Graphics.SizeInt32(bounds.MaximumWidth, bounds.MaximumHeight);
    }

    private WindowTrackSizeBounds GetWindowTrackSizeBounds()
    {
        uint dpi = _windowHandle == IntPtr.Zero ? 96 : GetDpiForWindow(_windowHandle);
        return WindowSizeConstraints.GetTrackSizeBounds(
            MinimumWidthDip,
            MinimumHeightDip,
            MaximumWidthDip,
            MaximumHeightDip,
            dpi);
    }

    private IntPtr WindowSizeSubclassProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == SettingsNavigateMessage)
        {
            string? page = GetPageTag(wParam.ToInt32().ToString());
            DispatcherQueue.TryEnqueue(() =>
            {
                AppWindow.Show();
                Activate();
                if (page != null)
                {
                    NavigateTo(page);
                }
            });
            return IntPtr.Zero;
        }

        IntPtr result = DefSubclassProc(windowHandle, message, wParam, lParam);
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return result;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        WindowTrackSizeBounds bounds = GetWindowTrackSizeBounds();
        minMaxInfo.MinimumTrackSize = new NativePoint(bounds.MinimumWidth, bounds.MinimumHeight);
        minMaxInfo.MaximumTrackSize = new NativePoint(bounds.MaximumWidth, bounds.MaximumHeight);
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private delegate IntPtr SubclassProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    private void NavMenu_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void NavMenu_DisplayModeChanged(
        NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args)
    {
        if (args.DisplayMode is NavigationViewDisplayMode.Compact or NavigationViewDisplayMode.Minimal)
        {
            sender.IsPaneOpen = false;
        }
    }

    private void TitleDragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(TitleDragRegion).Properties.IsLeftButtonPressed) return;

        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (windowHandle == IntPtr.Zero) return;

        ReleaseCapture();
        SendMessage(windowHandle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Navigate(string tag)
    {
        ContentFrame.Content = tag switch
        {
            "Lyrics" => CreateLyricsPage(),
            "Typography" => CreateTypographyPage(),
            "Visual" => CreateVisualPage(),
            "TaskbarPerformance" => CreateTaskbarPerformancePage(),
            "WaterReminder" => CreateWaterReminderPage(),
            "QuickTranslate" => CreateQuickTranslatePage(),
            "Floating" => CreateFloatingPage(),
            "Applications" => CreateApplicationsPage(),
            "DesktopWidget" => CreateDesktopWidgetPage(),
            "GeneralSettings" or "About" or "General" or "AboutTab" => CreateGeneralSettingsPage(),
            _ => null
        };
    }

    private Page CreateLyricsPage()
    {
        return new LyricsSettingsPage(
            CreateTypographyPage(),
            CreateVisualPage(),
            CreateFloatingPage(),
            CreateDesktopWidgetPage(),
            CreateApplicationsPage(),
            _lastLyricsTab,
            tag => _lastLyricsTab = tag);
    }

    private Page CreateTypographyPage()
    {
        var panel = NewPanel();
        panel.AddRow(SectionHeader("字体与排版"));
        panel.AddRow(CreateSettingsCard(
            LabeledFontPicker("字体", _settings.FontFamily, value => _settings.FontFamily = value),
            LabeledNumberBox("字体大小", _settings.FontSize, 8, 48, value => _settings.FontSize = value),
            LabeledComboBox("字体粗细", FontWeightOptions, ToChineseFontWeight(_settings.FontWeight), value => _settings.FontWeight = ToFontWeight(value)),
            LabeledNumberBox("下一句字号差", _settings.NextLyricFontSizeDiff, 0, 12, value => _settings.NextLyricFontSizeDiff = value),
            LabeledComboBox("下一句字体粗细", FontWeightOptions, ToChineseFontWeight(_settings.NextLyricFontWeight), value => _settings.NextLyricFontWeight = ToFontWeight(value)),
            LabeledToggle("显示双行歌词", _settings.IsDoubleLine, value => _settings.IsDoubleLine = value),
            LabeledNumberBox("双行歌词间距", _settings.DoubleLineGap, 0, 11, value => _settings.DoubleLineGap = value, step: 1)));

        panel.AddRow(SectionHeader("任务栏位置与对齐"));
        panel.AddRow(CreateSettingsCard(
            LabeledNumberBox("任务栏歌词宽度", _settings.Width, 180, 1600, value => _settings.Width = value),
            LabeledDisplaySelector(_settings.TaskbarMonitorDeviceName, value => _settings.TaskbarMonitorDeviceName = value),
            LabeledNumberBox("任务栏右侧偏移", _settings.OffsetX, 0, 200, value =>
            {
                _settings.OffsetX = (int)value;
                _changedTaskbarLyricOffset = true;
            }),
            LabeledNumberBox("歌词时间偏移（秒）", _settings.LyricOffsetSeconds, -10, 10, value => _settings.LyricOffsetSeconds = value, step: 0.5)));

        panel.AddRow(SectionHeader("交互与控制"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("鼠标悬停时显示媒体控制栏", _settings.EnableTaskbarHoverMediaControls, value => _settings.EnableTaskbarHoverMediaControls = value)));

        return Wrap(panel);
    }

    private static DisplayOption[] GetDisplayOptions()
    {
        try
        {
            var monitors = new List<(string DeviceName, int Width, int Height, bool IsPrimary)>();
            MonitorEnumProc collectMonitor = (monitor, _, _, _) =>
            {
                var monitorInfo = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    monitors.Add((
                        monitorInfo.DeviceName,
                        monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
                        monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
                        (monitorInfo.Flags & MonitorInfofPrimary) != 0));
                }

                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collectMonitor, IntPtr.Zero);

            DisplayOption[] options = monitors
                .OrderByDescending(monitor => monitor.IsPrimary)
                .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select((monitor, index) => new DisplayOption(
                    monitor.DeviceName,
                    $"显示器 {index + 1}{(monitor.IsPrimary ? "（主显示器）" : "")} — {monitor.Width} × {monitor.Height}"))
                .ToArray();
            return options.Length > 0 ? options : [new DisplayOption("", "主显示器")];
        }
        catch
        {
            return [new DisplayOption("", "主显示器")];
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProc monitorEnumProc,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private Page CreateVisualPage()
    {
        var panel = NewPanel();
        panel.AddRow(SectionHeader("颜色与背景"));
        panel.AddRow(CreateSettingsCard(
            LabeledColorPicker("歌词颜色", _settings.TextColor, value => _settings.TextColor = value),
            LabeledColorPicker("高亮颜色", _settings.ActiveTextColor, value => _settings.ActiveTextColor = value),
            LabeledColorPicker("任务栏背景", _settings.BackgroundColor, value => _settings.BackgroundColor = value)));

        panel.AddRow(SectionHeader("文字特效"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("启用文字阴影", _settings.EnableShadow, value => _settings.EnableShadow = value),
            LabeledToggle("启用文字描边", _settings.EnableOutline, value => _settings.EnableOutline = value)));

        return Wrap(panel);
    }

    private Page CreateTaskbarPerformancePage()
    {
        var panel = NewPanel();
        var selectedMetrics = TaskbarPerformanceMetricCatalog
            .Normalize(_settings.TaskbarPerformanceMetrics)
            .ToList();
        var summaryMetrics = TaskbarPerformanceMetricCatalog
            .GetSummarySelection(
                selectedMetrics,
                _settings.TaskbarPerformanceSummaryMetrics,
                _settings.TaskbarPerformanceSummaryMetricCount)
            .ToList();
        _settings.TaskbarPerformanceSummaryMetrics = new List<string>(summaryMetrics);
        var metricOrder = selectedMetrics
            .Concat(TaskbarPerformanceMetricCatalog.Definitions
                .Select(definition => definition.Id)
                .Where(id => !selectedMetrics.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        ListView? orderEditor = null;
        const double MetricToggleColumnWidth = 56;

        string[] refreshOptions = ["1 秒", "2 秒", "5 秒"];
        string selectedRefresh = _settings.TaskbarPerformanceRefreshSeconds switch
        {
            2 => "2 秒",
            5 => "5 秒",
            _ => "1 秒"
        };

        var resetPosition = new Button { Content = "恢复默认位置", HorizontalAlignment = HorizontalAlignment.Left };
        resetPosition.Click += (_, _) =>
        {
            _settings.TaskbarPerformanceOffsetX = null;
            _resetTaskbarPerformancePosition = true;
            QueueSettingsApply();
        };

        panel.AddRow(SectionHeader("监控与数据源"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle(
                "启用任务栏性能监控",
                _settings.EnableTaskbarPerformanceMonitor,
                value => _settings.EnableTaskbarPerformanceMonitor = value),
            LabeledDisplaySelector(_settings.TaskbarPerformanceMonitorDeviceName,
                value => _settings.TaskbarPerformanceMonitorDeviceName = value),
            LabeledNumberBox(
                "任务栏摘要数量",
                _settings.TaskbarPerformanceSummaryMetricCount,
                1,
                TaskbarPerformanceMetricCatalog.Definitions.Count,
                value =>
                {
                    _settings.TaskbarPerformanceSummaryMetricCount = (int)value;
                    NormalizeSummaryMetrics();
                    RefreshMetricOrderEditor();
                }),
            LabeledToggle(
                "增强温度读取（管理员权限）",
                _settings.EnableEnhancedTemperatureSensors,
                value => _settings.EnableEnhancedTemperatureSensors = value,
                "AMD 锐龙 CPU 温度依赖内核驱动（PawnIO）才能读取。开启后将以管理员权限运行辅助进程；若仍无温度，请安装 LibreHardwareMonitor 官方程序附带的 PawnIO 驱动。"),
            LabeledComboBox(
                "刷新频率",
                refreshOptions,
                selectedRefresh,
                value => _settings.TaskbarPerformanceRefreshSeconds = value switch
                {
                    "2 秒" => 2,
                    "5 秒" => 5,
                    _ => 1
                })));

        panel.AddRow(SectionHeader("字体与显示"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle(
                "显示双行指标",
                _settings.TaskbarPerformanceIsDoubleLine,
                value => _settings.TaskbarPerformanceIsDoubleLine = value),
            LabeledFontPicker(
                "字体",
                _settings.TaskbarPerformanceFontFamily,
                value => _settings.TaskbarPerformanceFontFamily = value),
            LabeledNumberBox(
                "字体大小",
                _settings.TaskbarPerformanceFontSize,
                8,
                24,
                value => _settings.TaskbarPerformanceFontSize = value),
            LabeledComboBox(
                "字体粗细",
                FontWeightOptions,
                ToChineseFontWeight(_settings.TaskbarPerformanceFontWeight),
                value => _settings.TaskbarPerformanceFontWeight = ToFontWeight(value))));

        orderEditor = new ListView
        {
            CanDragItems = true,
            CanReorderItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollMode(orderEditor, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(orderEditor, ScrollBarVisibility.Disabled);

        void AddMetricColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MetricToggleColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MetricToggleColumnWidth) });
        }

        ListViewItem CreateMetricColumnHeader()
        {
            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddMetricColumns(header);

            var displayHeader = new TextBlock
            {
                Text = "显示",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.72
            };
            var summaryHeader = new TextBlock
            {
                Text = "摘要",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.72
            };
            Grid.SetColumn(displayHeader, 2);
            Grid.SetColumn(summaryHeader, 4);
            header.Children.Add(displayHeader);
            header.Children.Add(summaryHeader);

            return new ListViewItem
            {
                Content = header,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 0, 10, 2),
                IsHitTestVisible = false,
                CanDrag = false,
                AllowDrop = false
            };
        }

        void SaveMetricOrder()
        {
            var enabledMetricIds = selectedMetrics
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selectedMetrics.Clear();
            selectedMetrics.AddRange(metricOrder
                .Where(enabledMetricIds.Contains)
                .Select(id => id));
            _settings.TaskbarPerformanceMetrics = TaskbarPerformanceMetricCatalog.Normalize(selectedMetrics);
            NormalizeSummaryMetrics();
            QueueSettingsApply();
        }

        bool IsMetricEnabled(string metricId) => selectedMetrics.Contains(metricId, StringComparer.OrdinalIgnoreCase);

        bool IsSummaryMetricEnabled(string metricId) =>
            summaryMetrics.Contains(metricId, StringComparer.OrdinalIgnoreCase);

        void NormalizeSummaryMetrics()
        {
            List<string> normalized = TaskbarPerformanceMetricCatalog.GetSummarySelection(
                selectedMetrics,
                summaryMetrics,
                _settings.TaskbarPerformanceSummaryMetricCount);
            summaryMetrics.Clear();
            summaryMetrics.AddRange(normalized);
            _settings.TaskbarPerformanceSummaryMetrics = new List<string>(summaryMetrics);
            QueueSettingsApply();
        }

        void ShowSummaryLimitWarning()
        {
            ErrorInfoBar.Severity = InfoBarSeverity.Warning;
            ErrorInfoBar.Message = $"摘要最多显示 {_settings.TaskbarPerformanceSummaryMetricCount} 项指标。";
            ErrorInfoBar.IsOpen = true;
            _successInfoBarTimer.Stop();
            _successInfoBarTimer.Start();
        }

        void RefreshMetricOrderEditor()
        {
            if (orderEditor == null) return;

            orderEditor.Items.Clear();
            orderEditor.Items.Add(CreateMetricColumnHeader());
            foreach (string metricId in metricOrder)
            {
                TaskbarPerformanceMetricDefinition definition = TaskbarPerformanceMetricCatalog.Definitions
                    .First(definition => string.Equals(definition.Id, metricId, StringComparison.OrdinalIgnoreCase));

                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                AddMetricColumns(row);
                var dragGrip = CreateMetricDragGrip();
                dragGrip.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(dragGrip, 0);
                row.Children.Add(dragGrip);
                var metricName = new TextBlock { Text = definition.DisplayName, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(metricName, 1);
                row.Children.Add(metricName);

                var displayToggle = new ToggleSwitch
                {
                    IsOn = IsMetricEnabled(metricId),
                    MinWidth = 0,
                    OnContent = null,
                    OffContent = null,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(displayToggle, "显示详细信息");
                displayToggle.Toggled += (_, _) =>
                {
                    if (displayToggle.IsOn)
                    {
                        if (!selectedMetrics.Contains(metricId, StringComparer.OrdinalIgnoreCase))
                        {
                            selectedMetrics.Add(metricId);
                        }
                    }
                    else
                    {
                        selectedMetrics.RemoveAll(id => string.Equals(id, metricId, StringComparison.OrdinalIgnoreCase));
                        summaryMetrics.RemoveAll(id => string.Equals(id, metricId, StringComparison.OrdinalIgnoreCase));
                    }
                    _settings.TaskbarPerformanceMetrics = TaskbarPerformanceMetricCatalog.Normalize(selectedMetrics);
                    NormalizeSummaryMetrics();
                    RefreshMetricOrderEditor();
                };
                Grid.SetColumn(displayToggle, 2);
                row.Children.Add(displayToggle);

                var summaryToggle = new ToggleSwitch
                {
                    IsOn = IsSummaryMetricEnabled(metricId),
                    IsEnabled = IsMetricEnabled(metricId),
                    MinWidth = 0,
                    OnContent = null,
                    OffContent = null,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(summaryToggle, "摘要显示");
                summaryToggle.Toggled += (_, _) =>
                {
                    if (!IsMetricEnabled(metricId))
                    {
                        if (summaryToggle.IsOn) summaryToggle.IsOn = false;
                        return;
                    }

                    if (summaryToggle.IsOn)
                    {
                        if (!IsSummaryMetricEnabled(metricId) &&
                            summaryMetrics.Count >= _settings.TaskbarPerformanceSummaryMetricCount)
                        {
                            summaryToggle.IsOn = false;
                            ShowSummaryLimitWarning();
                            return;
                        }

                        if (!IsSummaryMetricEnabled(metricId)) summaryMetrics.Add(metricId);
                    }
                    else
                    {
                        summaryMetrics.RemoveAll(id => string.Equals(id, metricId, StringComparison.OrdinalIgnoreCase));
                    }

                    NormalizeSummaryMetrics();
                    RefreshMetricOrderEditor();
                };
                Grid.SetColumn(summaryToggle, 4);
                row.Children.Add(summaryToggle);

                orderEditor.Items.Add(new ListViewItem
                {
                    Tag = definition.Id,
                    Content = row,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 10, 7)
                });
            }
        }

        orderEditor.DragItemsCompleted += (_, _) =>
        {
            var reordered = orderEditor.Items
                .OfType<ListViewItem>()
                .Select(item => item.Tag as string)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList();
            if (reordered.Count != metricOrder.Count) return;
            metricOrder.Clear();
            metricOrder.AddRange(reordered);
            SaveMetricOrder();
            RefreshMetricOrderEditor();
        };

        RefreshMetricOrderEditor();
        panel.AddRow(SectionHeader("指标显示与顺序"));
        panel.AddRow(CreateSettingsCard(orderEditor));

        panel.AddRow(SectionHeader("位置重置"));
        panel.AddRow(CreateSettingsCard(resetPosition));

        return Wrap(panel);
    }

    private QuickTranslateWindowMaterial? _appliedWindowMaterial;

    private void ApplyWindowMaterial()
    {
        QuickTranslateWindowMaterial material = QuickTranslateWindowMaterialParser.Parse(
            _settings.SettingsWindowMaterial);

        // MicaBackdrop relies on the Windows 11 DWM backdrop pipeline; on Windows 10 it
        // produces no background at all, so the window shows the desktop through it.
        if (material == QuickTranslateWindowMaterial.Mica &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            material = QuickTranslateWindowMaterial.Acrylic;
        }

        // Reassigning SystemBackdrop tears down and recreates the compositor surface, so
        // doing it on every save makes the window background flicker. Only rebuild the
        // backdrop when the effective material actually changes.
        if (_appliedWindowMaterial == material) return;

        SystemBackdrop = material switch
        {
            QuickTranslateWindowMaterial.Acrylic => new DesktopAcrylicBackdrop(),
            QuickTranslateWindowMaterial.Solid => null,
            _ => new MicaBackdrop()
        };

        _appliedWindowMaterial = material;
    }

    private Page CreateWaterReminderPage()
    {
        var panel = NewPanel();

        var resetPosition = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new SymbolIcon(Symbol.Refresh)
        };
        ToolTipService.SetToolTip(resetPosition, "恢复默认组件位置");
        resetPosition.Click += (_, _) =>
        {
            _settings.TaskbarWaterReminderOffsetX = null;
            _resetTaskbarWaterReminderPosition = true;
            QueueSettingsApply();
        };

        var displayEditor = new Grid { ColumnSpacing = 8 };
        displayEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        displayEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var displaySelector = LabeledDisplaySelector(
            _settings.TaskbarWaterReminderMonitorDeviceName,
            value => _settings.TaskbarWaterReminderMonitorDeviceName = value);
        Grid.SetColumn(displaySelector, 0);
        Grid.SetColumn(resetPosition, 1);
        displayEditor.Children.Add(displaySelector);
        displayEditor.Children.Add(resetPosition);

        panel.AddRow(SectionHeader("任务栏组件"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle(
                "启用喝水助手",
                _settings.EnableWaterReminder,
                value => _settings.EnableWaterReminder = value),
            displayEditor));

        panel.AddRow(SectionHeader("提醒节奏"));
        panel.AddRow(CreateSettingsCard(
            LabeledNumberBox(
                "提醒间隔（分钟）",
                _settings.WaterReminderIntervalMinutes,
                15,
                240,
                value => _settings.WaterReminderIntervalMinutes = (int)value),
            LabeledNumberBox(
                "稍后提醒（分钟）",
                _settings.WaterReminderSnoozeMinutes,
                5,
                60,
                value => _settings.WaterReminderSnoozeMinutes = (int)value),
            LabeledNumberBox(
                "每日目标（次）",
                _settings.WaterReminderDailyGoal,
                1,
                24,
                value => _settings.WaterReminderDailyGoal = (int)value)));

        panel.AddRow(SectionHeader("静默时段与打卡"));
        panel.AddRow(CreateSettingsCard(
            LabeledTimePicker(
                "开始时间",
                _settings.WaterReminderQuietStart,
                value => _settings.WaterReminderQuietStart = value),
            LabeledTimePicker(
                "结束时间",
                _settings.WaterReminderQuietEnd,
                value => _settings.WaterReminderQuietEnd = value),
            LabeledToggle(
                "显示系统通知",
                _settings.WaterReminderShowSystemNotification,
                value => _settings.WaterReminderShowSystemNotification = value),
            LabeledToggle(
                "全屏时隐藏提醒",
                _settings.WaterReminderHideInFullscreen,
                value => _settings.WaterReminderHideInFullscreen = value),
            CreateHotkeyRow("打卡快捷键", _settings.WaterReminderDrinkHotkey, "Alt+Shift+W", value => _settings.WaterReminderDrinkHotkey = value, "按下快捷键快速记录一次饮水打卡。")));

        panel.AddRow(SectionHeader("饮水统计"));
        _waterReminderStatisticsHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = CreateWaterReminderStatistics()
        };
        panel.AddRow(CreateSettingsCard(_waterReminderStatisticsHost));
        return Wrap(panel);
    }

    private FrameworkElement CreateWaterReminderStatistics()
    {
        DateTime now = DateTime.Now;
        _settings.WaterReminderDrinkHistory = WaterReminderHistory.Normalize(
            _settings.WaterReminderDrinkHistory,
            now);
        IReadOnlyList<WaterReminderDailyCount> dailyCounts = WaterReminderHistory.GetDailyCounts(
            _settings.WaterReminderDrinkHistory,
            now,
            7);
        IReadOnlyList<DateTime> todayEntries = _settings.WaterReminderDrinkHistory
            .Where(timestamp => timestamp.Date == now.Date)
            .OrderBy(timestamp => timestamp)
            .ToList();
        DateTime? latestEntry = _settings.WaterReminderDrinkHistory.Count == 0
            ? null
            : _settings.WaterReminderDrinkHistory[^1];

        var root = new StackPanel { Spacing = 12 };
        var summary = new Grid { ColumnSpacing = 8 };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddWaterReminderStatistic(summary, 0, "今日饮水", $"{_settings.WaterReminderCompletedToday} / {_settings.WaterReminderDailyGoal} 次");
        AddWaterReminderStatistic(summary, 1, "近七日", $"{dailyCounts.Sum(item => item.Count)} 次");
        AddWaterReminderStatistic(summary, 2, "最近一次", latestEntry?.ToString("HH:mm") ?? "暂无记录");
        root.Children.Add(summary);

        var trend = new Canvas
        {
            Height = 118,
            MinHeight = 118,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        trend.SizeChanged += (_, _) => RenderWaterReminderTrend(trend, dailyCounts);
        root.Children.Add(trend);
        RenderWaterReminderTrend(trend, dailyCounts);

        var times = new StackPanel { Spacing = 6 };
        times.Children.Add(new TextBlock { Text = "今日记录", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        if (todayEntries.Count == 0)
        {
            times.Children.Add(new TextBlock { Text = "今天还没有饮水记录。", Opacity = 0.68 });
        }
        else
        {
            var timeList = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            foreach (DateTime entry in todayEntries)
            {
                var recordItem = new Border
                {
                    Padding = new Thickness(8, 3, 6, 3),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 127, 127, 127))
                };
                var recordContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                recordContent.Children.Add(new TextBlock
                {
                    Text = entry.ToString("HH:mm"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var removeButton = new Button
                {
                    Width = 16,
                    Height = 16,
                    Padding = new Thickness(0),
                    Background = null,
                    BorderBrush = null,
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon
                    {
                        Glyph = "\uE74D",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 12
                    }
                };
                ToolTipService.SetToolTip(removeButton, "删除此记录");
                removeButton.Click += async (_, _) => await RemoveWaterReminderRecordAsync(entry, removeButton);
                recordContent.Children.Add(removeButton);
                recordItem.Child = recordContent;
                timeList.Children.Add(recordItem);
            }
            times.Children.Add(new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = timeList
            });
        }
        root.Children.Add(times);
        return root;
    }

    private async Task RemoveWaterReminderRecordAsync(DateTime entry, FrameworkElement source)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = source.XamlRoot,
            Title = "删除饮水记录？",
            Content = $"将删除 {entry:HH:mm} 的饮水记录，并同步今日进度。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            SettingsDocument currentSettings = SettingsDocument.Load(_settingsPath);
            if (!WaterReminderHistory.Remove(currentSettings.WaterReminderDrinkHistory, entry)) return;

            DateTime now = DateTime.Now;
            if (entry.Date == now.Date && currentSettings.WaterReminderCompletedToday > 0)
            {
                currentSettings.WaterReminderCompletedToday--;
            }
            if (currentSettings.WaterReminderLastCompletedAt == entry)
            {
                DateTime? latestToday = currentSettings.WaterReminderDrinkHistory
                    .Where(timestamp => timestamp.Date == now.Date)
                    .Select(timestamp => (DateTime?)timestamp)
                    .LastOrDefault();
                currentSettings.WaterReminderLastCompletedAt = latestToday ?? now;
            }

            currentSettings.Save(_settingsPath);
            _settings.WaterReminderDrinkHistory = currentSettings.WaterReminderDrinkHistory;
            _settings.WaterReminderRecordDate = currentSettings.WaterReminderRecordDate;
            _settings.WaterReminderCompletedToday = currentSettings.WaterReminderCompletedToday;
            _settings.WaterReminderLastCompletedAt = currentSettings.WaterReminderLastCompletedAt;
            _settings.WaterReminderSnoozedUntil = currentSettings.WaterReminderSnoozedUntil;
            NotifySettingsApplied();
            if (_waterReminderStatisticsHost is not null)
            {
                _waterReminderStatisticsHost.Content = CreateWaterReminderStatistics();
            }
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = "删除饮水记录失败: " + exception.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }

    private static void AddWaterReminderStatistic(Grid host, int column, string label, string value)
    {
        var tile = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 127, 127, 127))
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.68 });
        content.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold });
        tile.Child = content;
        Grid.SetColumn(tile, column);
        host.Children.Add(tile);
    }

    private static void RenderWaterReminderTrend(
        Canvas chart,
        IReadOnlyList<WaterReminderDailyCount> dailyCounts)
    {
        if (chart.ActualWidth <= 1 || dailyCounts.Count == 0) return;

        chart.Children.Clear();
        const double leftPadding = 10;
        const double rightPadding = 10;
        const double topPadding = 10;
        const double bottomPadding = 25;
        double plotWidth = Math.Max(1, chart.ActualWidth - leftPadding - rightPadding);
        double plotHeight = Math.Max(1, chart.Height - topPadding - bottomPadding);
        int maximum = Math.Max(1, dailyCounts.Max(item => item.Count));
        var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 127, 127, 127));
        var accentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 53, 127, 216));

        for (int step = 0; step < 3; step++)
        {
            double y = topPadding + plotHeight * step / 2d;
            chart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = leftPadding,
                X2 = leftPadding + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }

        var line = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = accentBrush,
            StrokeThickness = 2,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round
        };
        double denominator = Math.Max(1, dailyCounts.Count - 1);
        for (int index = 0; index < dailyCounts.Count; index++)
        {
            WaterReminderDailyCount item = dailyCounts[index];
            double x = leftPadding + plotWidth * index / denominator;
            double y = topPadding + plotHeight * (1 - item.Count / (double)maximum);
            line.Points.Add(new Windows.Foundation.Point(x, y));

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = accentBrush
            };
            ToolTipService.SetToolTip(dot, $"{item.Date:M/d}  {item.Count} 次");
            Canvas.SetLeft(dot, x - 3);
            Canvas.SetTop(dot, y - 3);
            chart.Children.Add(dot);

            var label = new TextBlock { Text = item.Date.ToString("M/d"), FontSize = 10, Opacity = 0.62 };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, topPadding + plotHeight + 6);
            chart.Children.Add(label);
        }
        chart.Children.Add(line);
    }

    private void ApplyApplicationTheme()
    {
        NavMenu.RequestedTheme = ApplicationThemeParser.Parse(_settings.ApplicationTheme) switch
        {
            ApplicationThemePreference.Light => ElementTheme.Light,
            ApplicationThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyWindowBorderTheme();
    }

    private void ApplyWindowBorderTheme()
    {
        if (_windowHandle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        int borderColor = ApplicationThemeParser.Resolve(_settings.ApplicationTheme) == ResolvedApplicationTheme.Dark
            ? DarkWindowBorderColor
            : LightWindowBorderColor;
        DwmSetWindowAttribute(_windowHandle, DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    internal bool UsesSystemApplicationTheme =>
        ApplicationThemeParser.Parse(_settings.ApplicationTheme) == ApplicationThemePreference.System;

    internal void RefreshSystemTheme()
    {
        if (!UsesSystemApplicationTheme) return;
        ApplyApplicationTheme();
    }

    private Page CreateQuickTranslatePage()
    {
        return new QuickTranslateSettingsPage(
            CreateQuickTranslateGeneralPage(),
            CreateQuickTranslateProvidersPage(),
            _lastQuickTranslateTab,
            tag => _lastQuickTranslateTab = tag);
    }

    private Page CreateQuickTranslateGeneralPage()
    {
        var panel = NewPanel();

        var resetButtonPosition = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new SymbolIcon(Symbol.Refresh)
        };
        ToolTipService.SetToolTip(resetButtonPosition, "恢复默认按钮位置");
        resetButtonPosition.Click += (_, _) =>
        {
            _settings.TaskbarTranslateButtonOffsetX = null;
            _resetTaskbarTranslateButtonPosition = true;
            QueueSettingsApply();
        };
        var displayEditor = new Grid { ColumnSpacing = 8 };
        displayEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        displayEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var displaySelector = LabeledDisplaySelector(_settings.TaskbarTranslateButtonMonitorDeviceName,
            value => _settings.TaskbarTranslateButtonMonitorDeviceName = value);
        Grid.SetColumn(displaySelector, 0);
        Grid.SetColumn(resetButtonPosition, 1);
        displayEditor.Children.Add(displaySelector);
        displayEditor.Children.Add(resetButtonPosition);

        panel.AddRow(SectionHeader("任务栏入口"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle(
                "显示任务栏翻译按钮",
                _settings.EnableTaskbarTranslateButton,
                value => _settings.EnableTaskbarTranslateButton = value),
            displayEditor));

        string selectedMaterial = QuickTranslateWindowMaterialParser.Parse(_settings.QuickTranslateWindowMaterial) switch
        {
            QuickTranslateWindowMaterial.Acrylic => "Acrylic",
            QuickTranslateWindowMaterial.Solid => "纯色",
            _ => "Mica"
        };

        panel.AddRow(SectionHeader("交互与外观"));
        panel.AddRow(CreateSettingsCard(
            CreateHotkeyRow("全局快捷键", _settings.QuickTranslateHotkey, "Alt+Shift+T", value => _settings.QuickTranslateHotkey = value, "点击输入框后按下组合键；移除后不注册全局快捷键。"),
            LabeledFontPicker(
                "翻译窗口字体",
                _settings.QuickTranslateFontFamily,
                value => _settings.QuickTranslateFontFamily = value),
            LabeledComboBoxRow("窗口材质", ["Mica", "Acrylic", "纯色"], selectedMaterial, value =>
            {
                _settings.QuickTranslateWindowMaterial = value switch
                {
                    "Acrylic" => "Acrylic",
                    "纯色" => "Solid",
                    _ => "Mica"
                };
            }),
            LabeledToggle(
                "AI 生成音标",
                _settings.EnableQuickTranslateAiPhonetic,
                value => _settings.EnableQuickTranslateAiPhonetic = value)));

        return Wrap(panel);
    }

    private Page CreateQuickTranslateProvidersPage()
    {
        var panel = NewPanel();

        var providerLayout = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 16
        };
        providerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        providerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left Column: Master list card
        var leftCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 14)
        };

        var leftGrid = new Grid();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var providerHeader = new Grid
        {
            Padding = new Thickness(12, 8, 8, 8)
        };
        providerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        providerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        providerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var providerHeaderTitle = new TextBlock
        {
            Text = "服务商列表",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(providerHeaderTitle, 0);
        providerHeader.Children.Add(providerHeaderTitle);

        var addProviderButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Content = new SymbolIcon(Symbol.Add)
        };
        ToolTipService.SetToolTip(addProviderButton, "添加翻译服务商");

        var removeProviderButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Content = new SymbolIcon(Symbol.Delete)
        };
        ToolTipService.SetToolTip(removeProviderButton, "移除当前选中的翻译服务商");

        Grid.SetColumn(addProviderButton, 1);
        Grid.SetColumn(removeProviderButton, 2);
        providerHeader.Children.Add(addProviderButton);
        providerHeader.Children.Add(removeProviderButton);
        leftGrid.Children.Add(providerHeader);

        var leftDivider = new Border
        {
            Height = 1,
            Background = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Opacity = 0.5
        };
        Grid.SetRow(leftDivider, 1);
        leftGrid.Children.Add(leftDivider);

        var providerList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            MaxHeight = 480
        };
        Grid.SetRow(providerList, 2);
        leftGrid.Children.Add(providerList);
        leftCard.Child = leftGrid;

        Grid.SetColumn(leftCard, 0);
        providerLayout.Children.Add(leftCard);

        // Right Column: Detail Cards
        var rightStack = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var emptyStateCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            Padding = new Thickness(24, 40, 24, 40),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 14),
            Visibility = Visibility.Collapsed
        };
        emptyStateCard.Child = new TextBlock
        {
            Text = "请在左侧选择或添加翻译服务商",
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        rightStack.Children.Add(emptyStateCard);

        var detailPanel = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Header Summary Card
        var topSummaryGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        topSummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topSummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleInfoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var detailTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var detailTitle = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var providerTypeBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        var providerTypeBadgeText = new TextBlock
        {
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            FontWeight = FontWeights.Medium
        };
        providerTypeBadge.Child = providerTypeBadgeText;
        detailTitleRow.Children.Add(detailTitle);
        detailTitleRow.Children.Add(providerTypeBadge);

        var detailEndpoint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleInfoStack.Children.Add(detailTitleRow);
        titleInfoStack.Children.Add(detailEndpoint);
        Grid.SetColumn(titleInfoStack, 0);
        topSummaryGrid.Children.Add(titleInfoStack);

        var setDefaultButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(setDefaultButton, 1);
        topSummaryGrid.Children.Add(setDefaultButton);

        var headerCard = CreateSettingsCard(topSummaryGrid);
        detailPanel.Children.Add(headerCard);

        // Input controls
        var providerIdBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var providerNameBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var appIdBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var cloudSecretBox = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var aiApiKeyBox = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var extraCredentialBox = new AutoSuggestBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var systemPromptBox = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 100,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = TranslationProviderProfiles.DefaultAiSystemPrompt
        };
        var fetchModelsButton = new Button
        {
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Content = new SymbolIcon(Symbol.Refresh)
        };
        ToolTipService.SetToolTip(fetchModelsButton, "获取可用模型列表");
        var apiBaseUrlBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "https://" };

        var extraCredentialEditor = new Grid { ColumnSpacing = 8 };
        extraCredentialEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        extraCredentialEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(extraCredentialBox, 0);
        Grid.SetColumn(fetchModelsButton, 1);
        extraCredentialEditor.Children.Add(extraCredentialBox);
        extraCredentialEditor.Children.Add(fetchModelsButton);

        // Card 1: 基本信息
        detailPanel.Children.Add(SectionHeader("基本信息"));
        detailPanel.Children.Add(CreateSettingsCard(
            Field("服务商名称", providerNameBox, "显示在快捷翻译窗口服务商下拉列表中。"),
            Field("服务商 ID", providerIdBox, "在翻译窗口中标识该配置；只能使用字母、数字、下划线和短横线。"),
            Field("API 接口地址 (Base URL)", apiBaseUrlBox, "请求接口基础地址。支持自建网关或兼容第三方代理服务。")
        ));

        // Card 2: 凭证与模型
        var credentialsSectionHeader = SectionHeader("接口凭证与模型");
        var cloudCredentialsCard = CreateSettingsCard(
            Field("应用 ID (App ID)", appIdBox, "服务商控制台分配的应用 ID 或开发者 ID。"),
            Field("应用密钥 (App Secret)", cloudSecretBox, "服务商控制台分配的应用密钥。")
        );
        var aiCredentialsCard = CreateSettingsCard(
            Field("API 密钥 (API Key)", aiApiKeyBox, "模型服务商提供的 API Token 或 Key。"),
            Field("模型名称 (Model)", extraCredentialEditor, "例如 deepseek-ai/DeepSeek-V3；支持点击右侧刷新按钮自动获取可用模型列表。")
        );

        detailPanel.Children.Add(credentialsSectionHeader);
        detailPanel.Children.Add(cloudCredentialsCard);
        detailPanel.Children.Add(aiCredentialsCard);

        // Card 3: 系统提示词 (Prompt - AI Only)
        var promptSectionHeader = SectionHeader("系统提示词 (Prompt)");
        var promptCard = CreateSettingsCard(
            Field("系统提示词", systemPromptBox, "留空时使用默认翻译提示词；支持变量：{target_language} 代表目标语言，{domain} 代表快捷翻译中选择的领域。")
        );
        detailPanel.Children.Add(promptSectionHeader);
        detailPanel.Children.Add(promptCard);

        rightStack.Children.Add(detailPanel);
        Grid.SetColumn(rightStack, 1);
        providerLayout.Children.Add(rightStack);

        var modelCandidates = new List<string>();
        bool synchronizing = false;

        TranslationProviderProfile? SelectedProfile() => (providerList.SelectedItem as ListViewItem)?.Tag as TranslationProviderProfile;

        string GetProviderDisplayName(TranslationProviderProfile profile) =>
            !string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.DisplayName :
            !string.IsNullOrWhiteSpace(profile.Id) ? profile.Id :
            "自定义服务商";

        string GetProviderEndpoint(TranslationProviderProfile profile) =>
            string.IsNullOrWhiteSpace(profile.ApiBaseUrl) ? "未配置接口地址" : profile.ApiBaseUrl;

        void UpdateModelSuggestions(string? query)
        {
            TranslationProviderProfile? profile = SelectedProfile();
            if (profile == null || !TranslationService.IsAiProvider(profile.Provider))
            {
                extraCredentialBox.ItemsSource = null;
                extraCredentialBox.IsSuggestionListOpen = false;
                return;
            }

            string[] suggestions = modelCandidates
                .Where(model => model.Contains(query?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .Take(50)
                .ToArray();
            extraCredentialBox.ItemsSource = suggestions;
            extraCredentialBox.IsSuggestionListOpen = suggestions.Length > 0;
        }

        FrameworkElement CreateProviderListEntry(TranslationProviderProfile profile)
        {
            var entry = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(2, 4, 2, 4)
            };
            entry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            entry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = GetProviderDisplayName(profile),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 0);
            topRow.Children.Add(nameBlock);

            bool isDefault = string.Equals(profile.Id, _settings.SelectedTranslationProviderId, StringComparison.OrdinalIgnoreCase);
            if (isDefault)
            {
                var defaultBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush,
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                defaultBadge.Child = new TextBlock
                {
                    Text = "默认",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                };
                Grid.SetColumn(defaultBadge, 1);
                topRow.Children.Add(defaultBadge);
            }
            Grid.SetRow(topRow, 0);
            entry.Children.Add(topRow);

            var bottomText = new TextBlock
            {
                Text = TranslationService.IsAiProvider(profile.Provider) ? "AI 大模型" : (profile.Provider ?? "云翻译"),
                FontSize = 11.5,
                Opacity = 0.65,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(bottomText, 1);
            entry.Children.Add(bottomText);

            return entry;
        }

        void UpdateSelectedProfile()
        {
            if (synchronizing) return;
            TranslationProviderProfile? profile = SelectedProfile();
            if (profile == null) return;

            bool isAi = TranslationService.IsAiProvider(profile.Provider);
            profile.Id = providerIdBox.Text.Trim();
            profile.DisplayName = providerNameBox.Text.Trim();
            profile.AppId = appIdBox.Text.Trim();
            profile.AppSecret = isAi ? aiApiKeyBox.Password : cloudSecretBox.Password;
            profile.ExtraCredential = extraCredentialBox.Text.Trim();
            profile.SystemPrompt = systemPromptBox.Text.Trim();
            profile.ApiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrlBox.Text) ? string.Empty : apiBaseUrlBox.Text.Trim();
            detailTitle.Text = GetProviderDisplayName(profile);
            detailEndpoint.Text = GetProviderEndpoint(profile);

            if (providerList.SelectedItem is ListViewItem currentItem)
            {
                currentItem.Content = CreateProviderListEntry(profile);
            }

            QueueSettingsApply();
        }

        void PopulateEditor(TranslationProviderProfile? profile)
        {
            synchronizing = true;
            try
            {
                if (profile == null)
                {
                    detailPanel.Visibility = Visibility.Collapsed;
                    emptyStateCard.Visibility = Visibility.Visible;
                    return;
                }

                emptyStateCard.Visibility = Visibility.Collapsed;
                detailPanel.Visibility = Visibility.Visible;

                bool isDefault = string.Equals(profile.Id, _settings.SelectedTranslationProviderId, StringComparison.OrdinalIgnoreCase);
                detailTitle.Text = GetProviderDisplayName(profile);
                detailEndpoint.Text = GetProviderEndpoint(profile);

                bool isAi = TranslationService.IsAiProvider(profile.Provider);
                providerTypeBadgeText.Text = isAi ? "AI 大模型" : "云端翻译";
                
                setDefaultButton.IsEnabled = !isDefault;
                setDefaultButton.Content = new SymbolIcon(Symbol.Accept);
                ToolTipService.SetToolTip(setDefaultButton, isDefault ? "当前已是默认服务商" : "设为默认服务商");

                providerIdBox.Text = profile.Id;
                providerNameBox.Text = profile.DisplayName;
                appIdBox.Text = profile.AppId;
                cloudSecretBox.Password = profile.AppSecret ?? string.Empty;
                aiApiKeyBox.Password = profile.AppSecret ?? string.Empty;
                extraCredentialBox.Text = profile.ExtraCredential;
                systemPromptBox.Text = profile.SystemPrompt ?? string.Empty;
                apiBaseUrlBox.Text = profile.ApiBaseUrl ?? string.Empty;

                cloudCredentialsCard.Visibility = isAi ? Visibility.Collapsed : Visibility.Visible;
                aiCredentialsCard.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
                promptSectionHeader.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
                promptCard.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;

                modelCandidates.Clear();
                UpdateModelSuggestions(extraCredentialBox.Text);
            }
            finally
            {
                synchronizing = false;
            }
        }

        void RefreshProviderList(TranslationProviderProfile? preferredSelection = null)
        {
            providerList.Items.Clear();
            foreach (TranslationProviderProfile profile in _settings.TranslationProviders)
            {
                providerList.Items.Add(new ListViewItem
                {
                    Content = CreateProviderListEntry(profile),
                    Tag = profile,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }

            preferredSelection ??= _settings.TranslationProviders.FirstOrDefault();
            ListViewItem? selectedItem = providerList.Items
                .OfType<ListViewItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Tag, preferredSelection))
                ?? providerList.Items.OfType<ListViewItem>().FirstOrDefault();

            providerList.SelectedItem = selectedItem;
            PopulateEditor((selectedItem?.Tag as TranslationProviderProfile) ?? preferredSelection);
            removeProviderButton.IsEnabled = _settings.TranslationProviders.Count > 1;
        }

        providerList.SelectionChanged += (_, _) =>
        {
            TranslationProviderProfile? profile = SelectedProfile();
            PopulateEditor(profile);
        };

        setDefaultButton.Click += (_, _) =>
        {
            TranslationProviderProfile? profile = SelectedProfile();
            if (profile == null) return;
            _settings.SelectedTranslationProviderId = profile.Id;
            RefreshProviderList(profile);
            QueueSettingsApply();
        };

        providerIdBox.TextChanged += (_, _) => UpdateSelectedProfile();
        providerNameBox.TextChanged += (_, _) => UpdateSelectedProfile();
        appIdBox.TextChanged += (_, _) => UpdateSelectedProfile();
        cloudSecretBox.PasswordChanged += (_, _) => UpdateSelectedProfile();
        aiApiKeyBox.PasswordChanged += (_, _) => UpdateSelectedProfile();
        extraCredentialBox.TextChanged += (sender, args) =>
        {
            if (sender is not AutoSuggestBox currentBox) return;
            try
            {
                UpdateSelectedProfile();
                if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                {
                    UpdateModelSuggestions(currentBox.Text);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        };
        extraCredentialBox.SuggestionChosen += (sender, args) =>
        {
            if (sender is not AutoSuggestBox currentBox) return;
            try
            {
                currentBox.Text = args.SelectedItem as string ?? string.Empty;
                UpdateSelectedProfile();
            }
            catch (ObjectDisposedException)
            {
            }
        };
        extraCredentialBox.GotFocus += (_, _) => UpdateModelSuggestions(extraCredentialBox.Text);
        extraCredentialBox.LostFocus += (_, _) => extraCredentialBox.IsSuggestionListOpen = false;
        fetchModelsButton.Click += async (_, _) =>
        {
            TranslationProviderProfile? profile = SelectedProfile();
            if (profile == null || !TranslationService.IsAiProvider(profile.Provider)) return;

            fetchModelsButton.IsEnabled = false;
            try
            {
                IReadOnlyList<string> models = await TranslationService.GetAvailableModelsAsync(
                    new TranslationConfiguration(
                        profile.Id,
                        profile.Provider,
                        profile.AppId,
                        profile.AppSecret,
                        profile.ApiBaseUrl,
                        profile.ExtraCredential),
                    CancellationToken.None);
                modelCandidates.Clear();
                modelCandidates.AddRange(models);
                UpdateModelSuggestions(extraCredentialBox.Text);
                if (models.Count == 0)
                {
                    ErrorInfoBar.Severity = InfoBarSeverity.Warning;
                    ErrorInfoBar.Message = "该服务商未返回可用模型。";
                    ErrorInfoBar.IsOpen = true;
                }
            }
            catch (Exception exception)
            {
                ErrorInfoBar.Severity = InfoBarSeverity.Error;
                ErrorInfoBar.Message = "获取模型失败: " + exception.Message;
                ErrorInfoBar.IsOpen = true;
            }
            finally
            {
                fetchModelsButton.IsEnabled = true;
            }
        };

        void AddProvider(string? provider)
        {
            TranslationProviderProfile profile = TranslationProviderProfiles.CreateNew(_settings.TranslationProviders, provider);
            _settings.TranslationProviders.Add(profile);
            _settings.SelectedTranslationProviderId = profile.Id;
            RefreshProviderList(profile);
            QueueSettingsApply();
        }

        var providerMenu = new MenuFlyout();
        MenuFlyoutItem CreateProviderMenuItem(string label, string? provider)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => AddProvider(provider);
            return item;
        }

        providerMenu.Items.Add(CreateProviderMenuItem("百度翻译", "Baidu"));
        providerMenu.Items.Add(CreateProviderMenuItem("有道智云", "Youdao"));
        providerMenu.Items.Add(new MenuFlyoutSeparator());
        providerMenu.Items.Add(CreateProviderMenuItem("Google Cloud Translation", "Google"));
        providerMenu.Items.Add(CreateProviderMenuItem("DeepL", "DeepL"));
        providerMenu.Items.Add(CreateProviderMenuItem("Microsoft Azure Translator", "Azure"));
        providerMenu.Items.Add(new MenuFlyoutSeparator());
        providerMenu.Items.Add(CreateProviderMenuItem("腾讯云机器翻译", "Tencent"));
        providerMenu.Items.Add(CreateProviderMenuItem("阿里云机器翻译", "Alibaba"));
        providerMenu.Items.Add(CreateProviderMenuItem("火山翻译", "Volcengine"));
        providerMenu.Items.Add(CreateProviderMenuItem("华为云机器翻译", "Huawei"));
        providerMenu.Items.Add(CreateProviderMenuItem("讯飞翻译", "iFlytek"));
        providerMenu.Items.Add(new MenuFlyoutSeparator());
        providerMenu.Items.Add(CreateProviderMenuItem("OpenAI", "OpenAI"));
        providerMenu.Items.Add(CreateProviderMenuItem("DeepSeek", "DeepSeek"));
        providerMenu.Items.Add(CreateProviderMenuItem("通义千问", "Qwen"));
        providerMenu.Items.Add(CreateProviderMenuItem("硅基流动", "SiliconFlow"));
        providerMenu.Items.Add(CreateProviderMenuItem("OpenAI 兼容 AI", "OpenAICompatible"));
        providerMenu.Items.Add(CreateProviderMenuItem("Ollama 本地模型", "Ollama"));
        providerMenu.Items.Add(new MenuFlyoutSeparator());
        providerMenu.Items.Add(CreateProviderMenuItem("自定义服务商", null));
        addProviderButton.Click += (_, _) => providerMenu.ShowAt(addProviderButton);

        removeProviderButton.Click += async (_, _) =>
        {
            TranslationProviderProfile? profile = SelectedProfile();
            if (profile == null || _settings.TranslationProviders.Count <= 1) return;

            var confirmation = new ContentDialog
            {
                XamlRoot = removeProviderButton.XamlRoot,
                Title = "删除服务商？",
                Content = $"将移除“{GetProviderDisplayName(profile)}”及其全部配置。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary ||
                !_settings.TranslationProviders.Contains(profile)) return;

            _settings.TranslationProviders.Remove(profile);
            _settings.SelectedTranslationProviderId = TranslationProviderProfiles.ResolveSelectedId(
                _settings.TranslationProviders,
                _settings.SelectedTranslationProviderId);
            RefreshProviderList(null);
            QueueSettingsApply();
        };

        TranslationProviderProfile? initiallySelected = _settings.TranslationProviders.FirstOrDefault(profile =>
            string.Equals(profile.Id, _settings.SelectedTranslationProviderId, StringComparison.OrdinalIgnoreCase));
        RefreshProviderList(initiallySelected);

        panel.AddRow(providerLayout);
        return Wrap(panel);
    }

    private FrameworkElement CreateHotkeyRow(string label, string currentValue, string defaultHotkey, Action<string> onUpdate, string? description = null)
    {
        var row = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement titleElement;
        var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (string.IsNullOrWhiteSpace(description))
        {
            titleElement = title;
        }
        else
        {
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(title);
            titleRow.Children.Add(InformationButton(description));
            titleElement = titleRow;
        }
        Grid.SetColumn(titleElement, 0);
        row.Children.Add(titleElement);

        var hotkeyBox = new TextBox
        {
            Text = NormalizeHotkey(currentValue),
            PlaceholderText = "点击按下快捷键",
            IsReadOnly = true,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        hotkeyBox.KeyDown += (_, args) => CaptureHotkey(hotkeyBox, args, onUpdate);

        var resetHotkeyButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new SymbolIcon(Symbol.Refresh)
        };
        if (!string.IsNullOrWhiteSpace(defaultHotkey))
        {
            ToolTipService.SetToolTip(resetHotkeyButton, $"重置为 {defaultHotkey}");
            resetHotkeyButton.Click += (_, _) =>
            {
                hotkeyBox.Text = NormalizeHotkey(defaultHotkey);
                onUpdate(hotkeyBox.Text);
                QueueSettingsApply();
            };
        }
        else
        {
            resetHotkeyButton.Visibility = Visibility.Collapsed;
        }

        var clearHotkeyButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new SymbolIcon(Symbol.Clear)
        };
        ToolTipService.SetToolTip(clearHotkeyButton, "移除快捷键");
        clearHotkeyButton.Click += (_, _) =>
        {
            hotkeyBox.Text = string.Empty;
            onUpdate(string.Empty);
            QueueSettingsApply();
        };

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        controls.Children.Add(hotkeyBox);
        if (!string.IsNullOrWhiteSpace(defaultHotkey))
        {
            controls.Children.Add(resetHotkeyButton);
        }
        controls.Children.Add(clearHotkeyButton);

        Grid.SetColumn(controls, 2);
        row.Children.Add(controls);

        return row;
    }

    private FrameworkElement CreateHotkeyField(string label, string currentValue, string defaultHotkey, Action<string> onUpdate, string? description = null) =>
        CreateHotkeyRow(label, currentValue, defaultHotkey, onUpdate, description);

    private void CaptureHotkey(TextBox hotkeyBox, KeyRoutedEventArgs args, Action<string> onUpdate)
    {
        uint modifiers = 0;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= QuickTranslateHotkey.Control;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= QuickTranslateHotkey.Alt;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= QuickTranslateHotkey.Shift;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= QuickTranslateHotkey.Win;

        if (QuickTranslateHotkey.TryCreate(modifiers, (uint)args.Key, out QuickTranslateHotkey hotkey))
        {
            string display = hotkey.ToDisplayString();
            hotkeyBox.Text = display;
            onUpdate(display);
            QueueSettingsApply();
        }
        args.Handled = true;
    }

    private static string NormalizeHotkey(string? value) =>
        QuickTranslateHotkey.TryParse(value, out QuickTranslateHotkey hotkey)
            ? hotkey.ToDisplayString()
            : string.Empty;

    private void CaptureQuickTranslateHotkey(TextBox hotkeyBox, KeyRoutedEventArgs args) =>
        CaptureHotkey(hotkeyBox, args, value => _settings.QuickTranslateHotkey = value);

    private void SetQuickTranslateHotkey(TextBox hotkeyBox, string value)
    {
        hotkeyBox.Text = NormalizeHotkey(value);
        _settings.QuickTranslateHotkey = hotkeyBox.Text;
        QueueSettingsApply();
    }

    private static string NormalizeQuickTranslateHotkey(string? value) => NormalizeHotkey(value);

    private Page CreateFloatingPage()
    {
        var panel = NewPanel();
        panel.AddRow(SectionHeader("窗口与交互"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("启用悬浮歌词", _settings.EnableFloatingLyrics, value => _settings.EnableFloatingLyrics = value),
            CreateHotkeyRow("切换快捷键", _settings.FloatingLyricsHotkey, "Alt+Shift+F", value => _settings.FloatingLyricsHotkey = value, "快速显示或隐藏独立悬浮歌词窗口。"),
            LabeledToggle("锁定位置", _settings.FloatingLyricsLocked, value => _settings.FloatingLyricsLocked = value),
            LabeledToggle("鼠标穿透", _settings.FloatingLyricsClickThrough, value => _settings.FloatingLyricsClickThrough = value)));

        panel.AddRow(SectionHeader("外观与排版"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("亚克力背景", _settings.FloatingLyricsUseAcrylic, value => _settings.FloatingLyricsUseAcrylic = value),
            LabeledToggle("启用文字阴影", _settings.FloatingLyricsEnableShadow, value => _settings.FloatingLyricsEnableShadow = value),
            LabeledFontPicker("字体", _settings.FloatingLyricsFontFamily, value => _settings.FloatingLyricsFontFamily = value),
            LabeledNumberBox("字体大小", _settings.FloatingLyricsFontSize, 10, 72, value => _settings.FloatingLyricsFontSize = value),
            LabeledComboBox("字体粗细", FontWeightOptions, ToChineseFontWeight(_settings.FloatingLyricsFontWeight), value => _settings.FloatingLyricsFontWeight = ToFontWeight(value)),
            LabeledNumberBox("窗口宽度", _settings.FloatingLyricsWidth ?? 360, 180, 1600, value => _settings.FloatingLyricsWidth = value),
            LabeledColorPicker("文字颜色", _settings.FloatingLyricsTextColor, value => _settings.FloatingLyricsTextColor = value),
            LabeledColorPicker("背景颜色", _settings.FloatingLyricsBackgroundColor, value => _settings.FloatingLyricsBackgroundColor = value)));

        return Wrap(panel);
    }

    private Page CreateApplicationsPage()
    {
        var panel = NewPanel();
        var selectedAppIds = new HashSet<string>(_settings.IncludedAppIds, StringComparer.OrdinalIgnoreCase);
        var appIdBox = new TextBox
        {
            Text = string.Join(",", selectedAppIds),
            MinWidth = 360
        };
        appIdBox.TextChanged += (_, _) =>
        {
            selectedAppIds.Clear();
            foreach (string appId in appIdBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                selectedAppIds.Add(appId);
            }
            _settings.IncludedAppIds = selectedAppIds.ToList();
            QueueSettingsApply();
        };

        var detectedApps = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        var detectionStatus = new TextBlock
        {
            Opacity = 0.65,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var detectButton = new Button { Content = "获取正在播放的应用", HorizontalAlignment = HorizontalAlignment.Left };
        detectButton.Click += async (_, _) =>
        {
            detectButton.IsEnabled = false;
            detectionStatus.Text = "正在读取系统媒体会话…";
            detectionStatus.Visibility = Visibility.Visible;
            detectedApps.Children.Clear();
            detectedApps.Visibility = Visibility.Collapsed;

            try
            {
                var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                string[] appIds = sessionManager.GetSessions()
                    .Where(session => session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    .Select(session => session.SourceAppUserModelId)
                    .Where(appId => !string.IsNullOrWhiteSpace(appId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(appId => GetMediaApplicationDisplayName(appId), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();

                if (appIds.Length == 0)
                {
                    detectionStatus.Text = "未检测到正在播放媒体的应用。请先在播放器中开始播放，再重新获取。";
                    return;
                }

                detectionStatus.Text = $"检测到 {appIds.Length} 个正在播放的应用；勾选后会加入应用标识筛选。";
                detectedApps.Visibility = Visibility.Visible;
                foreach (string appId in appIds)
                {
                    var checkBox = new CheckBox
                    {
                        IsChecked = selectedAppIds.Contains(appId),
                        Content = new StackPanel
                        {
                            Spacing = 1,
                            Children =
                            {
                                new TextBlock { Text = GetMediaApplicationDisplayName(appId) },
                                new TextBlock { Text = appId, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                            }
                        }
                    };
                    checkBox.Checked += (_, _) => SetSelectedMediaApplication(selectedAppIds, appIdBox, appId, true);
                    checkBox.Unchecked += (_, _) => SetSelectedMediaApplication(selectedAppIds, appIdBox, appId, false);
                    detectedApps.Children.Add(checkBox);
                }
            }
            catch (Exception ex)
            {
                detectionStatus.Text = $"读取系统媒体会话失败：{ex.Message}";
            }
            finally
            {
                detectButton.IsEnabled = true;
            }
        };

        var detector = new StackPanel
        {
            Spacing = 8,
            Children = { detectButton, detectionStatus, detectedApps }
        };

        panel.AddRow(SectionHeader("显示条件"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("仅在指定播放器运行时显示", _settings.RunOnlyWithMusicApp, value => _settings.RunOnlyWithMusicApp = value),
            LabeledTextBox("播放器进程名", _settings.MusicAppProcessNames, value => _settings.MusicAppProcessNames = value, "仅在开启上方选项时生效；使用英文逗号分隔。")));

        panel.AddRow(SectionHeader("任务栏外观"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("任务栏图标居中", _settings.CenterTaskbarIcons, value => _settings.CenterTaskbarIcons = value),
            LabeledToggle("白色亚克力背景", _settings.ApplyTaskbarAcrylic, value => _settings.ApplyTaskbarAcrylic = value)));

        panel.AddRow(SectionHeader("媒体来源"));
        panel.AddRow(CreateSettingsCard(
            detector,
            Field("已选媒体应用", appIdBox, "可手动编辑应用标识；检测结果勾选后会自动写入。")));

        return Wrap(panel);
    }

    private Page CreateGeneralSettingsPage()
    {
        return new GeneralSettingsPage(
            CreateGeneralPage(),
            CreateAboutTabPage(),
            _lastGeneralTab,
            tag => _lastGeneralTab = tag);
    }

    private Page CreateAboutPage() => CreateGeneralSettingsPage();

    private Page CreateGeneralPage()
    {
        var panel = NewPanel();

        panel.AddRow(SectionHeader("常规行为"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("开机自启动", _settings.LaunchOnStartup, value => _settings.LaunchOnStartup = value, "登录 Windows 时自动在后台启动 TinyBar。"),
            LabeledToggle("启动时自动检查更新", _settings.AutoCheckUpdates, value => _settings.AutoCheckUpdates = value)));

        string settingsWindowMaterial = QuickTranslateWindowMaterialParser.Parse(_settings.SettingsWindowMaterial) switch
        {
            QuickTranslateWindowMaterial.Acrylic => "Acrylic",
            QuickTranslateWindowMaterial.Solid => "纯色",
            _ => "Mica"
        };
        panel.AddRow(SectionHeader("窗口外观"));
        panel.AddRow(CreateSettingsCard(
            LabeledComboBoxRow(
                "应用主题",
                ["跟随系统", "浅色", "深色"],
                ApplicationThemeParser.ToDisplayName(ApplicationThemeParser.Parse(_settings.ApplicationTheme)),
                value =>
                {
                    _settings.ApplicationTheme = value switch
                    {
                        "浅色" => "Light",
                        "深色" => "Dark",
                        _ => "System"
                    };
                    ApplyApplicationTheme();
                }),
            LabeledComboBoxRow("设置窗口材质", ["Mica", "Acrylic", "纯色"], settingsWindowMaterial, value =>
            {
                _settings.SettingsWindowMaterial = value switch
                {
                    "Acrylic" => "Acrylic",
                    "纯色" => "Solid",
                    _ => "Mica"
                };
            })));

        panel.AddRow(SectionHeader("全局快捷键"));
        panel.AddRow(CreateSettingsCard(
            CreateHotkeyRow("快捷翻译", _settings.QuickTranslateHotkey, "Alt+Shift+T", value => _settings.QuickTranslateHotkey = value, "快速呼出快捷翻译窗口。"),
            CreateHotkeyRow("悬浮歌词", _settings.FloatingLyricsHotkey, "Alt+Shift+F", value => _settings.FloatingLyricsHotkey = value, "快速显示或隐藏独立悬浮歌词窗口。"),
            CreateHotkeyRow("桌面歌词", _settings.DesktopWidgetHotkey, "Alt+Shift+D", value => _settings.DesktopWidgetHotkey = value, "快速显示或隐藏桌面媒体组件。"),
            CreateHotkeyRow("饮水打卡", _settings.WaterReminderDrinkHotkey, "Alt+Shift+W", value => _settings.WaterReminderDrinkHotkey = value, "按下快捷键快速记录一次饮水打卡。")));

        return Wrap(panel);
    }

    private Page CreateAboutTabPage()
    {
        var panel = NewPanel();

        var headerCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string iconPngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TinyBar.png");
        FrameworkElement logoElement;
        if (File.Exists(iconPngPath))
        {
            logoElement = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPngPath)),
                Width = 36,
                Height = 36,
                Stretch = Stretch.Uniform
            };
        }
        else
        {
            logoElement = new FontIcon
            {
                Glyph = "\uE8D6",
                FontSize = 20,
                Foreground = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
            };
        }

        var logoBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = logoElement
        };
        Grid.SetColumn(logoBorder, 0);
        headerGrid.Children.Add(logoBorder);

        var identityStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = "TinyBar",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        var pill = new Border
        {
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = UpdateService.CurrentVersionDisplay,
                FontSize = 11,
                FontWeight = FontWeights.Medium
            }
        };
        titleRow.Children.Add(pill);
        identityStack.Children.Add(titleRow);

        identityStack.Children.Add(new TextBlock
        {
            Text = "Windows 任务栏信息组件：集中管理歌词、性能监控、喝水助手与快捷翻译。",
            FontSize = 12,
            Opacity = 0.72
        });
        Grid.SetColumn(identityStack, 2);
        headerGrid.Children.Add(identityStack);

        headerCard.Child = headerGrid;
        panel.AddRow(headerCard);

        var versionRow = new Grid();
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var versionTextStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        versionTextStack.Children.Add(new TextBlock { Text = "当前版本", FontWeight = FontWeights.Medium });
        versionTextStack.Children.Add(new TextBlock
        {
            Text = ".NET 9.0 · WinUI 3 · Windows 10/11 x64",
            FontSize = 12,
            Opacity = 0.65
        });
        Grid.SetColumn(versionTextStack, 0);
        versionRow.Children.Add(versionTextStack);

        var versionBadge = new Border
        {
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = UpdateService.CurrentVersionDisplay,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
        Grid.SetColumn(versionBadge, 1);
        versionRow.Children.Add(versionBadge);

        var updateRow = new Grid();
        updateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        updateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        updateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var updateTextStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var updateTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        updateTitleRow.Children.Add(new TextBlock { Text = "软件更新", FontWeight = FontWeights.Medium });
        updateTitleRow.Children.Add(InformationButton("通过 GitHub Releases 检查并在软件内下载安装正式版本。"));
        updateTextStack.Children.Add(updateTitleRow);

        var updateStatus = new TextBlock
        {
            Text = "尚未检查更新。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap
        };
        updateTextStack.Children.Add(updateStatus);
        Grid.SetColumn(updateTextStack, 0);
        updateRow.Children.Add(updateTextStack);

        var installUpdateButton = new Button
        {
            Content = "下载并安装",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            Visibility = Visibility.Collapsed
        };
        installUpdateButton.Click += (_, _) =>
        {
            if (!TryRequestInAppUpdate())
            {
                updateStatus.Text = "无法连接 TinyBar 主进程，请从托盘菜单重新打开检查更新。";
                return;
            }

            installUpdateButton.IsEnabled = false;
            updateStatus.Text = "正在打开更新窗口…";
            Close();
        };

        var checkIcon = new FontIcon
        {
            Glyph = "\uE72C",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var checkProgressRing = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        var checkText = new TextBlock
        {
            Text = "检查更新",
            VerticalAlignment = VerticalAlignment.Center
        };

        var checkButtonStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        checkButtonStack.Children.Add(checkIcon);
        checkButtonStack.Children.Add(checkProgressRing);
        checkButtonStack.Children.Add(checkText);

        var checkButton = new Button
        {
            Content = checkButtonStack
        };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkIcon.Visibility = Visibility.Collapsed;
            checkProgressRing.IsActive = true;
            checkProgressRing.Visibility = Visibility.Visible;
            installUpdateButton.Visibility = Visibility.Collapsed;
            updateStatus.Text = "正在检查更新…";
            try
            {
                UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();
                if (!result.Success)
                {
                    updateStatus.Text = $"检查更新失败：{result.ErrorMessage ?? "未知错误。"}";
                    return;
                }

                if (result.NoReleasePublished)
                {
                    updateStatus.Text = "暂未找到可用的正式版本。";
                }
                else if (result.HasUpdate)
                {
                    updateStatus.Text = $"发现新版本：{result.ReleaseName}（{result.LatestVersionDisplay}）。";
                    if (result.Package != null && !string.IsNullOrWhiteSpace(_updateEventName))
                    {
                        installUpdateButton.IsEnabled = true;
                        installUpdateButton.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    updateStatus.Text = $"当前已是最新版本（{UpdateService.CurrentVersionDisplay}）。";
                }
            }
            catch (TaskCanceledException)
            {
                updateStatus.Text = "检查更新超时，请稍后重试。";
            }
            catch (Exception exception)
            {
                updateStatus.Text = $"检查更新失败：{exception.Message}";
            }
            finally
            {
                checkProgressRing.IsActive = false;
                checkProgressRing.Visibility = Visibility.Collapsed;
                checkIcon.Visibility = Visibility.Visible;
                checkButton.IsEnabled = true;
            }
        };

        var updateActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        updateActions.Children.Add(checkButton);
        updateActions.Children.Add(installUpdateButton);
        Grid.SetColumn(updateActions, 2);
        updateRow.Children.Add(updateActions);

        panel.AddRow(SectionHeader("版本与更新"));
        panel.AddRow(CreateSettingsCard(versionRow, updateRow));

        panel.AddRow(SectionHeader("相关来源与社区"));
        panel.AddRow(CreateSourceLinks());

        return Wrap(panel);
    }

    private static FrameworkElement CreateSettingsCard(params FrameworkElement[] items)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 14)
        };

        var stack = new StackPanel();
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0)
            {
                var divider = new Border
                {
                    Height = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                    Opacity = 0.5
                };
                stack.Children.Add(divider);
            }

            var rowWrapper = new Grid
            {
                Padding = new Thickness(16, 8, 16, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44
            };
            rowWrapper.Children.Add(items[i]);
            stack.Children.Add(rowWrapper);
        }

        card.Child = stack;
        return card;
    }

    private bool TryRequestInAppUpdate()
    {
        if (string.IsNullOrWhiteSpace(_updateEventName)) return false;

        try
        {
            using EventWaitHandle updateRequest = EventWaitHandle.OpenExisting(_updateEventName);
            return updateRequest.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FrameworkElement CreateSourceLinks()
    {
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var link1 = CreateLinkTile("项目源码", "GitHub 开源仓库与源码", "\uE943", () => OpenUrl(UpdateService.RepositoryUrl));
        var link2 = CreateLinkTile("版本发布", "查看更新日志与历史版本", "\uE8EC", () => OpenUrl(UpdateService.ReleasesUrl));
        var link3 = CreateLinkTile("问题反馈", "提交 Bug 报告与功能建议", "\uE73E", () => OpenUrl($"{UpdateService.RepositoryUrl}/issues"));
        var link4 = CreateLinkTile("配置目录", "打开本地数据与配置文件夹", "\uE838", OpenConfigFolder);

        Grid.SetRow(link1, 0);
        Grid.SetColumn(link1, 0);
        grid.Children.Add(link1);

        Grid.SetRow(link2, 0);
        Grid.SetColumn(link2, 1);
        grid.Children.Add(link2);

        Grid.SetRow(link3, 1);
        Grid.SetColumn(link3, 0);
        grid.Children.Add(link3);

        Grid.SetRow(link4, 1);
        Grid.SetColumn(link4, 1);
        grid.Children.Add(link4);

        return grid;
    }

    private static FrameworkElement CreateLinkTile(string title, string description, string glyph, Action onClick)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12),
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconContainer = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Opacity = 0.85 }
        };
        Grid.SetColumn(iconContainer, 0);
        contentGrid.Children.Add(iconContainer);

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textStack, 2);
        contentGrid.Children.Add(textStack);

        var arrow = new FontIcon
        {
            Glyph = "\uE8A7",
            FontSize = 12,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(arrow, 3);
        contentGrid.Children.Add(arrow);

        button.Content = contentGrid;
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void OpenConfigFolder()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyBar");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static TextBlock SectionHeader(string title)
    {
        return new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.85,
            Margin = new Thickness(2, 6, 0, 4)
        };
    }

    private FrameworkElement LabeledComboBoxRow(string label, IReadOnlyList<string> options, string initial, Action<string> update)
    {
        var box = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = options.Contains(initial) ? initial : options[0],
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.SelectionChanged += (_, _) =>
        {
            update(box.SelectedItem as string ?? options[0]);
            QueueSettingsApply();
        };

        var row = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(box, 2);
        row.Children.Add(title);
        row.Children.Add(box);
        return row;
    }

    private static Button InformationButton(string description)
    {
        var button = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Background = null,
            BorderBrush = null,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "ⓘ",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Flyout = new Flyout
            {
                Content = new TextBlock
                {
                    Text = description,
                    MaxWidth = 360,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        ToolTipService.SetToolTip(button, "说明");
        return button;
    }

    private static void SetSelectedMediaApplication(HashSet<string> selectedAppIds, TextBox appIdBox, string appId, bool isSelected)
    {
        if (isSelected) selectedAppIds.Add(appId);
        else selectedAppIds.Remove(appId);

        appIdBox.Text = string.Join(",", selectedAppIds.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
    }

    private static string GetMediaApplicationDisplayName(string appId)
    {
        int packageSeparator = appId.IndexOf('!');
        if (packageSeparator > 0) return appId[..packageSeparator];

        return appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? appId[..^4]
            : appId;
    }

    private Page CreateDesktopWidgetPage()
    {
        var panel = NewPanel();

        var reset = new Button { Content = "重置组件位置", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            _settings.DesktopWidgetLeft = 48;
            _settings.DesktopWidgetTop = 48;
            _settings.DesktopWidgetMonitorDeviceName = "";
            _settings.DesktopWidgetMonitorOffsetX = null;
            _settings.DesktopWidgetMonitorOffsetY = null;
            _resetDesktopWidgetPosition = true;
            QueueSettingsApply();
        };

        panel.AddRow(SectionHeader("桌面媒体组件"));
        panel.AddRow(CreateSettingsCard(
            LabeledToggle("启用桌面媒体组件", _settings.EnableDesktopWidget, value => _settings.EnableDesktopWidget = value),
            CreateHotkeyRow("切换快捷键", _settings.DesktopWidgetHotkey, "Alt+Shift+D", value => _settings.DesktopWidgetHotkey = value, "快速显示或隐藏桌面媒体组件。"),
            LabeledComboBox("主题", ["深色", "浅色"], _settings.DesktopWidgetTheme == 1 ? "浅色" : "深色", value => _settings.DesktopWidgetTheme = value == "浅色" ? 1 : 0),
            LabeledToggle("锁定组件位置", _settings.DesktopWidgetLocked, value => _settings.DesktopWidgetLocked = value)));

        panel.AddRow(SectionHeader("位置重置"));
        panel.AddRow(CreateSettingsCard(reset));

        return Wrap(panel);
    }

    private static FormPanel NewPanel(string? title = null, string? subtitle = null, double? titleFontSize = null)
    {
        var panel = new FormPanel();
        if (!string.IsNullOrWhiteSpace(title))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var titleBlock = new TextBlock
            {
                Text = title,
                Style = Application.Current.Resources["TitleTextBlockStyle"] as Style
            };
            if (titleFontSize is double fontSize) titleBlock.FontSize = fontSize;
            header.Children.Add(titleBlock);
            if (!string.IsNullOrWhiteSpace(subtitle)) header.Children.Add(InformationButton(subtitle));
            panel.AddRow(header);
        }
        return panel;
    }

    private static Page Wrap(FormPanel panel)
    {
        var viewer = new ScrollViewer
        {
            Padding = new Thickness(28, 16, 28, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
        return new Page { Content = viewer };
    }

    private sealed class FormPanel : Grid
    {
        public FormPanel()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        public void AddRow(FrameworkElement child)
        {
            int rowIndex = RowDefinitions.Count;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            child.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (rowIndex > 0) child.Margin = new Thickness(0, 8, 0, 0);

            Grid.SetRow(child, rowIndex);
            Children.Add(child);
        }
    }

    private FrameworkElement LabeledTextBox(string label, string initial, Action<string> update, string? description = null)
    {
        var box = new TextBox { Text = initial, MinWidth = 360 };
        box.TextChanged += (_, _) => Commit(box.Text);
        return Field(label, box, description);

        void Commit(string value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private FrameworkElement LabeledTimePicker(string label, string initial, Action<string> update)
    {
        TimeSpan initialTime = TimeSpan.TryParseExact(
            initial,
            "hh\\:mm",
            CultureInfo.InvariantCulture,
            out TimeSpan parsedTime)
            ? parsedTime
            : TimeSpan.Zero;
        var picker = new TimePicker
        {
            Time = initialTime,
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            MinWidth = 180
        };
        picker.TimeChanged += (_, args) => Commit(args.NewTime.ToString("hh\\:mm", CultureInfo.InvariantCulture));
        return Field(label, picker, null);

        void Commit(string value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private FrameworkElement LabeledColorPicker(string label, string initial, Action<string> update)
    {
        const double SpectrumWidth = 280;
        const double SpectrumHeight = 176;
        const double SelectorDiameter = 16;

        var box = new TextBox { Text = initial, MinWidth = 360 };
        var checkerboard = new Grid();
        checkerboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        checkerboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        checkerboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        checkerboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < 2; row++)
        {
            for (int column = 0; column < 2; column++)
            {
                var square = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(
                            255,
                            (row + column) % 2 == 0 ? (byte)255 : (byte)208,
                            (row + column) % 2 == 0 ? (byte)255 : (byte)208,
                            (row + column) % 2 == 0 ? (byte)255 : (byte)208))
                };
                Grid.SetRow(square, row);
                Grid.SetColumn(square, column);
                checkerboard.Children.Add(square);
            }
        }

        var swatch = new Border
        {
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = checkerboard
        };
        var colorLayer = new Border { CornerRadius = new CornerRadius(3) };
        Grid.SetRowSpan(colorLayer, 2);
        Grid.SetColumnSpan(colorLayer, 2);
        checkerboard.Children.Add(colorLayer);

        Windows.UI.Color initialColor = TryParseColor(initial, out Windows.UI.Color parsedInitialColor)
            ? parsedInitialColor
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        RgbToHsv(initialColor, out double hue, out double saturation, out double value);
        double alpha = initialColor.A;

        var spectrum = new Canvas
        {
            Width = SpectrumWidth,
            Height = SpectrumHeight,
            Background = CreateHueGradientBrush()
        };
        spectrum.Children.Add(new Border
        {
            Width = SpectrumWidth,
            Height = SpectrumHeight,
            Background = CreateSaturationGradientBrush(),
            IsHitTestVisible = false
        });
        var selectionIndicator = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = SelectorDiameter,
            Height = SelectorDiameter,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        spectrum.Children.Add(selectionIndicator);

        var valueSlider = new Slider { Minimum = 0, Maximum = 100, Width = SpectrumWidth, Value = value * 100 };
        var alphaSlider = new Slider { Minimum = 0, Maximum = 255, Width = SpectrumWidth, Value = alpha };
        var pickerPanel = new StackPanel { Width = SpectrumWidth, Spacing = 2 };
        pickerPanel.Children.Add(new Border
        {
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = spectrum
        });
        pickerPanel.Children.Add(new TextBlock { Text = "亮度", FontSize = 12, Opacity = 0.7 });
        pickerPanel.Children.Add(valueSlider);
        pickerPanel.Children.Add(new TextBlock { Text = "不透明度", FontSize = 12, Opacity = 0.7 });
        pickerPanel.Children.Add(alphaSlider);
        var pickerButton = new Button
        {
            Width = 48,
            Height = 36,
            Padding = new Thickness(2),
            Background = null,
            BorderBrush = null,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = swatch,
            Flyout = new Flyout { Content = pickerPanel }
        };
        var editor = new Grid { MinWidth = 416, ColumnSpacing = 8 };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0);
        Grid.SetColumn(pickerButton, 1);
        editor.Children.Add(box);
        editor.Children.Add(pickerButton);

        void UpdateSwatch(Windows.UI.Color color) =>
            colorLayer.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        void PositionSelectionIndicator()
        {
            double left = hue / 360d * (SpectrumWidth - SelectorDiameter);
            double top = (1 - saturation) * (SpectrumHeight - SelectorDiameter);
            Canvas.SetLeft(selectionIndicator, left);
            Canvas.SetTop(selectionIndicator, top);
            selectionIndicator.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                value > 0.65 && saturation < 0.3
                    ? Windows.UI.Color.FromArgb(255, 20, 20, 20)
                    : Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }

        bool synchronizing = false;
        void ApplyColor(Windows.UI.Color color, bool updateText)
        {
            RgbToHsv(color, out hue, out saturation, out value);
            alpha = color.A;
            synchronizing = true;
            valueSlider.Value = value * 100;
            alphaSlider.Value = alpha;
            synchronizing = false;
            UpdateSwatch(color);
            PositionSelectionIndicator();

            if (!updateText) return;

            string colorText = FormatColor(color);
            if (!string.Equals(box.Text, colorText, StringComparison.Ordinal)) box.Text = colorText;
            else Commit(colorText);
        }

        void CommitPickerColor() => ApplyColor(HsvToRgb(hue, saturation, value, (byte)Math.Round(alpha)), true);

        void UpdateSpectrumSelection(Windows.Foundation.Point position)
        {
            double left = Math.Clamp(position.X - SelectorDiameter / 2, 0, SpectrumWidth - SelectorDiameter);
            double top = Math.Clamp(position.Y - SelectorDiameter / 2, 0, SpectrumHeight - SelectorDiameter);
            hue = left / (SpectrumWidth - SelectorDiameter) * 360;
            saturation = 1 - top / (SpectrumHeight - SelectorDiameter);
            CommitPickerColor();
        }

        bool selectingSpectrum = false;
        spectrum.PointerPressed += (_, args) =>
        {
            selectingSpectrum = true;
            spectrum.CapturePointer(args.Pointer);
            UpdateSpectrumSelection(args.GetCurrentPoint(spectrum).Position);
        };
        spectrum.PointerMoved += (_, args) =>
        {
            if (selectingSpectrum) UpdateSpectrumSelection(args.GetCurrentPoint(spectrum).Position);
        };
        spectrum.PointerReleased += (_, args) =>
        {
            selectingSpectrum = false;
            spectrum.ReleasePointerCapture(args.Pointer);
        };
        spectrum.PointerCanceled += (_, args) =>
        {
            selectingSpectrum = false;
            spectrum.ReleasePointerCapture(args.Pointer);
        };

        box.TextChanged += (_, _) =>
        {
            Commit(box.Text);
            if (!TryParseColor(box.Text, out Windows.UI.Color color)) return;

            ApplyColor(color, false);
        };
        valueSlider.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            value = valueSlider.Value / 100;
            CommitPickerColor();
        };
        alphaSlider.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            alpha = alphaSlider.Value;
            CommitPickerColor();
        };

        ApplyColor(initialColor, false);

        return Field(label, editor, null);

        void Commit(string value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private FrameworkElement LabeledFontPicker(string label, string initial, Action<string> update)
    {
        var box = new AutoSuggestBox
        {
            Text = initial,
            PlaceholderText = "输入字体名称搜索",
            MinWidth = 360,
            MaxSuggestionListHeight = 320
        };

        void UpdateSuggestions(string query)
        {
            try
            {
                string[] suggestions = GetFontSuggestions(query);
                box.ItemsSource = suggestions;
                box.IsSuggestionListOpen = suggestions.Length > 0 && !string.IsNullOrWhiteSpace(query);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (sender is not AutoSuggestBox currentBox) return;

            try
            {
                string text = currentBox.Text;
                Commit(text);
                UpdateSuggestions(text);
            }
            catch (ObjectDisposedException)
            {
            }
        };
        box.SuggestionChosen += (sender, args) =>
        {
            if (sender is not AutoSuggestBox currentBox) return;
            try
            {
                string fontName = args.SelectedItem as string ?? currentBox.Text;
                currentBox.Text = fontName;
                Commit(fontName);
            }
            catch (ObjectDisposedException)
            {
            }
        };
        box.QuerySubmitted += (sender, args) =>
        {
            if (sender is not AutoSuggestBox currentBox) return;
            try
            {
                string fontName = args.ChosenSuggestion as string ?? currentBox.Text;
                currentBox.Text = fontName;
                Commit(fontName);
            }
            catch (ObjectDisposedException)
            {
            }
        };

        return Field(label, box, "输入以搜索本机已安装字体，也可直接输入字体名称。");

        void Commit(string value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private static string[] GetFontSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            using var fonts = new InstalledFontCollection();
            return fonts.Families
                .Select(font => font.Name)
                .Where(name => name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(40)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string ToChineseFontWeight(string fontWeight) => fontWeight switch
    {
        "Light" => "细体",
        "SemiBold" => "半粗",
        "Bold" => "粗体",
        _ => "常规"
    };

    private static string ToFontWeight(string fontWeight) => fontWeight switch
    {
        "细体" => "Light",
        "半粗" => "SemiBold",
        "粗体" => "Bold",
        _ => "Normal"
    };

    private FrameworkElement LabeledNumberBox(string label, double initial, double minimum, double maximum, Action<double> update, double step = 1)
    {
        // Inline keeps the spin buttons always visible. Compact mode shows them in a flyout
        // that does not reliably dismiss when clicking elsewhere, leaving it stuck open.
        var box = new NumberBox
        {
            Value = initial,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 180
        };
        box.ValueChanged += (_, _) => { if (!double.IsNaN(box.Value)) Commit(box.Value); };
        return Field(label, box, null);

        void Commit(double value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private FrameworkElement LabeledComboBox(string label, IReadOnlyList<string> options, string initial, Action<string> update)
    {
        var box = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(initial) ? initial : options[0], MinWidth = 180 };
        box.SelectionChanged += (_, _) => Commit(box.SelectedItem as string ?? options[0]);
        return Field(label, box, null);

        void Commit(string value)
        {
            update(value);
            QueueSettingsApply();
        }
    }

    private FrameworkElement LabeledToggle(string label, bool initial, Action<bool> update, string? description = null)
    {
        var row = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleSwitch
        {
            IsOn = initial,
            MinWidth = 0,
            OnContent = null,
            OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) =>
        {
            update(toggle.IsOn);
            QueueSettingsApply();
        };
        if (string.IsNullOrWhiteSpace(description))
        {
            row.Children.Add(title);
        }
        else
        {
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(title);
            titleRow.Children.Add(InformationButton(description));
            row.Children.Add(titleRow);
        }
        row.Children.Add(toggle);
        var host = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = row
        };
        return host;
    }

    private static FrameworkElement CreateMetricDragGrip()
    {
        var grip = new Grid
        {
            Width = 14,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        for (int row = 0; row < 3; row++)
        {
            grip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int column = 0; column < 2; column++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(150, 95, 99, 104)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(dot, row);
                Grid.SetColumn(dot, column);
                grip.Children.Add(dot);
            }
        }

        grip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grip;
    }

    private static FrameworkElement Field(string label, FrameworkElement control, string? description)
    {
        var panel = new StackPanel { Spacing = 4 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(description)) header.Children.Add(InformationButton(description));
        panel.Children.Add(header);
        panel.Children.Add(control);
        return panel;
    }

    private void QueueSettingsApply()
    {
        _applySettingsTimer.Stop();
        _applySettingsTimer.Start();
    }

    private bool ApplySettings(bool showSuccessMessage = true)
    {
        if (!IsValidColor(_settings.TextColor) || !IsValidColor(_settings.ActiveTextColor) || !IsValidColor(_settings.BackgroundColor) || !IsValidColor(_settings.FloatingLyricsTextColor) || !IsValidColor(_settings.FloatingLyricsBackgroundColor))
        {
            _successInfoBarTimer.Stop();
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = "颜色格式无效，请使用 #RRGGBB 或 #AARRGGBB。";
            ErrorInfoBar.IsOpen = true;
            return false;
        }

        if (!TranslationProviderProfiles.HasUniqueIds(_settings.TranslationProviders))
        {
            _successInfoBarTimer.Stop();
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = "服务商 ID 必须唯一，且只能使用字母、数字、- 和 _。";
            ErrorInfoBar.IsOpen = true;
            return false;
        }

        if (_settings.TranslationProviders.Any(profile =>
            !string.IsNullOrWhiteSpace(profile.Provider) &&
            !TranslationProviderProfiles.IsValidApiBaseUrl(profile.ApiBaseUrl)))
        {
            _successInfoBarTimer.Stop();
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = "API Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
            ErrorInfoBar.IsOpen = true;
            return false;
        }

        try
        {
            PreserveRuntimeComponentPositions();
            _settings.Save(_settingsPath);
            AutoStartupService.SetAutoStart(_settings.LaunchOnStartup);
            ApplyApplicationTheme();
            ApplyWindowMaterial();
            _changedTaskbarLyricOffset = false;
            _resetTaskbarPerformancePosition = false;
            _resetTaskbarTranslateButtonPosition = false;
            _resetTaskbarWaterReminderPosition = false;
            _resetDesktopWidgetPosition = false;
            NotifySettingsApplied();
            _didSave = true;
            if (showSuccessMessage)
            {
                ErrorInfoBar.Severity = InfoBarSeverity.Success;
                ErrorInfoBar.Message = "设置已应用。";
                ErrorInfoBar.IsOpen = true;
                _successInfoBarTimer.Stop();
                _successInfoBarTimer.Start();
            }
            return true;
        }
        catch (Exception exception)
        {
            _successInfoBarTimer.Stop();
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = "设置保存失败: " + exception.Message;
            ErrorInfoBar.IsOpen = true;
            return false;
        }
    }

    private FrameworkElement LabeledDisplaySelector(string selectedDeviceName, Action<string> update)
    {
        DisplayOption[] displayOptions = GetDisplayOptions();
        DisplayOption selectedDisplay = displayOptions.FirstOrDefault(option =>
            string.Equals(option.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? displayOptions[0];

        return LabeledComboBox(
            "显示屏",
            displayOptions.Select(option => option.Label).ToArray(),
            selectedDisplay.Label,
            label => update(displayOptions.First(option => option.Label == label).DeviceName));
    }

    private void PreserveRuntimeComponentPositions()
    {
        SettingsDocument currentSettings = SettingsDocument.Load(_settingsPath);
        if (!_changedTaskbarLyricOffset)
        {
            _settings.OffsetX = currentSettings.OffsetX;
        }

        if (!_resetTaskbarPerformancePosition)
        {
            _settings.TaskbarPerformanceOffsetX = currentSettings.TaskbarPerformanceOffsetX;
        }

        if (!_resetTaskbarTranslateButtonPosition)
        {
            _settings.TaskbarTranslateButtonOffsetX = currentSettings.TaskbarTranslateButtonOffsetX;
        }

        if (!_resetTaskbarWaterReminderPosition)
        {
            _settings.TaskbarWaterReminderOffsetX = currentSettings.TaskbarWaterReminderOffsetX;
        }

        if (!_resetDesktopWidgetPosition)
        {
            _settings.DesktopWidgetLeft = currentSettings.DesktopWidgetLeft;
            _settings.DesktopWidgetTop = currentSettings.DesktopWidgetTop;
            _settings.DesktopWidgetMonitorDeviceName = currentSettings.DesktopWidgetMonitorDeviceName;
            _settings.DesktopWidgetMonitorOffsetX = currentSettings.DesktopWidgetMonitorOffsetX;
            _settings.DesktopWidgetMonitorOffsetY = currentSettings.DesktopWidgetMonitorOffsetY;
        }

        _settings.FloatingLyricsLeft = currentSettings.FloatingLyricsLeft;
        _settings.FloatingLyricsTop = currentSettings.FloatingLyricsTop;

        _settings.WaterReminderDrinkHistory = currentSettings.WaterReminderDrinkHistory;
        _settings.WaterReminderRecordDate = currentSettings.WaterReminderRecordDate;
        _settings.WaterReminderCompletedToday = currentSettings.WaterReminderCompletedToday;
        _settings.WaterReminderLastCompletedAt = currentSettings.WaterReminderLastCompletedAt;
        _settings.WaterReminderSnoozedUntil = currentSettings.WaterReminderSnoozedUntil;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_windowSizeSubclassInstalled)
        {
            RemoveWindowSubclass(_windowHandle, _windowSizeSubclassProc, WindowSizeSubclassId);
            _windowSizeSubclassInstalled = false;
        }

        Environment.ExitCode = _didSave ? 0 : 1;
    }

    private static bool IsValidColor(string value) => TryParseColor(value, out _);

    private static bool TryParseColor(string value, out Windows.UI.Color color)
    {
        string hex = value.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            color = Windows.UI.Color.FromArgb(
                255,
                (byte)(rgb >> 16),
                (byte)(rgb >> 8),
                (byte)rgb);
            return true;
        }

        if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            color = Windows.UI.Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
            return true;
        }

        color = default;
        return false;
    }

    private static string FormatColor(Windows.UI.Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Microsoft.UI.Xaml.Media.LinearGradientBrush CreateHueGradientBrush()
    {
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(255, 255, 0, 0) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 1d / 6, Color = Windows.UI.Color.FromArgb(255, 255, 255, 0) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 2d / 6, Color = Windows.UI.Color.FromArgb(255, 0, 255, 0) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 3d / 6, Color = Windows.UI.Color.FromArgb(255, 0, 255, 255) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 4d / 6, Color = Windows.UI.Color.FromArgb(255, 0, 0, 255) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 5d / 6, Color = Windows.UI.Color.FromArgb(255, 255, 0, 255) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(255, 255, 0, 0) });
        return brush;
    }

    private static Microsoft.UI.Xaml.Media.LinearGradientBrush CreateSaturationGradientBrush()
    {
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0, 255, 255, 255) });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(255, 255, 255, 255) });
        return brush;
    }

    private static void RgbToHsv(Windows.UI.Color color, out double hue, out double saturation, out double value)
    {
        double red = color.R / 255d;
        double green = color.G / 255d;
        double blue = color.B / 255d;
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double delta = maximum - minimum;

        value = maximum;
        saturation = maximum == 0 ? 0 : delta / maximum;
        if (delta == 0)
        {
            hue = 0;
            return;
        }

        hue = maximum == red
            ? 60 * ((green - blue) / delta % 6)
            : maximum == green
                ? 60 * ((blue - red) / delta + 2)
                : 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
    }

    private static Windows.UI.Color HsvToRgb(double hue, double saturation, double value, byte alpha)
    {
        double chroma = value * saturation;
        double hueSegment = hue / 60;
        double secondary = chroma * (1 - Math.Abs(hueSegment % 2 - 1));
        double match = value - chroma;
        (double red, double green, double blue) = hueSegment switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };

        return Windows.UI.Color.FromArgb(
            alpha,
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }

    private static string ResolveSettingsPath()
    {
        string? argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(path => !path.StartsWith("-", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(argument)
            ? SettingsStorage.CurrentPath
            : Path.GetFullPath(argument);
    }

    private static string ResolveInitialPage()
    {
        string? argument = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(value => value.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        return GetPageTag(argument?[7..]) ?? "Typography";
    }

    private static string? GetPageTag(string? page) => page switch
    {
        "0" => "Typography",
        "1" => "Visual",
        "2" => "Floating",
        "3" => "Applications",
        "4" => "DesktopWidget",
        "5" or "GeneralSettings" or "About" => "GeneralSettings",
        "6" => "QuickTranslate",
        "7" => "TaskbarPerformance",
        "8" => "WaterReminder",
        _ => null
    };

    private static void NotifySettingsApplied()
    {
        string? eventName = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(value => value.StartsWith("--apply-event=", StringComparison.OrdinalIgnoreCase))?[14..];
        if (!string.IsNullOrWhiteSpace(eventName))
        {
            SignalSettingsAppliedEvent(eventName);
            return;
        }

        SignalSettingsAppliedEvent(SharedSettingsAppliedEventName);
    }

    private static void SignalSettingsAppliedEvent(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;

        try
        {
            using var settingsApplied = EventWaitHandle.OpenExisting(eventName);
            settingsApplied.Set();
        }
        catch
        {
            // The main window may already have exited.
        }
    }

    private sealed class LyricsSettingsPage : Page
    {
        private static readonly (string Tag, string Label)[] TabDefinitions =
        [
            ("Typography", "布局与显示"),
            ("Visual", "其他效果"),
            ("Floating", "悬浮歌词"),
            ("DesktopWidget", "桌面歌词"),
            ("Applications", "应用筛选")
        ];

        private readonly IReadOnlyDictionary<string, Page> _pagesByTag;
        private readonly Grid _contentHost;
        private readonly SelectorBar _selectorBar;
        private readonly Action<string> _onTabChanged;

        public LyricsSettingsPage(
            Page typography,
            Page visual,
            Page floating,
            Page desktopWidget,
            Page applications,
            string initialTab,
            Action<string> onTabChanged)
        {
            _onTabChanged = onTabChanged;
            _pagesByTag = new Dictionary<string, Page>(StringComparer.Ordinal)
            {
                ["Typography"] = typography,
                ["Visual"] = visual,
                ["Floating"] = floating,
                ["DesktopWidget"] = desktopWidget,
                ["Applications"] = applications
            };

            _selectorBar = new SelectorBar
            {
                Margin = new Thickness(20, 2, 20, 0),
                Padding = new Thickness(0, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach ((string tag, string label) in TabDefinitions)
            {
                _selectorBar.Items.Add(new SelectorBarItem
                {
                    Text = label,
                    Tag = tag,
                    MinHeight = 28,
                    Padding = new Thickness(10, 2, 10, 2)
                });
            }
            _selectorBar.SelectionChanged += SelectorBar_SelectionChanged;

            _contentHost = new Grid();
            foreach (Page page in _pagesByTag.Values)
            {
                page.Visibility = Visibility.Collapsed;
                _contentHost.Children.Add(page);
            }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(_selectorBar);
            root.Children.Add(_contentHost);
            Grid.SetRow(_selectorBar, 0);
            Grid.SetRow(_contentHost, 1);
            Content = root;

            SelectTab(initialTab);
        }

        public void SelectTab(string subTag)
        {
            if (_selectorBar.Items.OfType<SelectorBarItem>()
                .FirstOrDefault(item => item.Tag as string == subTag) is SelectorBarItem target)
            {
                _selectorBar.SelectedItem = target;
            }

            UpdateVisibility();
        }

        private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            string selectedTag = (sender.SelectedItem as SelectorBarItem)?.Tag as string ?? "General";
            _onTabChanged(selectedTag);
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            string? selected = (_selectorBar.SelectedItem as SelectorBarItem)?.Tag as string;
            foreach ((string tag, Page page) in _pagesByTag)
            {
                page.Visibility = string.Equals(tag, selected, StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private sealed class GeneralSettingsPage : Page
    {
        private static readonly (string Tag, string Label)[] TabDefinitions =
        [
            ("General", "通用设置"),
            ("AboutTab", "关于")
        ];

        private readonly IReadOnlyDictionary<string, Page> _pagesByTag;
        private readonly Grid _contentHost;
        private readonly SelectorBar _selectorBar;
        private readonly Action<string> _onTabChanged;

        public GeneralSettingsPage(
            Page general,
            Page about,
            string initialTab,
            Action<string> onTabChanged)
        {
            _onTabChanged = onTabChanged;
            _pagesByTag = new Dictionary<string, Page>(StringComparer.Ordinal)
            {
                ["General"] = general,
                ["AboutTab"] = about
            };

            _selectorBar = new SelectorBar
            {
                Margin = new Thickness(20, 2, 20, 0),
                Padding = new Thickness(0, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach ((string tag, string label) in TabDefinitions)
            {
                _selectorBar.Items.Add(new SelectorBarItem
                {
                    Text = label,
                    Tag = tag,
                    MinHeight = 28,
                    Padding = new Thickness(10, 2, 10, 2)
                });
            }
            _selectorBar.SelectionChanged += SelectorBar_SelectionChanged;

            _contentHost = new Grid();
            foreach (Page page in _pagesByTag.Values)
            {
                page.Visibility = Visibility.Collapsed;
                _contentHost.Children.Add(page);
            }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(_selectorBar);
            root.Children.Add(_contentHost);
            Grid.SetRow(_selectorBar, 0);
            Grid.SetRow(_contentHost, 1);
            Content = root;

            SelectTab(initialTab);
        }

        public void SelectTab(string subTag)
        {
            if (_selectorBar.Items.OfType<SelectorBarItem>()
                .FirstOrDefault(item => item.Tag as string == subTag) is SelectorBarItem target)
            {
                _selectorBar.SelectedItem = target;
            }

            UpdateVisibility();
        }

        private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            string selectedTag = (sender.SelectedItem as SelectorBarItem)?.Tag as string ?? "General";
            _onTabChanged(selectedTag);
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            string? selected = (_selectorBar.SelectedItem as SelectorBarItem)?.Tag as string;
            foreach ((string tag, Page page) in _pagesByTag)
            {
                page.Visibility = string.Equals(tag, selected, StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private sealed class QuickTranslateSettingsPage : Page
    {
        private static readonly (string Tag, string Label)[] TabDefinitions =
        [
            ("General", "通用设置"),
            ("Providers", "服务商配置")
        ];

        private readonly IReadOnlyDictionary<string, Page> _pagesByTag;
        private readonly Grid _contentHost;
        private readonly SelectorBar _selectorBar;
        private readonly Action<string> _onTabChanged;

        public QuickTranslateSettingsPage(
            Page general,
            Page providers,
            string initialTab,
            Action<string> onTabChanged)
        {
            _onTabChanged = onTabChanged;
            _pagesByTag = new Dictionary<string, Page>(StringComparer.Ordinal)
            {
                ["General"] = general,
                ["Providers"] = providers
            };

            _selectorBar = new SelectorBar
            {
                Margin = new Thickness(20, 2, 20, 0),
                Padding = new Thickness(0, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach ((string tag, string label) in TabDefinitions)
            {
                _selectorBar.Items.Add(new SelectorBarItem
                {
                    Text = label,
                    Tag = tag,
                    MinHeight = 28,
                    Padding = new Thickness(10, 2, 10, 2)
                });
            }
            _selectorBar.SelectionChanged += SelectorBar_SelectionChanged;

            _contentHost = new Grid();
            foreach (Page page in _pagesByTag.Values)
            {
                page.Visibility = Visibility.Collapsed;
                _contentHost.Children.Add(page);
            }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(_selectorBar);
            root.Children.Add(_contentHost);
            Grid.SetRow(_selectorBar, 0);
            Grid.SetRow(_contentHost, 1);
            Content = root;

            SelectTab(initialTab);
        }

        public void SelectTab(string subTag)
        {
            if (_selectorBar.Items.OfType<SelectorBarItem>()
                .FirstOrDefault(item => item.Tag as string == subTag) is SelectorBarItem target)
            {
                _selectorBar.SelectedItem = target;
            }

            UpdateVisibility();
        }

        private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            string selectedTag = (sender.SelectedItem as SelectorBarItem)?.Tag as string ?? "General";
            _onTabChanged(selectedTag);
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            string? selected = (_selectorBar.SelectedItem as SelectorBarItem)?.Tag as string;
            foreach ((string tag, Page page) in _pagesByTag)
            {
                page.Visibility = string.Equals(tag, selected, StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}

public sealed class SettingsDocument
{
    public double Width { get; set; } = 400;
    public string TranslationProvider { get; set; } = "Baidu";
    public string BaiduTranslationAppId { get; set; } = "";
    public string BaiduTranslationAppSecret { get; set; } = "";
    public string YoudaoTranslationAppKey { get; set; } = "";
    public string YoudaoTranslationAppSecret { get; set; } = "";
    public List<TranslationProviderProfile> TranslationProviders { get; set; } = [];
    public string SelectedTranslationProviderId { get; set; } = "";
    public List<string> QuickTranslateDomains { get; set; } = [TranslationDomainCatalog.General];
    public string SelectedQuickTranslateDomain { get; set; } = TranslationDomainCatalog.General;
    public string QuickTranslateTargetLanguage { get; set; } = QuickTranslateTargetLanguages.Default;
    public bool EnableQuickTranslateAiPhonetic { get; set; }
    public string QuickTranslateHotkey { get; set; } = "Alt+Shift+T";
    public string FloatingLyricsHotkey { get; set; } = "";
    public string DesktopWidgetHotkey { get; set; } = "";
    public string WaterReminderDrinkHotkey { get; set; } = "";
    public string QuickTranslateWindowMaterial { get; set; } = "Mica";
    public string QuickTranslateFontFamily { get; set; } = "Microsoft YaHei UI";
    public bool EnableWaterReminder { get; set; }
    public int WaterReminderIntervalMinutes { get; set; } = 45;
    public int WaterReminderSnoozeMinutes { get; set; } = 10;
    public int WaterReminderDailyGoal { get; set; } = 8;
    public bool WaterReminderShowSystemNotification { get; set; } = true;
    public bool WaterReminderHideInFullscreen { get; set; } = true;
    public string WaterReminderQuietStart { get; set; } = "22:00";
    public string WaterReminderQuietEnd { get; set; } = "07:00";
    public string WaterReminderRecordDate { get; set; } = "";
    public int WaterReminderCompletedToday { get; set; }
    public List<DateTime> WaterReminderDrinkHistory { get; set; } = [];
    public DateTime? WaterReminderLastCompletedAt { get; set; }
    public DateTime? WaterReminderSnoozedUntil { get; set; }
    public string SettingsWindowMaterial { get; set; } = "Mica";
    public string ApplicationTheme { get; set; } = "System";
    public bool EnableTaskbarPerformanceMonitor { get; set; }
    public int TaskbarPerformanceSummaryMetricCount { get; set; } = 5;
    public bool EnableEnhancedTemperatureSensors { get; set; }
    public List<string> TaskbarPerformanceMetrics { get; set; } = TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
    public List<string> TaskbarPerformanceSummaryMetrics { get; set; } = TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
    public int TaskbarPerformanceRefreshSeconds { get; set; } = 1;
    public bool TaskbarPerformanceIsDoubleLine { get; set; }
    public string TaskbarPerformanceFontFamily { get; set; } = "Microsoft YaHei";
    public double TaskbarPerformanceFontSize { get; set; } = 10;
    public string TaskbarPerformanceFontWeight { get; set; } = "SemiBold";
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public string TextColor { get; set; } = "#FFFFFF";
    public string ActiveTextColor { get; set; } = "#FF33BBFF";
    public string BackgroundColor { get; set; } = "#33000000";
    public bool EnableShadow { get; set; }
    public string FontWeight { get; set; } = "SemiBold";
    public bool EnableOutline { get; set; }
    public bool EnableTaskbarHoverMediaControls { get; set; } = true;
    public int OffsetX { get; set; } = 10;
    public int? TaskbarPerformanceOffsetX { get; set; }
    public bool EnableTaskbarTranslateButton { get; set; } = true;
    public int? TaskbarTranslateButtonOffsetX { get; set; }
    public int? TaskbarWaterReminderOffsetX { get; set; }
    public string TaskbarMonitorDeviceName { get; set; } = "";
    public string TaskbarPerformanceMonitorDeviceName { get; set; } = "";
    public string TaskbarTranslateButtonMonitorDeviceName { get; set; } = "";
    public string TaskbarWaterReminderMonitorDeviceName { get; set; } = "";
    public bool IsDoubleLine { get; set; } = true;
    public double LyricOffsetSeconds { get; set; }
    public List<string> IncludedAppIds { get; set; } = [];
    public bool EnableFloatingLyrics { get; set; }
    public bool FloatingLyricsLocked { get; set; }
    public bool FloatingLyricsClickThrough { get; set; }
    public string FloatingLyricsFontFamily { get; set; } = "Microsoft YaHei";
    public double FloatingLyricsFontSize { get; set; } = 20;
    public string FloatingLyricsFontWeight { get; set; } = "Bold";
    public string FloatingLyricsTextColor { get; set; } = "#FF1F2937";
    public string FloatingLyricsBackgroundColor { get; set; } = "#FFFFFFFF";
    public bool FloatingLyricsUseAcrylic { get; set; }
    public bool FloatingLyricsEnableShadow { get; set; } = false;
    public double? FloatingLyricsLeft { get; set; }
    public double? FloatingLyricsTop { get; set; }
    public double? FloatingLyricsWidth { get; set; }
    public bool EnableDesktopWidget { get; set; }
    public int DesktopWidgetTheme { get; set; }
    public double DesktopWidgetLeft { get; set; } = 48;
    public double DesktopWidgetTop { get; set; } = 48;
    public string DesktopWidgetMonitorDeviceName { get; set; } = "";
    public double? DesktopWidgetMonitorOffsetX { get; set; }
    public double? DesktopWidgetMonitorOffsetY { get; set; }
    public bool DesktopWidgetLocked { get; set; }
    public double NextLyricFontSizeDiff { get; set; } = 2;
    public double DoubleLineGap { get; set; } = 6;
    public string NextLyricFontWeight { get; set; } = "Normal";
    public bool RunOnlyWithMusicApp { get; set; }
    public string MusicAppProcessNames { get; set; } = "QQMusic,cloudmusic,Spotify,YesPlayMusic,Foobar2000";
    public bool CenterTaskbarIcons { get; set; }
    public bool ApplyTaskbarAcrylic { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = false;

    public static SettingsDocument Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SettingsDocument();

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            bool hasSummaryMetrics = document.RootElement.TryGetProperty(
                nameof(TaskbarPerformanceSummaryMetrics), out _);
            SettingsDocument settings = JsonSerializer.Deserialize<SettingsDocument>(json) ?? new SettingsDocument();
            settings.TranslationProviders = TranslationProviderProfiles.Normalize(
                settings.TranslationProviders,
                settings.TranslationProvider,
                settings.BaiduTranslationAppId,
                settings.BaiduTranslationAppSecret,
                settings.YoudaoTranslationAppKey,
                settings.YoudaoTranslationAppSecret);
            settings.SelectedTranslationProviderId = TranslationProviderProfiles.ResolveSelectedId(
                settings.TranslationProviders,
                settings.SelectedTranslationProviderId);
            settings.QuickTranslateDomains = TranslationDomainCatalog.Normalize(settings.QuickTranslateDomains);
            settings.SelectedQuickTranslateDomain = TranslationDomainCatalog.ResolveSelected(
                settings.QuickTranslateDomains,
                settings.SelectedQuickTranslateDomain);
            settings.QuickTranslateTargetLanguage = QuickTranslateTargetLanguages.Normalize(
                settings.QuickTranslateTargetLanguage);
            settings.NormalizeWaterReminder(DateTime.Now);
            settings.IncludedAppIds ??= [];
            settings.TaskbarPerformanceMetrics ??= TaskbarPerformanceMetricCatalog.DefaultSelection.ToList();
            settings.TaskbarPerformanceMetrics = TaskbarPerformanceMetricCatalog.Normalize(settings.TaskbarPerformanceMetrics);
            settings.TaskbarPerformanceSummaryMetricCount = Math.Clamp(
                settings.TaskbarPerformanceSummaryMetricCount,
                1,
                TaskbarPerformanceMetricCatalog.Definitions.Count);
            if (!hasSummaryMetrics)
            {
                settings.TaskbarPerformanceSummaryMetrics = TaskbarPerformanceMetricCatalog.GetSummarySelection(
                    settings.TaskbarPerformanceMetrics,
                    settings.TaskbarPerformanceSummaryMetricCount);
            }
            settings.TaskbarPerformanceSummaryMetrics = TaskbarPerformanceMetricCatalog.GetSummarySelection(
                settings.TaskbarPerformanceMetrics,
                settings.TaskbarPerformanceSummaryMetrics,
                settings.TaskbarPerformanceSummaryMetricCount);
            settings.TaskbarPerformanceRefreshSeconds = settings.TaskbarPerformanceRefreshSeconds is 1 or 2 or 5
                ? settings.TaskbarPerformanceRefreshSeconds
                : 1;
            settings.TaskbarPerformanceMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                settings.TaskbarPerformanceMonitorDeviceName,
                settings.TaskbarMonitorDeviceName);
            settings.TaskbarTranslateButtonMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                settings.TaskbarTranslateButtonMonitorDeviceName,
                settings.TaskbarMonitorDeviceName);
            settings.TaskbarWaterReminderMonitorDeviceName = TaskbarComponentMonitorSelection.Resolve(
                settings.TaskbarWaterReminderMonitorDeviceName,
                settings.TaskbarMonitorDeviceName);
            return settings;
        }
        catch
        {
            return new SettingsDocument();
        }
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void NormalizeWaterReminder(DateTime now)
    {
        WaterReminderDrinkHistory = WaterReminderHistory.Normalize(WaterReminderDrinkHistory, now);
        WaterReminderIntervalMinutes = Math.Clamp(WaterReminderIntervalMinutes, 15, 240);
        WaterReminderSnoozeMinutes = Math.Clamp(WaterReminderSnoozeMinutes, 5, 60);
        WaterReminderDailyGoal = Math.Clamp(WaterReminderDailyGoal, 1, 24);
        WaterReminderQuietStart = NormalizeWaterReminderTime(WaterReminderQuietStart, "22:00");
        WaterReminderQuietEnd = NormalizeWaterReminderTime(WaterReminderQuietEnd, "07:00");

        string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(WaterReminderRecordDate, today, StringComparison.Ordinal))
        {
            WaterReminderRecordDate = today;
            WaterReminderCompletedToday = 0;
        }

        WaterReminderCompletedToday = Math.Max(0, WaterReminderCompletedToday);
        if (WaterReminderSnoozedUntil <= now)
        {
            WaterReminderSnoozedUntil = null;
        }
    }

    private static string NormalizeWaterReminderTime(string? value, string fallback) =>
        TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
            ? time.ToString("hh\\:mm", CultureInfo.InvariantCulture)
            : fallback;
}
