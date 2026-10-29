using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Media;

using System.Windows.Media.Imaging;
using System.Windows.Input;

// Aliases to avoid ambiguity with System.Windows.Forms/System.Drawing
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TaskbarInfo
{
    public partial class MainWindow : Window
    {
        private const string SharedSettingsAppliedEventName = "TinyBar.Settings.Apply";
        private const int QuickTranslateHotkeyId = 0x4C58;
        private const int FloatingLyricsHotkeyId = 0x4C59;
        private const int DesktopWidgetHotkeyId = 0x4C5A;
        private const int WaterReminderDrinkHotkeyId = 0x4C5B;
        private const int QuickTranslateSettingsPage = 6;
        private const int TaskbarPerformanceSettingsPage = 7;
        private const int WaterReminderSettingsPage = 8;
        private static readonly uint SettingsNavigateMessage =
            UnmanagedMethods.RegisterWindowMessage("TinyBar.Settings.Navigate");

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRenderFrame;
                StopSharedSettingsApplyNotification();
                StopSettingsUpdateRequestNotification();
                RestoreTaskbarOnExit();
                _taskbarPerformanceWindow?.Dispose();
                _taskbarTranslateButtonWindow?.Dispose();
                _taskbarWaterReminderWindow?.Dispose();
                _waterReminderPopup?.Dispose();
                _waterReminderTimer?.Stop();
                CloseQuickTranslatePopup();
                CloseSettingsHost();
                UnregisterQuickTranslateHotkey();
                _audioDeviceService.Dispose();
                _hoverWatchTimer?.Stop();
                _audioDeviceToastTimer?.Stop();
            };
            
            _hoverWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _hoverWatchTimer.Tick += HoverWatchTimer_Tick;

            // Initialize references first
            _mainLyricControl = InfoText;
            _nextLyricControl = NextLyricText;
            
            // Then initialize TextBlock positions (ApplySettings no longer sets these)
            Canvas.SetTop(_mainLyricControl, 2);
            Canvas.SetLeft(_mainLyricControl, 0);
            Canvas.SetTop(_nextLyricControl, 0); // Will be set by logic based on mode
            Canvas.SetLeft(_nextLyricControl, 0);
        }

        private const double MinTaskbarLyricsWidth = 180;
        private const double MaxTaskbarLyricsWidth = 1600;

        private bool _isDraggingHandle = false;
        private Point _dragStartMouseScreenPos;
        private int _dragStartOffsetX;
        private bool _isDraggingWidth = false;
        private double _dragStartWidth;
        private int _dragStartWidthOffsetX;

        private AppSettings _settings = new AppSettings();
        
        private MediaManager _mediaManager = new MediaManager();
        private LyricsEngine _lyricsEngine = new LyricsEngine();
        private string _currentArtist = "";
        private string _currentTitle = "";
        private string _currentTrackKey = "";
        private MediaTrackInfo _currentTrackInfo = new();
        private bool _hasLyrics = false;
        private string _lastLyricText = "";
        private string _lastCurrentLyric = ""; // Track current lyric separately for animation
        private bool _isShowingStatusText = true;
        private double _scrollableLyricDistance = 0; // 当前行超宽时可滚动的总距离，>0 时滚动跟随逐字进度
        private LyricsEngine.LyricLine? _activeLyricLine; // 当前逐字行，由渲染帧回调平滑推进
        private CancellationTokenSource? _lyricsSearchCts;
        private int _lyricsSearchVersion;
        
        // Sync & Interpolation logic
        private DispatcherTimer? _lyricSyncTimer;
        private TimeSpan _lastMediaPosition = TimeSpan.Zero;
        private DateTime _lastSyncTime = DateTime.Now;
        private bool _isMediaPlaying = false;
        
        // Swappable controls references
        private TextBlock _mainLyricControl;
        private TextBlock _nextLyricControl;

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private FloatingLyricsWindow? _floatingWindow;
        private DesktopWidgetWindow? _desktopWidget;
        private DispatcherTimer? _desktopHostTimer;
        private readonly UpdateService _updateService = new UpdateService();
        private readonly AudioDeviceService _audioDeviceService = new AudioDeviceService();
        private bool _isUpdatingVolumeSliderProgrammatically;
        private bool _volumeUiReady;
        private DispatcherTimer? _hoverWatchTimer;
        private UpdateCheckResult? _pendingUpdateResult;
        private bool _isCheckingUpdates;
        
        private DispatcherTimer? _processMonitorTimer;
        private TaskbarPerformanceWindow? _taskbarPerformanceWindow;
        private TaskbarTranslateButtonWindow? _taskbarTranslateButtonWindow;
        private TaskbarWaterReminderWindow? _taskbarWaterReminderWindow;
        private WaterReminderPopupWindow? _waterReminderPopup;
        private DispatcherTimer? _waterReminderTimer;
        private bool _waterReminderWasDue;
        private DispatcherTimer? _taskbarCenteringTimer;
        private IntPtr _centeredTaskSwitcher = IntPtr.Zero;
        private IntPtr _lastAcrylicTaskbar = IntPtr.Zero;
        private bool _centeringApplied;
        private bool _acrylicApplied;
        private bool _recentringAfterRebuild;
        private (int Left, int Top, int Width, int Height)? _lastCenteredBounds;
        private QuickTranslatePopupWindow? _quickTranslatePopup;
        private System.Diagnostics.Process? _settingsProcess;
        private HwndSource? _mainWindowSource;
        private bool _quickTranslateHotkeyRegistered;
        private EventWaitHandle? _sharedSettingsAppliedEvent;
        private RegisteredWaitHandle? _sharedSettingsAppliedWait;
        private EventWaitHandle? _settingsUpdateRequestedEvent;
        private RegisteredWaitHandle? _settingsUpdateRequestedWait;
        private string? _settingsUpdateRequestEventName;

        private void SetupProcessMonitor()
        {
            if (_processMonitorTimer == null)
            {
                _processMonitorTimer = new DispatcherTimer();
                _processMonitorTimer.Interval = TimeSpan.FromSeconds(3);
                _processMonitorTimer.Tick += ProcessMonitor_Tick;
            }

            if (_settings.RunOnlyWithMusicApp)
            {
                _processMonitorTimer.Start();
                ProcessMonitor_Tick(null, null); // Check immediately
            }
            else
            {
                _processMonitorTimer.Stop();
                if (this.Visibility != Visibility.Visible)
                {
                     this.Show(); 
                }
            }
        }

        private void ProcessMonitor_Tick(object? sender, EventArgs? e)
        {
            if (!_settings.RunOnlyWithMusicApp) return;

            string[] targets = _settings.MusicAppProcessNames.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            bool found = false;

            var processes = System.Diagnostics.Process.GetProcesses();
            foreach (var p in processes)
            {
                foreach (var t in targets)
                {
                    if (p.ProcessName.Equals(t.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }

            if (found)
            {
                SetTrayText("TinyBar");

                if (this.Visibility != Visibility.Visible)
                {
                    this.Show();
                }
                if (_settings.EnableFloatingLyrics && _floatingWindow != null && _floatingWindow.Visibility != Visibility.Visible)
                {
                    _floatingWindow.Show();
                }
            }
            else
            {
                SetTrayText("TinyBar - 已隐藏，等待播放器运行");
                // Hide everything
                if (this.Visibility == Visibility.Visible)
                {
                    this.Hide();
                }

                if (_floatingWindow != null && _floatingWindow.Visibility == Visibility.Visible)
                {
                    _floatingWindow.Hide();
                }
            }
        }

        /// <summary>
        /// Loads the tray glyph. The artwork is black with a white outline so a single
        /// asset stays legible on both light and dark taskbars, and no theme switch is
        /// needed when the user changes the system theme.
        /// </summary>
        private void ApplyTrayIcon()
        {
            if (_notifyIcon == null) return;

            try
            {
                var uri = new Uri("pack://application:,,,/src/icons/TinyBar_Tray.png");
                var info = Application.GetResourceStream(uri);
                if (info != null)
                {
                    using (var stream = info.Stream)
                    using (var bitmap = new System.Drawing.Bitmap(stream))
                    using (var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon()))
                    {
                        // Clone so the HICON can be destroyed with the temporary bitmap.
                        _notifyIcon.Icon = (System.Drawing.Icon)icon.Clone();
                    }

                    return;
                }
            }
            catch
            {
                // Fall through to the generic system icon.
            }

            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Init Tray Icon
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                ApplyTrayIcon();

                // Window Icon
                this.Icon = App.GetAppIcon();
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            _notifyIcon.Visible = true;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "TinyBar";
            _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
            
            // Handle Mouse Up to show WPF ContextMenu
            _notifyIcon.MouseUp += (s, args) => 
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    // Activate window to ensure menu closes when clicking outside
                    var helper = new WindowInteropHelper(this);
                    UnmanagedMethods.SetForegroundWindow(helper.Handle);
                    
                    if (FindResource("TrayContextMenu") is ContextMenu trayMenu)
                    {
                        trayMenu.PlacementTarget = MainBorder;
                        trayMenu.IsOpen = true;
                    }
                }
            };
            
            // Old WinForms Menu removed
            // var trayMenu = new System.Windows.Forms.ContextMenuStrip(); 
            // ...

            // Load Settings
            _settings = AppSettings.Load();
            StartSharedSettingsApplyNotification();
            StartSettingsUpdateRequestNotification();
            
            // Apply visual settings
            ApplySettings();
            
            // Sync Menu State (will trigger Checked/Unchecked events which handle window creation)
            if (FloatingLyricsMenuItem != null)
                FloatingLyricsMenuItem.IsChecked = _settings.EnableFloatingLyrics;

            if (FloatingLyricsCtxItem != null)
            {
                FloatingLyricsCtxItem.IsChecked = _settings.FloatingLyricsClickThrough;
                // Visibility controlled by ManageFloatingWindow
            }
            
            // Re-calc vertical center on resize
            TextContainer.SizeChanged += (s, e) => UpdateVerticalCentering();
            
            // Ensure floating window is correct state (in case menu item was null or event didn't fire)
            ManageFloatingWindow();
            ManageDesktopWidget();
            
            InjectIntoTaskbar();
            ManageTaskbarPerformance();
            ManageTaskbarTranslateButton();
            
            // Initialize Media Manager
            _mediaManager.FilterAppIds = _settings.IncludedAppIds; // Initialize Filter
            _mediaManager.MediaInfoChanged += MediaManager_MediaInfoChanged;
            _mediaManager.PlaybackPositionChanged += MediaManager_PlaybackPositionChanged;
            _mediaManager.PlaybackStatusChanged += MediaManager_PlaybackStatusChanged;
            _mediaManager.AppIdChanged += MediaManager_AppIdChanged;
            
            // Setup High-Frequency Smooth Sync Timer for Verse Color
            _lyricSyncTimer = new DispatcherTimer(DispatcherPriority.Render);
            _lyricSyncTimer.Interval = TimeSpan.FromMilliseconds(100);
            _lyricSyncTimer.Tick += LyricSyncTimer_Tick;
            _lyricSyncTimer.Start();

            // 逐字渐变与滚动由渲染帧驱动，帧率与显示器刷新同步，避免 100ms 轮询的跳变感
            CompositionTarget.Rendering += OnRenderFrame;

            _mediaManager.Initialize();
            _isMediaPlaying = _mediaManager.IsPlaying;
            InitializeAudioDeviceControls();

            if (_settings.AutoCheckUpdates)
            {
                _ = CheckForUpdatesAsync(isStartupCheck: true);
            }

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(PrewarmSettingsHost));
        }

        private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e)
        {
            if (_pendingUpdateResult != null)
            {
                ShowUpdateDialog(_pendingUpdateResult);
            }
        }

        private void LyricSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (!_hasLyrics || !_isMediaPlaying) return;

            // Interpolate current position based on last system sync
            TimeSpan elapsedSinceSync = DateTime.Now - _lastSyncTime;
            TimeSpan currentEstimatedPosition = _lastMediaPosition + elapsedSinceSync;

            // Update UI with interpolated position
            UpdateLyricsUI(currentEstimatedPosition);
            _desktopWidget?.UpdatePlayback(currentEstimatedPosition, GetCurrentTrackDuration());
        }

        // ... existing methods ...

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            // Close notify icon to avoid ghost icon
            if (_notifyIcon != null)
            {
                _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            
            // Start new instance
            var path = Environment.ProcessPath;
            if (path != null)
            {
                 // Use ShellExecute=true if needed, or simply start process
                 // With .NET Core/5+, Process.Start(string) uses ShellExecute=true by default only on Windows? No.
                 // Actually Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); is safer for exe execution from context like this.
                 try 
                 {
                     System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                 }
                 catch 
                 {
                     // Fallback
                     try { System.Diagnostics.Process.Start(path); } catch { }
                 }
            }
            
            Application.Current.Shutdown();
        }

        private void Exit_Click(object? sender, RoutedEventArgs? e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Application.Current.Shutdown();
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(isStartupCheck: false);
        }

        private async Task CheckForUpdatesAsync(bool isStartupCheck)
        {
            if (_isCheckingUpdates)
            {
                return;
            }

            _isCheckingUpdates = true;

            try
            {
                var result = await _updateService.CheckForUpdatesAsync();

                if (!result.Success)
                {
                    if (!isStartupCheck)
                    {
                        UpdateDialogWindow.ShowForError(this, result.ErrorMessage ?? "发生了未知错误。", _settings.SettingsWindowMaterial, _settings.ApplicationTheme);
                    }
                    return;
                }

                if (result.NoReleasePublished)
                {
                    if (!isStartupCheck)
                    {
                        ShowUpdateDialog(result);
                    }
                    return;
                }

                if (!result.HasUpdate)
                {
                    if (!isStartupCheck)
                    {
                        ShowUpdateDialog(result);
                    }
                    return;
                }

                _pendingUpdateResult = result;

                if (isStartupCheck)
                {
                    _notifyIcon?.ShowBalloonTip(
                        5000,
                        "TinyBar 有新版本",
                        $"当前 {result.CurrentVersionDisplay}，最新 {result.LatestVersionDisplay}。点击此通知可下载并安装。",
                        System.Windows.Forms.ToolTipIcon.Info);
                    return;
                }

                ShowUpdateDialog(result);
            }
            catch (Exception ex)
            {
                if (!isStartupCheck)
                {
                    UpdateDialogWindow.ShowForError(this, ex.Message, _settings.SettingsWindowMaterial, _settings.ApplicationTheme);
                }
            }
            finally
            {
                _isCheckingUpdates = false;
            }
        }

        private void ShowUpdateDialog(UpdateCheckResult result)
        {
            UpdateDialogWindow.ShowForResult(
                this,
                result,
                _settings.SettingsWindowMaterial,
                _settings.ApplicationTheme);
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void SetTrayText(string text)
        {
            if (_notifyIcon == null) return;
            _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void ShowTrayWarning(string message)
        {
            if (_notifyIcon == null) return;
            _notifyIcon.ShowBalloonTip(3000, "TinyBar", message, System.Windows.Forms.ToolTipIcon.Warning);
        }

        private void ApplySettings()
        {
            ApplyApplicationTheme();
            ApplyTrayIcon();
            AutoStartupService.Sync(_settings.LaunchOnStartup);

            // Sync Process Monitoring
            SetupProcessMonitor();
            ManageTaskbarPerformance();
            ManageTaskbarTranslateButton();
            ManageWaterReminder();
            ApplyTaskbarCustomization();
            ConfigureQuickTranslateHotkey();
            
            // Sync Floating Window
            _floatingWindow?.ApplySettings(_settings);
            ManageDesktopWidget();
            _desktopWidget?.ApplySettings(_settings);

            if (!_settings.EnableTaskbarHoverMediaControls && HoverMediaControlsBar != null)
            {
                HoverMediaControlsBar.Opacity = 0;
                HoverMediaControlsBar.IsHitTestVisible = false;
                HoverMediaControlsBar.Visibility = Visibility.Collapsed;
                TextContainer.Opacity = 1;
                VolumePopup.IsOpen = false;
                AudioDevicePopup.IsOpen = false;
            }

            // Do NOT set this.Width/Height here, as it causes conflict with MoveWindow in InjectIntoTaskbar
            // this.Width = _settings.Width;
            
            // Adjust Height for Double Line logic (Internal elements only)
            double baseHeight = 30; // Default Taskbar Height approx
            bool isActuallyDoubleLine = _settings.IsDoubleLine && _hasLyrics;
            double targetHeight = isActuallyDoubleLine ? baseHeight * 2 : baseHeight;
            // this.Height = targetHeight; // Don't set Window Height, let MoveWindow handle it
            
            // Allow TextContainer to fill available space
            TextContainer.Height = double.NaN; 
            TextContainer.VerticalAlignment = VerticalAlignment.Stretch;
            
            _mainLyricControl.FontSize = _settings.FontSize;

            // Apply Background Color
            try
            {
                var bgColor = (Color)ColorConverter.ConvertFromString(_settings.BackgroundColor);
                // Fix: If fully transparent, set Alpha to 1 to stay hit-testable
                if (bgColor.A == 0) bgColor.A = 1; 
                MainBorder.Background = new SolidColorBrush(bgColor);
            }
            catch
            {
                MainBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33000000"));
            }
            
            try 
            {
               var fontFamily = new FontFamily(_settings.FontFamily);
               var mainColor = (Color)ColorConverter.ConvertFromString(_settings.TextColor);
               var activeColor = (Color)ColorConverter.ConvertFromString(_settings.ActiveTextColor);
               
               // Apply colors to the brush defined in XAML instead of replacing it
               if (_mainLyricControl.Foreground is LinearGradientBrush mainBrush && mainBrush.GradientStops.Count >= 2)
               {
                   mainBrush.GradientStops[0].Color = activeColor;
                   mainBrush.GradientStops[1].Color = mainColor;
               }

               _mainLyricControl.FontFamily = fontFamily;
               _mainLyricControl.Opacity = 1.0; 

               try
               {
                   if (!string.IsNullOrEmpty(_settings.FontWeight))
                   {
                       var weightStr = _settings.FontWeight.Split(' ')[0];
                       var converter = new FontWeightConverter();
                       var obj = converter.ConvertFromString(weightStr);
                       _mainLyricControl.FontWeight = (obj as FontWeight?) ?? FontWeights.SemiBold;
                   }
                   else
                   {
                       _mainLyricControl.FontWeight = FontWeights.SemiBold;
                   }
               }
               catch
               {
                   _mainLyricControl.FontWeight = FontWeights.SemiBold;
               }

                if (isActuallyDoubleLine)
                {
                    ApplyDoubleLineLayout();
                }
                else
                {
                    _nextLyricControl.Visibility = Visibility.Collapsed; // 显式隐藏第二行
                    _mainLyricControl.TextWrapping = TextWrapping.NoWrap;
                    UpdateVerticalCentering();
                }
            }
            catch {}

            // Effects
            _mainLyricControl.Effect = null;
            _nextLyricControl.Effect = null;

            if (_settings.EnableShadow || _settings.EnableOutline)
            {
                 var dropShadow = new System.Windows.Media.Effects.DropShadowEffect();
                 dropShadow.Color = Colors.Black;
                 
                 if (_settings.EnableOutline)
                 {
                     dropShadow.BlurRadius = 2;
                     dropShadow.ShadowDepth = 0;
                     dropShadow.Opacity = 1;
                 }
                 else
                 {
                     dropShadow.BlurRadius = 4;
                     dropShadow.ShadowDepth = 2;
                     dropShadow.Opacity = 0.8;
                 }
                 _mainLyricControl.Effect = dropShadow;
                 // Apply effect to next line too
                 _nextLyricControl.Effect = dropShadow;
            }

            if (_isShowingStatusText)
            {
                ApplyStatusTextLayout();
            }
        }

        private void UpdateVerticalCentering()
        {
            bool isActuallyDoubleLine = _settings.IsDoubleLine && _hasLyrics;
            if (isActuallyDoubleLine) return; // Managed by double line logic

            double containerH = TextContainer.ActualHeight;
            if (containerH <= 0 || double.IsNaN(containerH)) containerH = 30; // Fallback

            // Estimate Text Height based on FontSize (approx 1.3-1.4 em usually)
            // Or use ActualHeight if available? TextBlock ActualHeight updates late.
            // Let's use FontSize * 1.4 for safety or measure a sample.
            // Using FontSize * 1.35 is standard for Segoe UI.
            double textH = _settings.FontSize * 1.35;
            
            double top = (containerH - textH) / 2;
            
            Canvas.SetTop(_mainLyricControl, top);
        }

        private void ApplyStatusTextLayout()
        {
            _mainLyricControl.BeginAnimation(Canvas.LeftProperty, null);
            _nextLyricControl.BeginAnimation(Canvas.LeftProperty, null);
            _mainLyricControl.BeginAnimation(Canvas.TopProperty, null);
            _nextLyricControl.BeginAnimation(Canvas.TopProperty, null);

            Canvas.SetLeft(_mainLyricControl, 0);
            Canvas.SetLeft(_nextLyricControl, 0);

            _nextLyricControl.Text = string.Empty;
            _nextLyricControl.Visibility = Visibility.Collapsed;
            _nextLyricControl.Opacity = 0;

            _mainLyricControl.Visibility = Visibility.Visible;
            _mainLyricControl.Opacity = 1.0;
            _mainLyricControl.FontFamily = new FontFamily(_settings.FontFamily);
            _mainLyricControl.FontSize = _settings.FontSize;
            _mainLyricControl.FontWeight = GetConfiguredFontWeight(
                _settings.FontWeight,
                FontWeights.SemiBold);

            // Status text should always render as a single centered line,
            // regardless of whether double-line lyric mode is enabled.
            double containerH = TextContainer.ActualHeight;
            if (containerH <= 0 || double.IsNaN(containerH))
            {
                containerH = 30;
            }

            double textH = _settings.FontSize * 1.35;
            double top = (containerH - textH) / 2;
            Canvas.SetTop(_mainLyricControl, top);
        }

        private void ApplyDoubleLineLayout()
        {
            try
            {
                var fontFamily = new FontFamily(_settings.FontFamily);
                var mainColor = (Color)ColorConverter.ConvertFromString(_settings.TextColor);
                var activeColor = (Color)ColorConverter.ConvertFromString(_settings.ActiveTextColor);

                _nextLyricControl.FontFamily = fontFamily;

                // 副歌词使用独立的渐变画刷实例：避免与主行共享画刷而被逐字 offset 动画影响
                _nextLyricControl.Foreground = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop(activeColor, 0),
                        new GradientStop(mainColor, 0)
                    }
                };

                _nextLyricControl.FontSize = Math.Max(9, _settings.FontSize - _settings.NextLyricFontSizeDiff);
                _nextLyricControl.Opacity = 0.7; // Use Opacity property for dimming
                _nextLyricControl.FontWeight = GetConfiguredFontWeight(_settings.NextLyricFontWeight, FontWeights.Normal);
                _nextLyricControl.Visibility = Visibility.Visible;
                _nextLyricControl.TextWrapping = TextWrapping.NoWrap;

                _mainLyricControl.TextWrapping = TextWrapping.NoWrap;
                _mainLyricControl.Height = double.NaN;

                UpdateDoubleLineVerticalCentering();
            }
            catch { }
        }

        /// <summary>Default gap between the two lyric rows, in DIPs.</summary>
        private const double DefaultDoubleLineGap = 3;

        /// <summary>
        /// Centres the two lyric rows as a block inside the taskbar strip.
        /// The rows are laid out top-to-bottom with a configurable gap, then the whole
        /// block is offset so its middle matches the container's middle.
        /// </summary>
        private void UpdateDoubleLineVerticalCentering()
        {
            // The container may not have been laid out yet when settings are applied
            // (e.g. right after the window is re-hosted), which would make ActualHeight
            // stale and the gap look like it was ignored. Force a layout pass first.
            if (TextContainer.ActualHeight <= 0 || double.IsNaN(TextContainer.ActualHeight))
            {
                TextContainer.UpdateLayout();
            }

            GetDoubleLinePositions(out double top, out double bottom);
            Canvas.SetTop(_mainLyricControl, top);
            Canvas.SetTop(_nextLyricControl, bottom);
        }

        /// <summary>
        /// Shared geometry for the double-line layout. Both the static layout and the
        /// lyric-change animation must use this, otherwise the rows drift apart on every
        /// scroll animation.
        /// </summary>
        private void GetDoubleLinePositions(out double mainTop, out double nextTop)
        {
            // Force a layout pass first: ActualHeight is 0 before the window is measured,
            // which would make the rows jump between renders.
            if (TextContainer.ActualHeight <= 0)
            {
                TextContainer.UpdateLayout();
            }

            double containerH = TextContainer.ActualHeight;
            if (containerH <= 0 || double.IsNaN(containerH)) containerH = 40;

            double mainSize = _settings.FontSize;
            double nextSize = Math.Max(9, _settings.FontSize - _settings.NextLyricFontSizeDiff);

            // Advance height of a single line. 1.35em overestimates and makes two rows
            // overlap when the gap is 0, so use the tighter 1.2em that matches the actual
            // rendered line box for the default font stack.
            double mainH = mainSize * 1.2;
            double nextH = nextSize * 1.2;
            // Take the configured gap literally, including 0 (rows flush together). Only a
            // missing/negative value falls back to the default, which happens for configs
            // written before the setting existed.
            double gap = _settings.DoubleLineGap >= 0 ? _settings.DoubleLineGap : DefaultDoubleLineGap;

            // The container is inside a padded border, so its usable height is smaller than
            // the taskbar strip. Clamp so the second row can never be pushed out of view.
            double maxGap = Math.Max(0, containerH - mainH - nextH);
            if (gap > maxGap) gap = maxGap;

            double blockH = mainH + gap + nextH;
            double startTop = (containerH - blockH) / 2;

            mainTop = startTop;
            nextTop = startTop + mainH + gap;
        }

        private static FontWeight GetConfiguredFontWeight(string? value, FontWeight fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            try
            {
                var converter = new FontWeightConverter();
                var converted = converter.ConvertFromString(value.Split(' ')[0]);
                return (converted as FontWeight?) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings(0);
        }

        private void ShowQuickTranslate()
        {
            if (_quickTranslatePopup?.IsVisible == true)
            {
                CloseQuickTranslatePopup();
                return;
            }

            if (!TryGetQuickTranslateLaunchOptions(out QuickTranslateLaunchOptions options))
            {
                ShowTrayWarning("无法获取任务栏位置，快捷翻译未打开。");
                return;
            }

            try
            {
                var popup = new QuickTranslatePopupWindow(_settings);
                _quickTranslatePopup = popup;
                popup.Closed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_quickTranslatePopup, popup))
                    {
                        _quickTranslatePopup = null;
                    }
                }));
                popup.ShowAt(options);
            }
            catch (Exception exception)
            {
                _quickTranslatePopup = null;
                ShowTrayWarning("快捷翻译窗口启动失败: " + exception.Message);
            }
        }

        private bool TryGetQuickTranslateLaunchOptions(out QuickTranslateLaunchOptions options)
        {
            options = null!;
            IntPtr taskbarHandle = TaskbarMonitorLocator.FindTaskbarWindow(_settings.TaskbarTranslateButtonMonitorDeviceName);
            if (taskbarHandle == IntPtr.Zero ||
                !UnmanagedMethods.GetWindowRect(taskbarHandle, out UnmanagedMethods.RECT taskbarRect))
            {
                return false;
            }

            System.Drawing.Rectangle taskbarBounds = System.Drawing.Rectangle.FromLTRB(
                taskbarRect.Left, taskbarRect.Top, taskbarRect.Right, taskbarRect.Bottom);
            System.Drawing.Rectangle buttonBounds;
            if (_taskbarTranslateButtonWindow?.TryGetScreenBounds(out buttonBounds) != true)
            {
                buttonBounds = taskbarBounds;
            }

            var screen = System.Windows.Forms.Screen.FromHandle(taskbarHandle);
            options = new QuickTranslateLaunchOptions(
                buttonBounds,
                taskbarBounds,
                screen.Bounds,
                screen.WorkingArea);
            return true;
        }

        private void CloseQuickTranslatePopup()
        {
            QuickTranslatePopupWindow? popup = _quickTranslatePopup;
            _quickTranslatePopup = null;
            try
            {
                popup?.Close();
            }
            catch
            {
            }
        }

        private readonly System.Collections.Generic.Dictionary<int, string> _activeRegisteredHotkeys = new();
        private readonly System.Collections.Generic.Dictionary<int, string> _lastConfiguredHotkeys = new();
        private readonly System.Collections.Generic.Dictionary<int, string> _warnedOccupiedHotkeys = new();

        private void ConfigureQuickTranslateHotkey() => ConfigureGlobalHotkeys();
        private void UnregisterQuickTranslateHotkey() => UnregisterGlobalHotkeys();

        private void ConfigureGlobalHotkeys()
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null) return;

            if (_mainWindowSource != source)
            {
                _mainWindowSource?.RemoveHook(MainWindowMessageHook);
                _mainWindowSource = source;
                _mainWindowSource.AddHook(MainWindowMessageHook);
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            SyncFeatureHotkey(handle, QuickTranslateHotkeyId, _settings.QuickTranslateHotkey, "快捷翻译");
            SyncFeatureHotkey(handle, FloatingLyricsHotkeyId, _settings.FloatingLyricsHotkey, "悬浮歌词");
            SyncFeatureHotkey(handle, DesktopWidgetHotkeyId, _settings.DesktopWidgetHotkey, "桌面歌词");
            SyncFeatureHotkey(handle, WaterReminderDrinkHotkeyId, _settings.WaterReminderDrinkHotkey, "饮水打卡");

            _quickTranslateHotkeyRegistered = _activeRegisteredHotkeys.ContainsKey(QuickTranslateHotkeyId);
        }

        private void SyncFeatureHotkey(IntPtr handle, int id, string? hotkeyText, string name)
        {
            string configured = hotkeyText?.Trim() ?? string.Empty;

            if (_lastConfiguredHotkeys.TryGetValue(id, out string? lastConfigured) &&
                string.Equals(lastConfigured, configured, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_activeRegisteredHotkeys.ContainsKey(id))
            {
                UnmanagedMethods.UnregisterHotKey(handle, id);
                _activeRegisteredHotkeys.Remove(id);
            }

            _lastConfiguredHotkeys[id] = configured;

            if (string.IsNullOrWhiteSpace(configured))
            {
                _warnedOccupiedHotkeys.Remove(id);
                return;
            }

            if (!QuickTranslateHotkey.TryParse(configured, out QuickTranslateHotkey hotkey))
            {
                if (!_warnedOccupiedHotkeys.TryGetValue(id, out string? warned) ||
                    !string.Equals(warned, configured, StringComparison.OrdinalIgnoreCase))
                {
                    ShowTrayWarning($"{name}快捷键格式无效，请使用 Alt+Shift+字母 这类格式。");
                    _warnedOccupiedHotkeys[id] = configured;
                }
                return;
            }

            bool success = UnmanagedMethods.RegisterHotKey(
                handle,
                id,
                hotkey.Modifiers | UnmanagedMethods.MOD_NOREPEAT,
                hotkey.VirtualKey);

            if (success)
            {
                _activeRegisteredHotkeys[id] = configured;
                _warnedOccupiedHotkeys.Remove(id);
            }
            else
            {
                if (!_warnedOccupiedHotkeys.TryGetValue(id, out string? warned) ||
                    !string.Equals(warned, configured, StringComparison.OrdinalIgnoreCase))
                {
                    ShowTrayWarning($"{name}快捷键「{configured}」已被其他程序占用。");
                    _warnedOccupiedHotkeys[id] = configured;
                }
            }
        }

        private void UnregisterGlobalHotkeys()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                foreach (int id in _activeRegisteredHotkeys.Keys)
                {
                    UnmanagedMethods.UnregisterHotKey(handle, id);
                }
            }
            _activeRegisteredHotkeys.Clear();
            _lastConfiguredHotkeys.Clear();
            _warnedOccupiedHotkeys.Clear();
            _quickTranslateHotkeyRegistered = false;
        }

        private void ToggleFloatingLyrics()
        {
            _settings.EnableFloatingLyrics = !_settings.EnableFloatingLyrics;
            _settings.Save();
            ManageFloatingWindow();
            if (FloatingLyricsMenuItem != null)
            {
                FloatingLyricsMenuItem.IsChecked = _settings.EnableFloatingLyrics;
            }
        }

        private void ToggleDesktopWidget()
        {
            _settings.EnableDesktopWidget = !_settings.EnableDesktopWidget;
            _settings.Save();
            ManageDesktopWidget();
        }

        private IntPtr MainWindowMessageHook(
            IntPtr handle,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == UnmanagedMethods.WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == QuickTranslateHotkeyId)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(ShowQuickTranslate));
                }
                else if (hotkeyId == FloatingLyricsHotkeyId)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(ToggleFloatingLyrics));
                }
                else if (hotkeyId == DesktopWidgetHotkeyId)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(ToggleDesktopWidget));
                }
                else if (hotkeyId == WaterReminderDrinkHotkeyId)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(RecordWaterDrink));
                }
            }
            return IntPtr.Zero;
        }

        private void OpenSettings(int initialNavIndex)
        {
            if (TryActivateSettingsHost(initialNavIndex)) return;

            EventWaitHandle? settingsAppliedEvent = null;
            RegisteredWaitHandle? settingsAppliedWait = null;
            int settingsApplied = 0;

            try
            {
                string settingsHost = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "SettingsHost",
                    "TinyBar.Settings.exe");
                if (!System.IO.File.Exists(settingsHost))
                {
                    settingsHost = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "SettingsHost",
                        "taskbarTool.Settings.exe");
                }
                if (!System.IO.File.Exists(settingsHost))
                {
                    settingsHost = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "SettingsHost",
                        "TaskbarInfo.Settings.exe");
                }
                if (!System.IO.File.Exists(settingsHost))
                {
                    System.Windows.MessageBox.Show("设置窗口组件未找到，请重新生成开发版本。", "TinyBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string applyEventName = $"TinyBar.Settings.{Environment.ProcessId}.{Guid.NewGuid():N}";
                settingsAppliedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, applyEventName);
                settingsAppliedWait = ThreadPool.RegisterWaitForSingleObject(
                    settingsAppliedEvent,
                    (_, _) =>
                    {
                        Interlocked.Exchange(ref settingsApplied, 1);
                        Dispatcher.BeginInvoke(new Action(ReloadSettingsFromHost));
                    },
                    null,
                    Timeout.Infinite,
                    false);

                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = settingsHost,
                    Arguments = $"\"{AppSettings.SettingsPath}\" --page={initialNavIndex} --apply-event=\"{applyEventName}\" --update-event=\"{_settingsUpdateRequestEventName}\"",
                    UseShellExecute = false
                });
                if (process == null)
                {
                    DisposeSettingsApplyNotification(settingsAppliedEvent, settingsAppliedWait);
                    return;
                }

                _settingsProcess = process;
                int processId = process.Id;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (process.ExitCode == 0 && Interlocked.CompareExchange(ref settingsApplied, 0, 0) == 0)
                        {
                            ReloadSettingsFromHost();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    DisposeSettingsApplyNotification(settingsAppliedEvent, settingsAppliedWait);
                    if (_settingsProcess?.Id == processId)
                    {
                        _settingsProcess = null;
                    }
                    try { process.Dispose(); } catch (InvalidOperationException) { }
                }));
            }
            catch (Exception exception)
            {
                DisposeSettingsApplyNotification(settingsAppliedEvent, settingsAppliedWait);
                System.Windows.MessageBox.Show($"无法打开设置窗口：{exception.Message}", "TinyBar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PrewarmSettingsHost()
        {
            if (_settingsProcess != null) return;

            string settingsHost = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "SettingsHost",
                "TinyBar.Settings.exe");
            if (!System.IO.File.Exists(settingsHost))
            {
                settingsHost = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "SettingsHost",
                    "taskbarTool.Settings.exe");
            }
            if (!System.IO.File.Exists(settingsHost))
            {
                settingsHost = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "SettingsHost",
                    "TaskbarInfo.Settings.exe");
            }
            if (!System.IO.File.Exists(settingsHost)) return;

            try
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = settingsHost,
                    Arguments = $"\"{AppSettings.SettingsPath}\" --keep-alive --hidden --parent-pid={Environment.ProcessId} --update-event=\"{_settingsUpdateRequestEventName}\"",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(settingsHost),
                    UseShellExecute = false
                });
                if (process == null) return;

                _settingsProcess = process;
                int processId = process.Id;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_settingsProcess?.Id != processId) return;
                    _settingsProcess.Dispose();
                    _settingsProcess = null;
                }));
            }
            catch
            {
                // Opening settings remains available through the normal on-demand path.
            }
        }

        private bool TryActivateSettingsHost(int initialNavIndex)
        {
            System.Diagnostics.Process? process = _settingsProcess;
            if (process == null) return false;

            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    _settingsProcess = null;
                    return false;
                }

                process.Refresh();
                IntPtr windowHandle = process.MainWindowHandle;
                if (windowHandle == IntPtr.Zero)
                {
                    process.WaitForInputIdle(1500);
                    process.Refresh();
                    windowHandle = process.MainWindowHandle;
                }
                if (windowHandle == IntPtr.Zero)
                {
                    windowHandle = FindTopLevelWindowForProcess(process.Id);
                }
                if (windowHandle != IntPtr.Zero)
                {
                    if (SettingsNavigateMessage != 0)
                    {
                        UnmanagedMethods.PostMessage(
                            windowHandle,
                            SettingsNavigateMessage,
                            new IntPtr(initialNavIndex),
                            IntPtr.Zero);
                    }
                    UnmanagedMethods.ShowWindow(windowHandle, UnmanagedMethods.SW_RESTORE);
                    UnmanagedMethods.SetForegroundWindow(windowHandle);
                }
                return true;
            }
            catch
            {
                process.Dispose();
                _settingsProcess = null;
                return false;
            }
        }

        private static IntPtr FindTopLevelWindowForProcess(int processId)
        {
            IntPtr windowHandle = IntPtr.Zero;
            UnmanagedMethods.EnumWindows((candidate, _) =>
            {
                UnmanagedMethods.GetWindowThreadProcessId(candidate, out uint candidateProcessId);
                if (candidateProcessId != (uint)processId ||
                    UnmanagedMethods.GetParent(candidate) != IntPtr.Zero ||
                    !UnmanagedMethods.IsWindow(candidate))
                {
                    return true;
                }

                windowHandle = candidate;
                return false;
            }, IntPtr.Zero);
            return windowHandle;
        }

        private void CloseSettingsHost()
        {
            System.Diagnostics.Process? process = _settingsProcess;
            _settingsProcess = null;
            if (process == null) return;

            try
            {
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ReloadSettingsFromHost()
        {
            CloseQuickTranslatePopup();
            _settings = AppSettings.Load();
            ApplyApplicationTheme();
            _mediaManager.FilterAppIds = _settings.IncludedAppIds;
            _mediaManager.RefreshSession();
            ManageFloatingWindow();
            ApplySettings();
            InjectIntoTaskbar();
        }

        private void NotifySettingsApplied()
        {
            try
            {
                using var settingsApplied = EventWaitHandle.OpenExisting(SharedSettingsAppliedEventName);
                settingsApplied.Set();
            }
            catch
            {
                // Main window may have exited.
            }
        }

        private void ApplyApplicationTheme()
        {
            WpfThemeService.Apply(
                Application.Current,
                ApplicationThemeParser.Resolve(_settings.ApplicationTheme));
        }

        internal void RefreshSystemTheme()
        {
            if (ApplicationThemeParser.Parse(_settings.ApplicationTheme) != ApplicationThemePreference.System)
            {
                return;
            }

            ApplyApplicationTheme();
            ApplyTrayIcon();
            ApplySettings();
        }

        private void StartSharedSettingsApplyNotification()
        {
            _sharedSettingsAppliedEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                SharedSettingsAppliedEventName);
            _sharedSettingsAppliedWait = ThreadPool.RegisterWaitForSingleObject(
                _sharedSettingsAppliedEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(ReloadSettingsFromHost)),
                null,
                Timeout.Infinite,
                false);
        }

        private void StopSharedSettingsApplyNotification()
        {
            _sharedSettingsAppliedWait?.Unregister(null);
            _sharedSettingsAppliedWait = null;
            _sharedSettingsAppliedEvent?.Dispose();
            _sharedSettingsAppliedEvent = null;
        }

        private void StartSettingsUpdateRequestNotification()
        {
            if (_settingsUpdateRequestedEvent != null) return;

            _settingsUpdateRequestEventName = $"TinyBar.UpdateRequest.{Environment.ProcessId}";
            _settingsUpdateRequestedEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                _settingsUpdateRequestEventName);
            _settingsUpdateRequestedWait = ThreadPool.RegisterWaitForSingleObject(
                _settingsUpdateRequestedEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(isStartupCheck: false))),
                null,
                Timeout.Infinite,
                false);
        }

        private void StopSettingsUpdateRequestNotification()
        {
            _settingsUpdateRequestedWait?.Unregister(null);
            _settingsUpdateRequestedWait = null;
            _settingsUpdateRequestedEvent?.Dispose();
            _settingsUpdateRequestedEvent = null;
            _settingsUpdateRequestEventName = null;
        }

        private static void DisposeSettingsApplyNotification(EventWaitHandle? settingsAppliedEvent, RegisteredWaitHandle? settingsAppliedWait)
        {
            settingsAppliedWait?.Unregister(null);
            settingsAppliedEvent?.Dispose();
        }

        private void ManageTaskbarPerformance()
        {
            List<string> selectedMetrics = TaskbarPerformanceMetricCatalog.Normalize(_settings.TaskbarPerformanceMetrics);
            bool enabled = _settings.EnableTaskbarPerformanceMonitor && selectedMetrics.Count > 0;
            if (!enabled)
            {
                _taskbarPerformanceWindow?.Dispose();
                _taskbarPerformanceWindow = null;
                return;
            }

            if (_taskbarPerformanceWindow == null)
            {
                _taskbarPerformanceWindow = new TaskbarPerformanceWindow();
                _taskbarPerformanceWindow.SettingsRequested += (_, _) => OpenSettings(TaskbarPerformanceSettingsPage);
            }
            _taskbarPerformanceWindow.ApplySettings(
                _settings,
                GetPerformanceAnchorLeft());
        }

        private void ManageTaskbarTranslateButton()
        {
            if (!_settings.EnableTaskbarTranslateButton)
            {
                CloseQuickTranslatePopup();
                _taskbarTranslateButtonWindow?.Dispose();
                _taskbarTranslateButtonWindow = null;
                return;
            }

            if (_taskbarTranslateButtonWindow == null)
            {
                _taskbarTranslateButtonWindow = new TaskbarTranslateButtonWindow();
                _taskbarTranslateButtonWindow.TranslateRequested += (_, _) => ShowQuickTranslate();
                _taskbarTranslateButtonWindow.SettingsRequested += (_, _) => OpenSettings(QuickTranslateSettingsPage);
            }

            _taskbarTranslateButtonWindow.ApplySettings(_settings);
        }

        private void ManageWaterReminder()
        {
            if (!_settings.EnableWaterReminder)
            {
                _waterReminderTimer?.Stop();
                _waterReminderWasDue = false;
                _waterReminderPopup?.Dispose();
                _waterReminderPopup = null;
                _taskbarWaterReminderWindow?.Dispose();
                _taskbarWaterReminderWindow = null;
                return;
            }

            if (_taskbarWaterReminderWindow == null)
            {
                _taskbarWaterReminderWindow = new TaskbarWaterReminderWindow();
                _taskbarWaterReminderWindow.DrinkRequested += (_, _) => RecordWaterDrink();
                _taskbarWaterReminderWindow.SnoozeRequested += (_, _) => SnoozeWaterReminder();
                _taskbarWaterReminderWindow.SettingsRequested += (_, _) => OpenSettings(WaterReminderSettingsPage);
            }

            _taskbarWaterReminderWindow.ApplySettings(_settings);
            if (_waterReminderTimer == null)
            {
                _waterReminderTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                _waterReminderTimer.Tick += (_, _) => UpdateWaterReminder();
            }

            _waterReminderTimer.Start();
            UpdateWaterReminder();
        }

        private void ApplyTaskbarCustomization()
        {
            IntPtr taskbar = TaskbarMonitorLocator.FindTaskbarWindow(_settings.TaskbarMonitorDeviceName);
            if (taskbar == IntPtr.Zero) return;

            bool skipCenteringPass = false;

            if (_settings.ApplyTaskbarAcrylic)
            {
                // Re-assert on every pass: Explorer rebuilds its accent policy when windows
                // are added or removed, which silently drops our override.
                TaskbarAppearance.ApplyWhiteAcrylic(taskbar);
                _acrylicApplied = true;
            }
            else if (_acrylicApplied)
            {
                // Only restore once, on the transition from on to off. Toggling the system
                // transparency flag repeatedly would flicker the whole shell.
                _acrylicApplied = false;
                TaskbarAppearance.ClearAcrylic(taskbar);

                // The transparency toggle makes Explorer rebuild the task list, which snaps
                // it back to the left edge. Re-centre once the shell settles; polling here
                // covers the rebuild finishing after ClearAcrylic returns.
                if (_settings.CenterTaskbarIcons)
                {
                    // Stop the slow background poller first: while Explorer is rebuilding it
                    // would compute bogus bounds and fight the corrector below.
                    StopTaskbarCenteringTimer();
                    RecentreAfterShellRebuild();
                    skipCenteringPass = true;
                }
            }

            _lastAcrylicTaskbar = taskbar;

            if (skipCenteringPass && _settings.CenterTaskbarIcons)
            {
                // The background corrector owns the layout until the shell settles; running
                // the normal path here would restart the poller and fight it.
                return;
            }

            ManageTaskbarCentering(taskbar);
        }

        private void ManageTaskbarCentering(IntPtr taskbar)
        {
            if (!_settings.CenterTaskbarIcons)
            {
                if (_centeringApplied && _centeredTaskSwitcher != IntPtr.Zero)
                {
                    TaskbarCentering.RestoreDefaultLayout(taskbar, _centeredTaskSwitcher);
                }

                _centeringApplied = false;
                _centeredTaskSwitcher = IntPtr.Zero;
                StopTaskbarCenteringTimer();
                return;
            }

            ApplyTaskbarCentering(taskbar);
            StartTaskbarCenteringTimer();
        }

        private void ApplyTaskbarCentering(IntPtr taskbar)
        {
            IntPtr switcher = TaskbarCentering.FindTaskSwitcherWindow(taskbar);
            IntPtr list = TaskbarCentering.FindTaskListWindow(taskbar);
            if (switcher == IntPtr.Zero) return;

            var bounds = TaskbarCentering.ComputeCenteredBounds(taskbar, switcher, list);
            if (bounds == null) return;

            if (TaskbarCentering.ApplyCenteredLayout(bounds.Value, switcher))
            {
                _centeredTaskSwitcher = switcher;
                _centeringApplied = true;
                _lastCenteredBounds = bounds;
            }
        }

        /// <summary>
        /// Explorer rebuilds the task list after the system transparency flag is toggled,
        /// resetting it to the left edge. The rebuild blocks the UI thread, so a
        /// DispatcherTimer cannot correct the layout in time. Instead, wait on a background
        /// thread until the shell has settled, then restore the cached centre position.
        /// </summary>
        private void RecentreAfterShellRebuild()
        {
            var target = _lastCenteredBounds;
            _recentringAfterRebuild = false;

            if (target == null) return;

            string? deviceName = _settings.TaskbarMonitorDeviceName;
            var deadline = DateTime.UtcNow.AddSeconds(4);

            _ = Task.Run(async () =>
            {
                // Explorer keeps re-applying the left-aligned full-width layout for roughly
                // 900ms after the toggle; corrections issued inside that window are simply
                // overwritten. Wait for it to stop, then take the layout back in one shot.
                await Task.Delay(900).ConfigureAwait(false);

                while (DateTime.UtcNow < deadline)
                {
                    IntPtr taskbar = TaskbarMonitorLocator.FindTaskbarWindow(deviceName);
                    IntPtr switcher = taskbar == IntPtr.Zero
                        ? IntPtr.Zero
                        : TaskbarCentering.FindTaskSwitcherWindow(taskbar);

                    if (switcher != IntPtr.Zero &&
                        UnmanagedMethods.GetWindowRect(switcher, out UnmanagedMethods.RECT current))
                    {
                        // Explorer resets BOTH position and width while rebuilding. Restore
                        // the full geometry (not just Left) or the next re-layout pass will
                        // push the list back to the left edge again.
                        int currentWidth = current.Right - current.Left;
                        bool wrongLeft = Math.Abs(current.Left - target.Value.Left) > 2;
                        bool wrongWidth = Math.Abs(currentWidth - target.Value.Width) > 2;
                        if (wrongLeft || wrongWidth)
                        {
                            TaskbarCentering.ApplyCenteredLayout(target.Value, switcher);
                        }
                    }

                    await Task.Delay(30).ConfigureAwait(false);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_settings.CenterTaskbarIcons) return;
                    // Final pass with a fresh measurement now that the shell is stable, so
                    // the resting position matches the buttons actually on screen.
                    IntPtr finalTaskbar = TaskbarMonitorLocator.FindTaskbarWindow(deviceName);
                    if (finalTaskbar != IntPtr.Zero) ApplyTaskbarCentering(finalTaskbar);
                    StartTaskbarCenteringTimer();
                }));
            });
        }

        private void StartTaskbarCenteringTimer()
        {
            if (_taskbarCenteringTimer != null) return;

            // Explorer re-lays-out the task list whenever buttons are added or removed,
            // which would otherwise snap the icons back to the left edge.
            _taskbarCenteringTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _taskbarCenteringTimer.Tick += (_, _) =>
            {
                if (!_settings.CenterTaskbarIcons) return;
                IntPtr taskbar = TaskbarMonitorLocator.FindTaskbarWindow(_settings.TaskbarMonitorDeviceName);
                if (taskbar != IntPtr.Zero) ApplyTaskbarCentering(taskbar);
            };
            _taskbarCenteringTimer.Start();
        }

        private void StopTaskbarCenteringTimer()
        {
            _taskbarCenteringTimer?.Stop();
            _taskbarCenteringTimer = null;
        }

        private void RestoreTaskbarOnExit()
        {
            StopTaskbarCenteringTimer();

            IntPtr taskbar = TaskbarMonitorLocator.FindTaskbarWindow(_settings.TaskbarMonitorDeviceName);
            if (taskbar == IntPtr.Zero) return;

            if (_centeringApplied && _centeredTaskSwitcher != IntPtr.Zero)
            {
                TaskbarCentering.RestoreDefaultLayout(taskbar, _centeredTaskSwitcher);
                _centeringApplied = false;
            }

            if (_acrylicApplied)
            {
                TaskbarAppearance.ClearAcrylic(taskbar);
                _acrylicApplied = false;
            }
        }

        private void UpdateWaterReminder()
        {
            if (!_settings.EnableWaterReminder || _taskbarWaterReminderWindow == null) return;

            string recordDate = _settings.WaterReminderRecordDate;
            int completedToday = _settings.WaterReminderCompletedToday;
            DateTime? lastCompletedAt = _settings.WaterReminderLastCompletedAt;
            DateTime? snoozedUntil = _settings.WaterReminderSnoozedUntil;
            WaterReminderStatus status = WaterReminderSchedule.GetStatus(_settings, DateTime.Now);
            if (recordDate != _settings.WaterReminderRecordDate ||
                completedToday != _settings.WaterReminderCompletedToday ||
                lastCompletedAt != _settings.WaterReminderLastCompletedAt ||
                snoozedUntil != _settings.WaterReminderSnoozedUntil)
            {
                _settings.Save();
            }

            _taskbarWaterReminderWindow.Update(status);
            _waterReminderPopup?.ApplyTheme(ApplicationThemeParser.Resolve(_settings.ApplicationTheme));
            bool shouldNotify = status.IsDue && !_waterReminderWasDue;
            if (shouldNotify && _settings.WaterReminderHideInFullscreen && IsFullscreenAppRunning())
            {
                // Defer while a fullscreen app is active; retried on the next tick.
                return;
            }
            _waterReminderWasDue = status.IsDue;
            if (!shouldNotify) return;

            ShowWaterReminderPopup(status);
            if (_settings.WaterReminderShowSystemNotification && _notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "喝水助手",
                    "该喝水了。",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void ShowWaterReminderPopup(WaterReminderStatus status)
        {
            if (_taskbarWaterReminderWindow == null) return;

            if (_waterReminderPopup == null)
            {
                _waterReminderPopup = new WaterReminderPopupWindow();
                _waterReminderPopup.DrinkRequested += (_, _) => RecordWaterDrink();
                _waterReminderPopup.SnoozeRequested += (_, _) => SnoozeWaterReminder();
            }

            _waterReminderPopup.ApplyTheme(ApplicationThemeParser.Resolve(_settings.ApplicationTheme));
            _waterReminderPopup.ShowAbove(_taskbarWaterReminderWindow, status);
        }

        private bool IsFullscreenAppRunning()
        {
            IntPtr foreground = UnmanagedMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            // The desktop icon list (SysListView32) and taskbar buttons are child windows of
            // Progman/WorkerW/Shell_TrayWnd. Resolve to the top-level window first so that
            // focusing the desktop is not mistaken for a fullscreen app.
            IntPtr root = UnmanagedMethods.GetAncestor(foreground, UnmanagedMethods.GA_ROOT);
            if (root == IntPtr.Zero) root = foreground;

            UnmanagedMethods.GetWindowThreadProcessId(root, out uint processId);
            if (processId == Environment.ProcessId) return false;

            IntPtr desktopWindow = UnmanagedMethods.GetDesktopWindow();
            IntPtr shellWindow = UnmanagedMethods.GetShellWindow();
            if (root == desktopWindow || root == shellWindow) return false;

            System.Text.StringBuilder className = new(256);
            if (UnmanagedMethods.GetClassName(root, className, className.Capacity) > 0)
            {
                string name = className.ToString();
                if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd")
                {
                    return false;
                }
            }

            if (!UnmanagedMethods.IsWindowVisible(root)) return false;
            if (UnmanagedMethods.IsIconic(root)) return false;
            if (!UnmanagedMethods.GetWindowRect(root, out UnmanagedMethods.RECT windowRect)) return false;

            int width = windowRect.Right - windowRect.Left;
            int height = windowRect.Bottom - windowRect.Top;
            if (width <= 0 || height <= 0) return false;

            // Use the monitor that contains the foreground window.
            UnmanagedMethods.POINT center = new()
            {
                X = windowRect.Left + width / 2,
                Y = windowRect.Top + height / 2
            };
            IntPtr monitor = UnmanagedMethods.MonitorFromPoint(center, UnmanagedMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            UnmanagedMethods.MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<UnmanagedMethods.MONITORINFO>() };
            if (!UnmanagedMethods.GetMonitorInfo(monitor, ref mi)) return false;

            // Fullscreen: the window covers the entire monitor, including the taskbar area.
            const int tolerance = 2;
            return windowRect.Left <= mi.rcMonitor.Left + tolerance &&
                   windowRect.Top <= mi.rcMonitor.Top + tolerance &&
                   windowRect.Right >= mi.rcMonitor.Right - tolerance &&
                   windowRect.Bottom >= mi.rcMonitor.Bottom - tolerance;
        }

        private void RecordWaterDrink()
        {
            WaterReminderSchedule.RecordDrink(_settings, DateTime.Now);
            _settings.Save();
            _waterReminderPopup?.Hide();
            _waterReminderWasDue = false;
            UpdateWaterReminder();
            _taskbarWaterReminderWindow?.ShowDrinkRecordedFeedback();
        }

        private void SnoozeWaterReminder()
        {
            WaterReminderSchedule.Snooze(_settings, DateTime.Now);
            _settings.Save();
            _waterReminderPopup?.Hide();
            _waterReminderWasDue = false;
            UpdateWaterReminder();
        }

        private int GetPerformanceAnchorLeft()
        {
            return _currentX;
        }

        // ... (Media Events kept as is, not shown here to avoid huge replacement) ...

        
        private void InjectIntoTaskbar()
        {
            try 
            {
                var helper = new WindowInteropHelper(this);
                IntPtr hWnd = helper.Handle;
                if (hWnd == IntPtr.Zero) return;

                // 1. Find the Taskbar
                IntPtr taskbarWnd = TaskbarMonitorLocator.FindTaskbarWindow(_settings.TaskbarMonitorDeviceName);
                if (taskbarWnd == IntPtr.Zero) return;

                // Check if we are already child
                IntPtr currentParent = UnmanagedMethods.GetParent(hWnd);
                if (currentParent != taskbarWnd)
                {
                    // 2. Modify Window Style to be a Child window
                    int style = UnmanagedMethods.GetWindowLong(hWnd, UnmanagedMethods.GWL_STYLE);
                    style = (style & ~(-2147483648)); // Remove Popup
                    style |= UnmanagedMethods.WS_CHILD; 
                    style |= UnmanagedMethods.WS_VISIBLE;

                    UnmanagedMethods.SetWindowLong(hWnd, UnmanagedMethods.GWL_STYLE, style);

                    // 3. Set the Taskbar as the parent
                    IntPtr result = UnmanagedMethods.SetParent(hWnd, taskbarWnd);
                    if (result == IntPtr.Zero)
                    {
                        // Log?
                        // int err = Marshal.GetLastWin32Error();
                        return;
                    }
                }

                // 4. Position the window
                UnmanagedMethods.RECT rectTaskbar;
                UnmanagedMethods.GetWindowRect(taskbarWnd, out rectTaskbar);
                int tbWidth = rectTaskbar.Right - rectTaskbar.Left;
                int tbHeight = rectTaskbar.Bottom - rectTaskbar.Top;
                
                int width = (int)_settings.Width;
                int height = tbHeight;
                int xPos = 0;

                // Always anchor against the tray area; drag handle updates OffsetX.
                IntPtr trayNotifyWnd = UnmanagedMethods.FindWindowEx(taskbarWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotifyWnd != IntPtr.Zero)
                {
                    UnmanagedMethods.RECT rectTray;
                    UnmanagedMethods.GetWindowRect(trayNotifyWnd, out rectTray);
                    
                    // Tray Left (Screen) - Taskbar Left (Screen) - My Width - Offset
                    xPos = (rectTray.Left - rectTaskbar.Left) - width - _settings.OffsetX;
                }
                else
                {
                    // Fallback
                    xPos = tbWidth - width - _settings.OffsetX - 10;
                }

                // Boundary Checks
                if (xPos < 0) xPos = 0;
                // if (xPos + width > tbWidth) xPos = tbWidth - width; // Optional constraint

                UnmanagedMethods.MoveWindow(hWnd, xPos, 0, width, height, true);
                
                // InfoText.Text = "Attached"; // Don't overwrite lyrics during update
                _currentX = xPos;
                _taskbarPerformanceWindow?.UpdateLyricsPosition(GetPerformanceAnchorLeft());
            }
            catch (Exception)
            {
                // Silent fail to avoid crash loop
            }
        }

        private int _currentX;

        // Playback status update
        private void MediaManager_PlaybackStatusChanged(object? sender, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            _isMediaPlaying = (status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            Dispatcher.Invoke(() => UpdatePlayPauseButton(status));
        }

        private void UpdatePlayPauseButton(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            bool isPlaying = status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _floatingWindow?.SetPlaybackState(isPlaying);
            _desktopWidget?.SetPlaybackState(isPlaying);

            string glyph = isPlaying ? "\ue12e" : "\ue13c";
            string tooltip = isPlaying ? "暂停" : "播放";

            if (PlayPauseButton != null)
            {
                PlayPauseButton.Content = glyph;
                PlayPauseButton.ToolTip = tooltip;
            }

            if (HoverPlayPauseButton != null)
            {
                HoverPlayPauseButton.Content = glyph;
                HoverPlayPauseButton.ToolTip = tooltip;
            }
        }

        private void InitializeAudioDeviceControls()
        {
            _audioDeviceService.VolumeChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(() => UpdateVolumeUI(e.MasterVolume, e.IsMuted));
            };
            _audioDeviceService.DefaultDeviceChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(RefreshAudioDevicesList);
            };

            float currentVol = _audioDeviceService.GetMasterVolume();
            bool isMuted = _audioDeviceService.IsMuted();
            UpdateVolumeUI(currentVol, isMuted);
        }

        private void UpdateVolumeUI(float volume, bool isMuted)
        {
            if (float.IsNaN(volume) || float.IsInfinity(volume))
            {
                volume = 0.5f;
            }
            volume = Math.Clamp(volume, 0f, 1f);
            int percent = Math.Clamp((int)Math.Round(volume * 100f), 0, 100);

            if (VolumePercentText != null)
            {
                VolumePercentText.Text = isMuted ? "静音" : $"{percent}%";
            }

            if (VolumeSlider != null)
            {
                if (!VolumeSlider.IsMouseCaptureWithin && Math.Abs(VolumeSlider.Value - percent) >= 1)
                {
                    _isUpdatingVolumeSliderProgrammatically = true;
                    VolumeSlider.Value = percent;
                    _isUpdatingVolumeSliderProgrammatically = false;
                }
            }

            string icon = GetVolumeGlyph(volume, isMuted);
            if (HoverVolumeButton != null)
            {
                HoverVolumeButton.Content = icon;
                HoverVolumeButton.ToolTip = isMuted ? "已静音 (点击/滚轮调节)" : $"音量: {percent}% (滚轮调节)";
            }
            if (VolumeMuteToggleButton != null)
            {
                VolumeMuteToggleButton.Content = isMuted ? "\ue1ac" : "\ue1ab";
                VolumeMuteToggleButton.ToolTip = isMuted ? "取消静音" : "静音";
            }

            // The slider now mirrors the real device volume, so later ValueChanged events
            // genuinely come from the user and are safe to write back.
            _volumeUiReady = true;
        }

        private static string GetVolumeGlyph(float volume, bool isMuted)
        {
            if (isMuted || volume <= 0.001f) return "\ue1ac"; // Lucide volume-x
            if (volume < 0.40f) return "\ue1aa"; // Lucide volume-1
            return "\ue1ab"; // Lucide volume-2
        }

        private static bool IsPointInElementPhysicalBounds(FrameworkElement element, UnmanagedMethods.POINT pt, double margin = 20)
        {
            try
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                    return false;

                Point p1 = element.PointToScreen(new Point(0, 0));
                Point p2 = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                double left = Math.Min(p1.X, p2.X) - margin;
                double right = Math.Max(p1.X, p2.X) + margin;
                double top = Math.Min(p1.Y, p2.Y) - margin;
                double bottom = Math.Max(p1.Y, p2.Y) + margin;

                return pt.X >= left && pt.X <= right && pt.Y >= top && pt.Y <= bottom;
            }
            catch
            {
                // In case of transient rendering during opening, keep active
                return true;
            }
        }

        private bool IsCursorOverLyricsComponentOrPopups()
        {
            if (!UnmanagedMethods.GetCursorPos(out var pt)) return false;

            // 1. Check the entire taskbar lyrics window
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero && UnmanagedMethods.GetWindowRect(helper.Handle, out var winRect))
            {
                // Generous 16px margin in all directions to cover edges, grips, and transitions
                if (pt.X >= winRect.Left - 16 && pt.X <= winRect.Right + 16 &&
                    pt.Y >= winRect.Top - 16 && pt.Y <= winRect.Bottom + 16)
                {
                    return true;
                }
            }

            // 2. Check VolumePopup
            if (VolumePopup != null && VolumePopup.IsOpen && VolumePopup.Child is FrameworkElement volChild)
            {
                if (IsPointInElementPhysicalBounds(volChild, pt, margin: 24))
                {
                    return true;
                }
            }

            // 3. Check AudioDevicePopup
            if (AudioDevicePopup != null && AudioDevicePopup.IsOpen && AudioDevicePopup.Child is FrameworkElement devChild)
            {
                if (IsPointInElementPhysicalBounds(devChild, pt, margin: 24))
                {
                    return true;
                }
            }

            return false;
        }

        private void HoverWatchTimer_Tick(object? sender, EventArgs e)
        {
            if (!_settings.EnableTaskbarHoverMediaControls)
            {
                HideHoverMediaControls();
                return;
            }

            if (!IsCursorOverLyricsComponentOrPopups())
            {
                HideHoverMediaControls();
            }
        }

        private void ShowHoverMediaControls()
        {
            if (HoverMediaControlsBar == null || TextContainer == null) return;

            HoverMediaControlsBar.Visibility = Visibility.Visible;
            HoverMediaControlsBar.Opacity = 1;
            HoverMediaControlsBar.IsHitTestVisible = true;
            TextContainer.Opacity = 0;

            float vol = _audioDeviceService.GetMasterVolume();
            bool muted = _audioDeviceService.IsMuted();
            UpdateVolumeUI(vol, muted);

            bool isPlaying = _mediaManager.IsPlaying;
            if (HoverPlayPauseButton != null)
            {
                HoverPlayPauseButton.Content = isPlaying ? "\ue12e" : "\ue13c";
                HoverPlayPauseButton.ToolTip = isPlaying ? "暂停" : "播放";
            }

            if (_hoverWatchTimer != null && !_hoverWatchTimer.IsEnabled)
            {
                _hoverWatchTimer.Start();
            }
        }

        private void HideHoverMediaControls()
        {
            if (VolumePopup != null) VolumePopup.IsOpen = false;
            if (AudioDevicePopup != null) AudioDevicePopup.IsOpen = false;
            if (HoverMediaControlsBar == null || TextContainer == null) return;

            HoverMediaControlsBar.Opacity = 0;
            HoverMediaControlsBar.IsHitTestVisible = false;
            HoverMediaControlsBar.Visibility = Visibility.Collapsed;
            TextContainer.Opacity = 1;

            _hoverWatchTimer?.Stop();
        }

        private void MainBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_settings.EnableTaskbarHoverMediaControls) return;
            ShowHoverMediaControls();
        }

        private void MainBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // If a popup is open (e.g. user is moving cursor upward into the volume/device popup), don't abruptly close
            if (VolumePopup?.IsOpen == true || AudioDevicePopup?.IsOpen == true)
            {
                return;
            }

            if (!IsCursorOverLyricsComponentOrPopups())
            {
                HideHoverMediaControls();
            }
        }

        private void HoverVolumeButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (VolumePopup == null) return;
            if (AudioDevicePopup != null) AudioDevicePopup.IsOpen = false;
            if (!VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = true;
                float vol = _audioDeviceService.GetMasterVolume();
                bool muted = _audioDeviceService.IsMuted();
                UpdateVolumeUI(vol, muted);
            }
        }

        private void HoverVolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (VolumePopup == null) return;
            if (AudioDevicePopup != null) AudioDevicePopup.IsOpen = false;
            VolumePopup.IsOpen = !VolumePopup.IsOpen;
            if (VolumePopup.IsOpen)
            {
                float vol = _audioDeviceService.GetMasterVolume();
                bool muted = _audioDeviceService.IsMuted();
                UpdateVolumeUI(vol, muted);
            }
        }

        private void HoverMediaPlaybackButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (VolumePopup != null && VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
            }
            if (AudioDevicePopup != null && AudioDevicePopup.IsOpen)
            {
                AudioDevicePopup.IsOpen = false;
            }
        }

        private void HoverAudioDeviceButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (VolumePopup != null && VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
            }
        }

        private void HoverVolumeButton_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;
            AdjustVolume(e.Delta > 0 ? 0.02f : -0.02f);
        }

        private void HoverVolumeButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Middle)
            {
                e.Handled = true;
                bool wasMuted = _audioDeviceService.IsMuted();
                float vol = _audioDeviceService.GetMasterVolume();
                if (wasMuted)
                {
                    if (vol <= 0.005f)
                    {
                        vol = 0.20f;
                        _audioDeviceService.SetMasterVolume(vol);
                    }
                    _audioDeviceService.SetMute(false);
                    UpdateVolumeUI(vol, false);
                }
                else
                {
                    _audioDeviceService.SetMute(true);
                    UpdateVolumeUI(vol, true);
                }
            }
        }

        private void VolumePopup_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;
            AdjustVolume(e.Delta > 0 ? 0.02f : -0.02f);
        }

        private void AdjustVolume(float delta)
        {
            float current = _audioDeviceService.GetMasterVolume();
            float target = Math.Clamp(current + delta, 0f, 1f);
            _audioDeviceService.SetMasterVolume(target);

            // Automatically unmute when user scrolls volume to audible level
            if (_audioDeviceService.IsMuted() && target > 0.005f)
            {
                _audioDeviceService.SetMute(false);
            }
            UpdateVolumeUI(target, _audioDeviceService.IsMuted());
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingVolumeSliderProgrammatically) return;
            if (double.IsNaN(e.NewValue) || double.IsInfinity(e.NewValue)) return;

            // The slider starts at its XAML default and fires ValueChanged before the real
            // system volume has been read. Writing back at that point would silently change
            // the user's volume on every launch, so ignore events until the UI is synced.
            if (!_volumeUiReady) return;

            float level = (float)(Math.Clamp(e.NewValue, 0.0, 100.0) / 100.0);
            _audioDeviceService.SetMasterVolume(level);

            // If user adjusts slider to > 0 while currently muted, automatically unmute!
            if (level > 0.005f && _audioDeviceService.IsMuted())
            {
                _audioDeviceService.SetMute(false);
            }

            int percent = (int)Math.Round(level * 100f);
            bool isMuted = _audioDeviceService.IsMuted() || level <= 0.005f;

            if (VolumePercentText != null)
            {
                VolumePercentText.Text = _audioDeviceService.IsMuted() ? "静音" : $"{percent}%";
            }
            string icon = GetVolumeGlyph(level, isMuted);
            if (HoverVolumeButton != null)
            {
                HoverVolumeButton.Content = icon;
                HoverVolumeButton.ToolTip = _audioDeviceService.IsMuted() ? "已静音 (点击/滚轮调节 / 中键静音)" : $"音量: {percent}% (滚轮调节)";
            }
            if (VolumeMuteToggleButton != null)
            {
                VolumeMuteToggleButton.Content = _audioDeviceService.IsMuted() ? "\ue1ac" : "\ue1ab";
                VolumeMuteToggleButton.ToolTip = _audioDeviceService.IsMuted() ? "取消静音" : "静音";
            }
        }

        private void VolumeMuteToggle_Click(object sender, RoutedEventArgs e)
        {
            bool wasMuted = _audioDeviceService.IsMuted();
            float vol = _audioDeviceService.GetMasterVolume();
            if (wasMuted)
            {
                // Unmuting: if volume was 0%, restore to a comfortable audible level (20%)
                if (vol <= 0.005f)
                {
                    vol = 0.20f;
                    _audioDeviceService.SetMasterVolume(vol);
                }
                _audioDeviceService.SetMute(false);
                UpdateVolumeUI(vol, false);
            }
            else
            {
                // Muting
                _audioDeviceService.SetMute(true);
                UpdateVolumeUI(vol, true);
            }
        }

        private void VolumePopup_Closed(object? sender, EventArgs e)
        {
            if (!IsCursorOverLyricsComponentOrPopups())
            {
                HideHoverMediaControls();
            }
        }

        private void HoverAudioDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (AudioDevicePopup == null) return;
            if (VolumePopup != null) VolumePopup.IsOpen = false;
            AudioDevicePopup.IsOpen = !AudioDevicePopup.IsOpen;
            if (AudioDevicePopup.IsOpen)
            {
                RefreshAudioDevicesList();
            }
        }

        private void RefreshAudioDevicesList()
        {
            if (AudioDevicesItemsControl == null) return;
            var devices = _audioDeviceService.GetPlaybackDevices();
            AudioDevicesItemsControl.ItemsSource = devices;
        }

        private DispatcherTimer? _audioDeviceToastTimer;

        private void ShowAudioDeviceSwitchedToast(string deviceName, string iconGlyph)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AudioDeviceSwitchToast == null || AudioDeviceToastText == null || AudioDeviceToastIcon == null) return;

                AudioDeviceToastIcon.Text = iconGlyph;
                AudioDeviceToastText.Text = $"已切换到：{deviceName}";
                AudioDeviceSwitchToast.IsOpen = true;

                if (_audioDeviceToastTimer == null)
                {
                    _audioDeviceToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
                    _audioDeviceToastTimer.Tick += (_, _) =>
                    {
                        _audioDeviceToastTimer.Stop();
                        if (AudioDeviceSwitchToast != null)
                        {
                            AudioDeviceSwitchToast.IsOpen = false;
                        }
                    };
                }
                else
                {
                    _audioDeviceToastTimer.Stop();
                }
                _audioDeviceToastTimer.Start();
            }));
        }

        private async void DeviceRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is AudioPlaybackDevice device)
            {
                if (AudioDevicePopup != null) AudioDevicePopup.IsOpen = false;
                bool success = await Task.Run(() => _audioDeviceService.SetDefaultPlaybackDevice(device.Id));
                RefreshAudioDevicesList();
                if (success)
                {
                    ShowAudioDeviceSwitchedToast(device.Name, device.IconGlyph);
                }
            }
        }

        private void AudioDevicePopup_Closed(object? sender, EventArgs e)
        {
            if (!IsCursorOverLyricsComponentOrPopups())
            {
                HideHoverMediaControls();
            }
        }

        // Media control event handlers
        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.PlayPauseAsync();
        }

        private async void NextTrack_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.NextTrackAsync();
        }

        private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.PreviousTrackAsync();
        }

        // Lyric offset adjustment
        private void LyricsContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            UpdateLyricOffsetMenuText();
        }

        private void LyricOffsetDecrease_Click(object sender, RoutedEventArgs e)
        {
            AdjustLyricOffset(-0.5);
        }

        private void LyricOffsetIncrease_Click(object sender, RoutedEventArgs e)
        {
            AdjustLyricOffset(0.5);
        }

        private void AdjustLyricOffset(double deltaSeconds)
        {
            double newOffset = Math.Clamp(Math.Round(_settings.LyricOffsetSeconds + deltaSeconds, 1), -10.0, 10.0);
            if (Math.Abs(newOffset - _settings.LyricOffsetSeconds) < 0.01) return;

            _settings.LyricOffsetSeconds = newOffset;
            _settings.Save();
            UpdateLyricOffsetMenuText();

            // 立即按新偏移刷新当前歌词行
            _lastCurrentLyric = "";
            _lastLyricText = "";
            if (_hasLyrics)
            {
                UpdateLyricsUI(_lastMediaPosition);
            }
        }

        private void UpdateLyricOffsetMenuText()
        {
            if (LyricOffsetText == null) return;

            double offset = _settings.LyricOffsetSeconds;
            string sign = offset > 0 ? "+" : "";
            LyricOffsetText.Text = $"{sign}{offset:0.0} 秒";
        }

        private void OpenAppFilter_Click(object sender, RoutedEventArgs e)
        {
             var win = new AppFilterWindow(_settings, _mediaManager);
             if (win.ShowDialog() == true)
             {
                 _mediaManager.FilterAppIds = _settings.IncludedAppIds; // Update Filter
                 _mediaManager.RefreshSession(); // Apply immediately
             }
        }

        private void FloatingLyrics_Checked(object sender, RoutedEventArgs e)
        {
            _settings.EnableFloatingLyrics = true;
            _settings.Save();
            ManageFloatingWindow();
        }

        private void FloatingLyrics_Unchecked(object sender, RoutedEventArgs e)
        {
            _settings.EnableFloatingLyrics = false;
            _settings.Save();
            ManageFloatingWindow();
        }

        private void ManageFloatingWindow()
        {
            if (_settings.EnableFloatingLyrics)
            {
                if (_floatingWindow != null && _floatingWindow.IsAcrylicMode != _settings.FloatingLyricsUseAcrylic)
                {
                    _floatingWindow.CloseRequested -= FloatingWindow_CloseRequested;
                    _floatingWindow.SettingsRequested -= FloatingWindow_SettingsRequested;
                    _floatingWindow.PreviousTrackRequested -= FloatingWindow_PreviousTrackRequested;
                    _floatingWindow.PlayPauseRequested -= FloatingWindow_PlayPauseRequested;
                    _floatingWindow.NextTrackRequested -= FloatingWindow_NextTrackRequested;
                    _floatingWindow.Close();
                    _floatingWindow = null;
                }

                if (_floatingWindow == null)
                {
                    _floatingWindow = new FloatingLyricsWindow(_settings);
                    _floatingWindow.CloseRequested += FloatingWindow_CloseRequested;
                    _floatingWindow.SettingsRequested += FloatingWindow_SettingsRequested;
                    _floatingWindow.PreviousTrackRequested += FloatingWindow_PreviousTrackRequested;
                    _floatingWindow.PlayPauseRequested += FloatingWindow_PlayPauseRequested;
                    _floatingWindow.NextTrackRequested += FloatingWindow_NextTrackRequested;
                    _floatingWindow.Show();
                    // Apply CTX state explicitly if initialized late
                    _floatingWindow.SetClickThrough(_settings.FloatingLyricsClickThrough);
                    _floatingWindow.SetPlaybackState(_isMediaPlaying);
                }
                else
                {
                    _floatingWindow.Show();
                    // Ensure state
                    _floatingWindow.SetClickThrough(_settings.FloatingLyricsClickThrough);
                }
                
                if (FloatingLyricsCtxItem != null)
                {
                     FloatingLyricsCtxItem.IsEnabled = true;
                     FloatingLyricsCtxItem.Visibility = Visibility.Visible;
                }

                // Push current text if available
                if (!string.IsNullOrEmpty(_lastLyricText)) 
                {
                    string textToShow = _lastLyricText;
                    if (textToShow.Contains("|"))
                    {
                        textToShow = textToShow.Split('|')[0];
                    }
                    _floatingWindow.UpdateLyrics(textToShow);
                }
            }
            else
            {
                if (FloatingLyricsCtxItem != null)
                {
                     FloatingLyricsCtxItem.IsEnabled = false;
                     FloatingLyricsCtxItem.Visibility = Visibility.Collapsed;
                }
                _floatingWindow?.Hide();
            }
        }

        private void FloatingWindow_CloseRequested()
        {
            _settings.EnableFloatingLyrics = false;
            _settings.Save();

            if (FloatingLyricsMenuItem != null)
            {
                FloatingLyricsMenuItem.IsChecked = false;
            }

            ManageFloatingWindow();
        }

        private void FloatingWindow_SettingsRequested()
        {
            OpenSettings(2);
        }

        private Task FloatingWindow_PreviousTrackRequested() => _mediaManager.PreviousTrackAsync();

        private Task FloatingWindow_PlayPauseRequested() => _mediaManager.PlayPauseAsync();

        private Task FloatingWindow_NextTrackRequested() => _mediaManager.NextTrackAsync();

        private void ManageDesktopWidget()
        {
            if (_settings.EnableDesktopWidget)
            {
                if (_desktopWidget == null)
                {
                    _desktopWidget = new DesktopWidgetWindow(_settings);
                    _desktopWidget.CloseRequested += DesktopWidget_CloseRequested;
                    _desktopWidget.SettingsRequested += DesktopWidget_SettingsRequested;
                    _desktopWidget.PreviousTrackRequested += DesktopWidget_PreviousTrackRequested;
                    _desktopWidget.PlayPauseRequested += DesktopWidget_PlayPauseRequested;
                    _desktopWidget.NextTrackRequested += DesktopWidget_NextTrackRequested;
                    _desktopWidget.Show();
                    _desktopWidget.UpdateTrack(_currentTrackInfo);
                    _desktopWidget.SetPlaybackState(_isMediaPlaying);
                    _desktopWidget.UpdatePlayback(_lastMediaPosition, GetCurrentTrackDuration());

                    string initialLyric = !string.IsNullOrWhiteSpace(_lastCurrentLyric)
                        ? _lastCurrentLyric
                        : (_currentTrackInfo.HasTrack ? _currentTrackInfo.DisplayText : "等待播放");
                    _desktopWidget.UpdateLyrics(initialLyric);
                }
                else
                {
                    _desktopWidget.ApplySettings(_settings);
                    if (!_desktopWidget.IsVisible) _desktopWidget.Show();
                    _desktopWidget.EnsureDesktopAttachment();
                }

                EnsureDesktopHostTimer();
            }
            else
            {
                _desktopHostTimer?.Stop();
                if (_desktopWidget != null)
                {
                    _desktopWidget.CloseRequested -= DesktopWidget_CloseRequested;
                    _desktopWidget.SettingsRequested -= DesktopWidget_SettingsRequested;
                    _desktopWidget.PreviousTrackRequested -= DesktopWidget_PreviousTrackRequested;
                    _desktopWidget.PlayPauseRequested -= DesktopWidget_PlayPauseRequested;
                    _desktopWidget.NextTrackRequested -= DesktopWidget_NextTrackRequested;
                    _desktopWidget.Close();
                    _desktopWidget = null;
                }
            }
        }

        private void EnsureDesktopHostTimer()
        {
            if (_desktopHostTimer == null)
            {
                _desktopHostTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _desktopHostTimer.Tick += (_, _) => _desktopWidget?.RefreshDesktopAttachment();
            }
            _desktopHostTimer.Start();
        }

        private void DesktopWidget_CloseRequested()
        {
            _settings.EnableDesktopWidget = false;
            _settings.Save();
            ManageDesktopWidget();
        }

        private void DesktopWidget_SettingsRequested() => OpenSettings(4);
        private Task DesktopWidget_PreviousTrackRequested() => _mediaManager.PreviousTrackAsync();
        private Task DesktopWidget_PlayPauseRequested() => _mediaManager.PlayPauseAsync();
        private Task DesktopWidget_NextTrackRequested() => _mediaManager.NextTrackAsync();

        private TimeSpan GetCurrentTrackDuration()
        {
            return _currentTrackInfo.DurationMs is > 0
                ? TimeSpan.FromMilliseconds(_currentTrackInfo.DurationMs.Value)
                : TimeSpan.Zero;
        }

        private void FloatingLyricsCtx_Checked(object sender, RoutedEventArgs e)
        {
            _settings.FloatingLyricsClickThrough = true;
            _settings.Save();
            if (_floatingWindow != null) _floatingWindow.SetClickThrough(true);
        }

        private void FloatingLyricsCtx_Unchecked(object sender, RoutedEventArgs e)
        {
             _settings.FloatingLyricsClickThrough = false;
             _settings.Save();
             if (_floatingWindow != null) _floatingWindow.SetClickThrough(false);
        }





        private async void MediaManager_MediaInfoChanged(object? sender, MediaTrackInfo track)
        {
            _currentTrackInfo = track;
            Dispatcher.Invoke(() => _desktopWidget?.UpdateTrack(track));
            string artist = track.Artist.Trim();
            string title = track.Title.Trim();
            string displayText = track.DisplayText;

            if (!track.HasTrack)
            {
                _lyricsSearchCts?.Cancel();
                Interlocked.Increment(ref _lyricsSearchVersion);
                _currentArtist = "";
                _currentTitle = "";
                _currentTrackKey = "";
                _hasLyrics = false;
                _lastLyricText = "";
                _lastCurrentLyric = "";

                Dispatcher.Invoke(() =>
                {
                    TooltipTitleText.Text = "未知歌曲";
                    TooltipArtistText.Text = "未知艺术家";
                    MenuSongInfoText.Text = displayText;
                    MenuSongInfoText.ToolTip = displayText;
                    ShowStatusText(displayText);
                    _floatingWindow?.UpdateLyrics(displayText);
                });
                return;
            }

            string trackKey = $"{track.SourceAppId}\n{title}\n{artist}\n{track.Album}";
            if (string.Equals(trackKey, _currentTrackKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentTrackKey = trackKey;
            _currentArtist = artist;
            _currentTitle = title;
            _hasLyrics = false;
            _lastLyricText = "";
            _lastCurrentLyric = "";

            _lyricsSearchCts?.Cancel();
            _lyricsSearchCts?.Dispose();
            var searchCts = new CancellationTokenSource();
            _lyricsSearchCts = searchCts;
            int searchVersion = Interlocked.Increment(ref _lyricsSearchVersion);

            Dispatcher.Invoke(() =>
            {
                TooltipTitleText.Text = title;
                TooltipArtistText.Text = string.IsNullOrEmpty(artist) ? "未知艺术家" : artist;
                MenuSongInfoText.Text = displayText;
                MenuSongInfoText.ToolTip = displayText;
                ShowStatusText("正在搜索歌词…");
                _floatingWindow?.UpdateLyrics("正在搜索歌词…");
            });

            var result = await _lyricsEngine.SearchAndLoadLyricsAsync(track, searchCts.Token);
            if (searchCts.IsCancellationRequested || searchVersion != _lyricsSearchVersion)
            {
                return;
            }

            _hasLyrics = result.IsSuccess;
            Dispatcher.Invoke(() =>
            {
                if (_hasLyrics)
                {
                    ShowStatusText(displayText);
                    ApplySettings();
                    UpdateLyricsUI(_lastMediaPosition);
                }
                else
                {
                    string statusText = string.IsNullOrEmpty(result.StatusText)
                        ? "未找到歌词"
                        : result.StatusText;
                    ShowStatusText(statusText);
                    _floatingWindow?.UpdateLyrics(statusText);
                    ApplySettings();
                }
            });
        }

        private void MediaManager_PlaybackPositionChanged(object? sender, TimeSpan position)
        {
            // Sync anchor point
            _lastMediaPosition = position;
            _lastSyncTime = DateTime.Now;
            _desktopWidget?.UpdatePlayback(position, GetCurrentTrackDuration());

        }

        private void UpdateLyricsUI(TimeSpan position)
        {
            if (!_hasLyrics) return;

            bool wasShowingStatusText = _isShowingStatusText;
            _isShowingStatusText = false;

            // 切歌流程中 ApplySettings 末尾会因 _isShowingStatusText 仍为 true 而再次调用
            // ApplyStatusTextLayout，把双行第二行重置为隐藏且透明；离开状态文本时恢复双行布局。
            if (wasShowingStatusText && _settings.IsDoubleLine)
            {
                ApplyDoubleLineLayout();
            }

            var adjustedPosition = position - TimeSpan.FromSeconds(_settings.LyricOffsetSeconds);

            if (_settings.IsDoubleLine)
            {
                var (current, next) = _lyricsEngine.GetLyricsForTime(adjustedPosition);
                
                if (current != null)
                {
                    _activeLyricLine = current;
                    // Update Content
                    if (current.Text != _lastCurrentLyric)
                    {
                        _lastCurrentLyric = current.Text;
                        _lastLyricText = $"{current.Text}|{(next?.Text ?? "")}";
                        
                        UpdateSingleLineProgress(_mainLyricControl, current, adjustedPosition);
                        
                        _nextLyricControl.Text = next?.Text ?? "";
                        _nextLyricControl.Visibility = string.IsNullOrEmpty(_nextLyricControl.Text) ? Visibility.Collapsed : Visibility.Visible;
                    }
                    else if (current.HasSyllables)
                    {
                        UpdateSingleLineProgress(_mainLyricControl, current, adjustedPosition);
                    }
                }
            }
            else
            {
                // Single Line Mode
                var (current, _) = _lyricsEngine.GetLyricsForTime(adjustedPosition);
                if (current != null)
                {
                    _activeLyricLine = current;
                    if (current.Text != _lastLyricText)
                    {
                        _lastLyricText = current.Text;
                        UpdateSingleLineProgress(_mainLyricControl, current, adjustedPosition);
                    }
                    else if (current.HasSyllables)
                    {
                        UpdateSingleLineProgress(_mainLyricControl, current, adjustedPosition);
                    }
                }
            }
        }

        private void UpdateSingleLineProgress(TextBlock target, LyricsEngine.LyricLine line, TimeSpan time)
        {
            // Update Text if different
            if (target.Text != line.Text)
            {
                target.Text = line.Text;
                // Auto scroll if long - Disable infinite loop in double line mode to avoid control collision
                UpdateSingleLineScroll(target, line.Text, (line.EndMs - line.StartMs) / 1000.0, !_settings.IsDoubleLine, line.HasSyllables);
            }

            double progress = _lyricsEngine.GetLineProgress(line, time);

            ApplyMainLineProgress(target, line, progress);

            if (_settings.EnableFloatingLyrics && _floatingWindow != null && target == _mainLyricControl)
            {
                _floatingWindow.UpdateLyrics(line.Text);
                _floatingWindow.UpdateProgress(progress, line.HasSyllables);
            }

            if (target == _mainLyricControl)
            {
                _desktopWidget?.UpdateLyrics(line.Text);
            }
        }

        // 逐字行的渲染推进：渐变高亮 + 滚动跟随进度（逻辑轮询与渲染帧回调共用）
        private void ApplyMainLineProgress(TextBlock target, LyricsEngine.LyricLine line, double progress)
        {
            var brush = target.Foreground as LinearGradientBrush;
            if (brush != null && brush.GradientStops.Count >= 2)
            {
                if (line.HasSyllables)
                {
                    brush.GradientStops[0].Offset = progress;
                    brush.GradientStops[1].Offset = Math.Min(1.0, progress + 0.05);
                }
                else
                {
                    // 纯 LRC 行没有逐字时间：整行使用主色，避免残留上一个逐字行的渐变偏移
                    brush.GradientStops[0].Offset = 0;
                    brush.GradientStops[1].Offset = 0;
                }
            }

            // 滚动跟随逐字进度：唱到哪个字，可视区就滚到哪个字
            if (line.HasSyllables && _scrollableLyricDistance > 0)
            {
                Canvas.SetLeft(target, -progress * _scrollableLyricDistance);
            }
        }

        // 渲染帧回调：在行切换轮询（100ms）之间，把当前逐字行的高亮与滚动推进到与显示刷新同步
        private void OnRenderFrame(object? sender, EventArgs e)
        {
            if (!_hasLyrics || !_isMediaPlaying || _activeLyricLine == null || !_activeLyricLine.HasSyllables) return;

            TimeSpan estimatedPosition = _lastMediaPosition + (DateTime.Now - _lastSyncTime);
            TimeSpan adjusted = estimatedPosition - TimeSpan.FromSeconds(_settings.LyricOffsetSeconds);
            double progress = _lyricsEngine.GetLineProgress(_activeLyricLine, adjusted);
            ApplyMainLineProgress(_mainLyricControl, _activeLyricLine, progress);

            // 悬浮歌词跟随同一逐字进度，以渲染帧频率同步
            if (_settings.EnableFloatingLyrics && _floatingWindow != null)
            {
                _floatingWindow.UpdateProgress(progress, true);
            }
        }

        private void AnimateDoubleLyricTransition(string newCurrent, double currentDur, string newNext, double nextDur)
        {
             // Immediate update for floating window
             if (_settings.EnableFloatingLyrics && _floatingWindow != null)
                 _floatingWindow.UpdateLyrics(newCurrent);

             // Resolve Next Weight
             FontWeight nextWeight = FontWeights.Normal;
             try 
             {
                  if (!string.IsNullOrEmpty(_settings.NextLyricFontWeight))
                  {
                      var weightStr = _settings.NextLyricFontWeight.Split(' ')[0];
                      var converter = new FontWeightConverter();
                      var obj = converter.ConvertFromString(weightStr);
                      nextWeight = (obj as FontWeight?) ?? FontWeights.Normal;
                  }
             }
             catch {}

             double nextFontSize = Math.Max(9, _settings.FontSize - _settings.NextLyricFontSizeDiff);

             // 1. Swap References Logic
             var oldMain = _mainLyricControl;
             var oldNext = _nextLyricControl;

             // Promoted: Used to be Next (Bottom), now Main (Top)
             _mainLyricControl = oldNext; 
             // Demoted: Used to be Main (Top), now Next (Bottom)
             _nextLyricControl = oldMain; 

             double topPos, bottomPos;
             GetDoubleLinePositions(out topPos, out bottomPos);
             
             // 2. Animate NEW Main (Moving Bottom -> Top)
             // Reset animations first
             _mainLyricControl.BeginAnimation(Canvas.LeftProperty, null);
             Canvas.SetLeft(_mainLyricControl, 0);
             _mainLyricControl.BeginAnimation(Canvas.TopProperty, null);
             _mainLyricControl.BeginAnimation(TextBlock.FontSizeProperty, null);
             _mainLyricControl.BeginAnimation(TextBlock.OpacityProperty, null);
             
             _mainLyricControl.Visibility = Visibility.Visible;
             _mainLyricControl.Text = newCurrent;
             
             // Transition Weight: From NextWeight to MainWeight (SemiBold)
             _mainLyricControl.FontWeight = FontWeights.SemiBold; 

             // Animations
             var moveUp = new DoubleAnimation(bottomPos, topPos, TimeSpan.FromMilliseconds(400)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
             var growFont = new DoubleAnimation(nextFontSize, _settings.FontSize, TimeSpan.FromMilliseconds(400)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
             var fadeIn = new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(400)); 

             _mainLyricControl.BeginAnimation(Canvas.TopProperty, moveUp);
             _mainLyricControl.BeginAnimation(TextBlock.FontSizeProperty, growFont);
             _mainLyricControl.BeginAnimation(TextBlock.OpacityProperty, fadeIn);

             // 3. Animate NEW Next (Old Main leaving Top)
             _nextLyricControl.BeginAnimation(Canvas.LeftProperty, null);
             Canvas.SetLeft(_nextLyricControl, 0);
             _nextLyricControl.BeginAnimation(Canvas.TopProperty, null);
             _nextLyricControl.BeginAnimation(TextBlock.FontSizeProperty, null);
             _nextLyricControl.BeginAnimation(TextBlock.OpacityProperty, null);
             
             _nextLyricControl.Visibility = Visibility.Visible;

             // Exiting animation: Fade out 
             var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(200));
             
             // We need to capture variables for closure
             var targetNextWeight = nextWeight;
             var targetNextFontSize = nextFontSize;

             fadeOut.Completed += (s, e) => 
             {
                 // Recycle for New Next
                 _nextLyricControl.BeginAnimation(Canvas.TopProperty, null); 
                 _nextLyricControl.Text = newNext;
                 _nextLyricControl.Visibility = Visibility.Visible;
                 
                 Canvas.SetTop(_nextLyricControl, bottomPos);
                 _nextLyricControl.FontSize = targetNextFontSize;
                 _nextLyricControl.FontWeight = targetNextWeight; 
                 
                 // Fade In New Next
                 var nextFadeIn = new DoubleAnimation(0.0, 0.7, TimeSpan.FromMilliseconds(200));
                 _nextLyricControl.BeginAnimation(TextBlock.OpacityProperty, nextFadeIn);
             };

             _nextLyricControl.BeginAnimation(TextBlock.OpacityProperty, fadeOut);
             
             // 4. Trigger Scroll for Main after animation
             var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
             timer.Tick += (s, e) =>
             {
                 timer.Stop();
                 // Finalize State 
                 _mainLyricControl.BeginAnimation(Canvas.TopProperty, null);
                 Canvas.SetTop(_mainLyricControl, topPos);
                 _mainLyricControl.BeginAnimation(TextBlock.FontSizeProperty, null);
                 _mainLyricControl.FontSize = _settings.FontSize;
                 _mainLyricControl.BeginAnimation(TextBlock.OpacityProperty, null);
                 _mainLyricControl.Opacity = 1.0;
                 _mainLyricControl.Visibility = Visibility.Visible;
                 
                 UpdateSingleLineScroll(_mainLyricControl, newCurrent, currentDur);
             };
             timer.Start();
        }
        
        private void ShowStatusText(string text)
        {
            _isShowingStatusText = true;
            ApplyStatusTextLayout();
            UpdateSingleLineScroll(_mainLyricControl, text, 0, true);
            _desktopWidget?.UpdateLyrics(text);
        }

        private void UpdateSingleLineScroll(TextBlock target, string text, double durationSeconds, bool isInfinite = false, bool followSyllables = false)
        {
             target.BeginAnimation(Canvas.LeftProperty, null);
             Canvas.SetLeft(target, 0);
             target.Visibility = Visibility.Visible;
             
             _nextLyricControl.BeginAnimation(Canvas.LeftProperty, null);

             target.UpdateLayout(); 

             double textWidth = target.ActualWidth;
             double containerWidth = TextContainer.ActualWidth;

             _scrollableLyricDistance = 0;

             if (textWidth > containerWidth)
             {
                 double gap = 50; 
                 if (followSyllables)
                 {
                     // 有逐字时间：滚动完全跟随逐字高亮进度，由 UpdateSingleLineProgress 驱动
                     _scrollableLyricDistance = textWidth - containerWidth + 20;
                     if (isInfinite) _nextLyricControl.Visibility = Visibility.Collapsed;
                 }
                 else if (isInfinite)
                 {
                     _nextLyricControl.Text = text;
                     _nextLyricControl.Visibility = Visibility.Visible;
                     _nextLyricControl.FontSize = target.FontSize; 
                     _nextLyricControl.FontWeight = target.FontWeight; 
                     _nextLyricControl.Opacity = target.Opacity;
                     _nextLyricControl.Foreground = target.Foreground;
                     
                     Canvas.SetTop(_nextLyricControl, Canvas.GetTop(target));
                     _nextLyricControl.UpdateLayout();
                     
                     double totalDistance = textWidth + gap;
                     double duration = totalDistance / 30.0;

                     DoubleAnimation animMain = new DoubleAnimation(0, -totalDistance, new Duration(TimeSpan.FromSeconds(duration))) { RepeatBehavior = RepeatBehavior.Forever };
                     DoubleAnimation animNext = new DoubleAnimation(totalDistance, 0, new Duration(TimeSpan.FromSeconds(duration))) { RepeatBehavior = RepeatBehavior.Forever };

                     target.BeginAnimation(Canvas.LeftProperty, animMain);
                     _nextLyricControl.BeginAnimation(Canvas.LeftProperty, animNext);
                 }
                 else
                 {
                     double delay = 1.0;
                     double scrollTime = Math.Max(2.0, durationSeconds - 2.0);
                     DoubleAnimation animation = new DoubleAnimation(0, -(textWidth - containerWidth + 20), new Duration(TimeSpan.FromSeconds(scrollTime))) { BeginTime = TimeSpan.FromSeconds(delay) };
                     target.BeginAnimation(Canvas.LeftProperty, animation);
                 }
             }
             else
             {
                 if (isInfinite) _nextLyricControl.Visibility = Visibility.Collapsed;
             }
        }
        
        private void UpdateInfo()
        {
             // Fallback or Trigger manual update
        }

        private void MediaManager_AppIdChanged(object? sender, string appId)
        {
            Dispatcher.Invoke(() => UpdateAppIcon(appId));
        }

        private void UpdateAppIcon(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                AppIcon.Visibility = Visibility.Collapsed;
                AppIconBackdrop.Visibility = Visibility.Collapsed;
                return;
            }

            ImageSource? iconSource = null;

            // 1. Try if it's a direct path
            if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(appId))
            {
                iconSource = GetIconFromPath(appId);
            }
            
            // 2. Fallback: Try to find a running process that matches the ID or common names
            if (iconSource == null)
            {
                string processName = appId;
                
                // Cleanup ID if it looks like a UWP ID or complex string
                if (processName.Contains("!"))
                {
                   // processName = processName.Split('!')[0]; // Simple attempt
                }
                
                // Clean up extension
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    processName = System.IO.Path.GetFileNameWithoutExtension(processName);

                iconSource = GetIconFromProcessName(processName);
                
                // Specific fix for "Spotify" which might be named simply "Spotify" in ID but "Spotify.exe" in process
                if (iconSource == null && appId.ToLower().Contains("spotify"))
                     iconSource = GetIconFromProcessName("Spotify");
                     
                else if (iconSource == null && appId.ToLower().Contains("cloudmusic")) // NetEase
                     iconSource = GetIconFromProcessName("cloudmusic"); // Or cloudmusic.exe
                     
                 else if (iconSource == null && appId.ToLower().Contains("qqmusic")) // QQ Music
                     iconSource = GetIconFromProcessName("QQMusic");
            }

            if (iconSource != null)
            {
                AppIcon.Source = iconSource;
                AppIcon.Visibility = Visibility.Visible;
                // 白色/浅色图标（如 Kimi）在浅色组件背景上几乎不可见，垫一个深色圆角底板来衬托
                AppIconBackdrop.Visibility = IsMostlyLightIcon(iconSource)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                AppIcon.Visibility = Visibility.Collapsed;
                AppIconBackdrop.Visibility = Visibility.Collapsed;
            }
        }

        private ImageSource? GetIconFromProcessName(string processName)
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    try 
                    {
                        var path = processes[0].MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        {
                            return GetIconFromPath(path);
                        }
                    }
                    catch { } // MainModule might deny access
                }
            }
            catch {}
            return null;
        }

        private ImageSource? GetIconFromPath(string path)
        {
            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon != null)
                    {
                        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch {}
            return null;
        }

        // 判断图标是否以浅色/白色为主；这类图标在浅色背景上几乎不可见，需要深色底板衬托
        private static bool IsMostlyLightIcon(ImageSource source)
        {
            if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return false;
            }

            try
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                int opaque = 0;
                int light = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] < 128) continue; // 忽略透明像素
                    opaque++;
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                    if (luminance > 225) light++;
                }

                return opaque > 0 && light / (double)opaque > 0.55;
            }
            catch
            {
                return false;
            }
        }

        private void DragHandle_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as UIElement;
            if (el != null)
            {
                _isDraggingHandle = true;
                el.CaptureMouse();
                try
                {
                    _dragStartMouseScreenPos = PointToScreen(e.GetPosition(this));
                }
                catch
                {
                    _dragStartMouseScreenPos = e.GetPosition(null);
                }
                _dragStartOffsetX = _settings.OffsetX;
                e.Handled = true;
            }
        }

        private void DragHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingHandle) return;

            Point currentMouseScreenPos;
            try
            {
                currentMouseScreenPos = PointToScreen(e.GetPosition(this));
            }
            catch
            {
                currentMouseScreenPos = e.GetPosition(null);
            }

            double deltaX = currentMouseScreenPos.X - _dragStartMouseScreenPos.X;

            int newOffsetX = _dragStartOffsetX - (int)Math.Round(deltaX);

            // 限制 OffsetX 不能为负
            if (newOffsetX < 0) newOffsetX = 0;

            if (_settings.OffsetX != newOffsetX)
            {
                _settings.OffsetX = newOffsetX;
                InjectIntoTaskbar();
            }
            e.Handled = true;
        }

        private void DragHandle_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingHandle)
            {
                var el = sender as UIElement;
                if (el != null)
                {
                    el.ReleaseMouseCapture();
                }
                _isDraggingHandle = false;
                
                // 拖拽结束时保存设置
                _settings.Save();
                e.Handled = true;
            }
        }

        private void WidthGrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as UIElement;
            if (el == null) return;

            _isDraggingWidth = true;
            el.CaptureMouse();
            try
            {
                _dragStartMouseScreenPos = PointToScreen(e.GetPosition(this));
            }
            catch
            {
                _dragStartMouseScreenPos = e.GetPosition(null);
            }
            _dragStartWidth = _settings.Width;
            _dragStartWidthOffsetX = _settings.OffsetX;
            e.Handled = true;
        }

        private void WidthGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingWidth) return;

            Point currentMouseScreenPos;
            try
            {
                currentMouseScreenPos = PointToScreen(e.GetPosition(this));
            }
            catch
            {
                currentMouseScreenPos = e.GetPosition(null);
            }

            double deltaX = currentMouseScreenPos.X - _dragStartMouseScreenPos.X;
            double newWidth = Math.Round(Math.Clamp(_dragStartWidth + deltaX, MinTaskbarLyricsWidth, MaxTaskbarLyricsWidth));
            int newOffsetX = Math.Max(0, _dragStartWidthOffsetX - (int)Math.Round(newWidth - _dragStartWidth));

            if (Math.Abs(_settings.Width - newWidth) > 0.01 || _settings.OffsetX != newOffsetX)
            {
                _settings.Width = newWidth;
                _settings.OffsetX = newOffsetX;
                InjectIntoTaskbar();
            }
            e.Handled = true;
        }

        private void WidthGrip_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isDraggingWidth) return;

            var el = sender as UIElement;
            el?.ReleaseMouseCapture();
            _isDraggingWidth = false;

            // 拖拽结束时保存设置
            _settings.Save();
            e.Handled = true;
        }

        private void AppIcon_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                string appId = _mediaManager.CurrentAppAppUserModelId;
                if (!string.IsNullOrEmpty(appId))
                {
                    TryActivateApp(appId);
                }
            }
        }

        private void TryActivateApp(string appId)
        {
             // 1. Try generic process finding logic
             string processName = appId;
             if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                processName = System.IO.Path.GetFileNameWithoutExtension(processName);

             bool success = TryActivateAppByName(processName);

             // 2. Fallbacks for common apps
             if (!success)
             {
                 if (appId.ToLower().Contains("spotify")) TryActivateAppByName("Spotify");
                 else if (appId.ToLower().Contains("cloudmusic")) TryActivateAppByName("cloudmusic");
                 else if (appId.ToLower().Contains("qqmusic")) TryActivateAppByName("QQMusic");
             }
        }

        private bool TryActivateAppByName(string processName)
        {
            var pids = new System.Collections.Generic.HashSet<uint>();
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(processName);
                if (processes.Length == 0) return false;

                foreach (var p in processes)
                {
                    pids.Add((uint)p.Id);
                    // Standard check
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        if (UnmanagedMethods.IsWindowVisible(p.MainWindowHandle))
                        {
                            IntPtr hwnd = p.MainWindowHandle;
                             // If minimized, restore
                            UnmanagedMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                            UnmanagedMethods.SetForegroundWindow(hwnd);
                            return true;
                        }
                    }
                }
                
                // Advanced Search
                IntPtr bestCandidate = IntPtr.Zero;

                UnmanagedMethods.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
                {
                    uint pid;
                    UnmanagedMethods.GetWindowThreadProcessId(hwnd, out pid);
                    
                    if (pids.Contains(pid))
                    {
                        // Must have a title to be a candidate for a "Main Window"
                        int len = UnmanagedMethods.GetWindowTextLength(hwnd);
                        if (len > 0)
                        {
                            bool isVisible = UnmanagedMethods.IsWindowVisible(hwnd);
                            
                            // If we found a visible window with a title, it's a strong candidate
                            if (isVisible)
                            {
                                bestCandidate = hwnd;
                                return false; // Stop searching, we found a good one
                            }
                            
                            // Only update if we haven't found a candidate yet
                            if (bestCandidate == IntPtr.Zero)
                            {
                                bestCandidate = hwnd;
                            }
                        }
                    }
                    return true; // Continue
                }, IntPtr.Zero);
                
                if (bestCandidate != IntPtr.Zero)
                {
                    UnmanagedMethods.ShowWindow(bestCandidate, 9); // Force Restore
                    UnmanagedMethods.SetForegroundWindow(bestCandidate);
                    return true;
                }
            }
            catch
            { 
                 // Troubleshooting
                 // System.Windows.MessageBox.Show("Error: " + ex.Message);
            }
            return false;
        }
    }
}
