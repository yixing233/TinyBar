using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Wpf.Ui.Markup;
using FontFamily = System.Windows.Media.FontFamily;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfUiApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TaskbarInfo;

public partial class QuickTranslatePopupWindow : Window
{
    private const double PopupWidth = 480;
    private const double CompactPopupHeight = 294;
    private const double CompactResultTextBoxHeight = 64;
    private const double MaximumResultTextBoxHeight = 156;
    private const double ResultTextBoxLineHeight = 18;
    private const double ResultTextBoxVerticalPadding = 12;
    private const double ErrorStatusHeight = 34;
    private const int ResultDisplayUnitsPerLine = 48;
    private const byte AcrylicTintOpacity = 96;
    private static readonly Duration PopupTransitionDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _translationElapsedTimer;
    private readonly ThemesDictionary _wpfUiThemeDictionary = new();
    private CancellationTokenSource? _translationCancellation;
    private QuickTranslateLaunchOptions? _launchOptions;
    private QuickTranslatePlacement? _placement;
    private bool _isAlwaysOnTop;
    private bool _isShowingDomainDialog;
    private bool _loadingDomains;
    private bool _loadingProviders;
    private bool _loadingTargetLanguage;
    private bool _revealAfterRender;
    private bool _isTranslating;
    private int _translationVersion;
    private DateTimeOffset _translationStartedAt;
    private string _translationProgressPrefix = string.Empty;
    private int _layoutAnimationVersion;

    public QuickTranslatePopupWindow(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        Resources.MergedDictionaries.Insert(0, _wpfUiThemeDictionary);
        Resources.MergedDictionaries.Insert(1, new ControlsDictionary());
        ApplyWpfUiTheme();
        ApplyConfiguredFontFamily();
        _translationElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _translationElapsedTimer.Tick += TranslationElapsedTimer_Tick;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(10),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0)
        });
        SourceInitialized += (_, _) =>
        {
            PositionNativeWindow();
            ApplyWindowMaterial();
        };
        ContentRendered += Window_ContentRendered;
        Deactivated += Window_Deactivated;
        Closed += (_, _) =>
        {
            _translationElapsedTimer.Stop();
            _translationCancellation?.Cancel();
            _translationCancellation?.Dispose();
        };

        PopulateProviderBox();
        PopulateDomainBox();
        PopulateTargetLanguageBox();
        UpdateDomainControls();
    }

    private void ApplyConfiguredFontFamily()
    {
        try
        {
            FontFamily = new FontFamily(_settings.QuickTranslateFontFamily);
        }
        catch
        {
            FontFamily = new FontFamily("Microsoft YaHei UI");
        }
    }

    public void ShowAt(QuickTranslateLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _launchOptions = options;
        ResizeForContent(false);
        PositionBeforeShow(options);
        _revealAfterRender = true;
        Opacity = 0;
        Show();
    }

    private void PositionBeforeShow(QuickTranslateLaunchOptions options)
    {
        double scale = GetTargetDpiScale(options) / 96d;
        _placement = GetPlacement(options, Height);

        Left = _placement.Value.Left / scale;
        Top = _placement.Value.Top / scale;
    }

    private static QuickTranslatePlacement GetPlacement(QuickTranslateLaunchOptions options, double logicalHeight)
    {
        double scale = GetTargetDpiScale(options) / 96d;
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(PopupWidth * scale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
        return QuickTranslateLayout.GetPlacement(
            options.ButtonBounds,
            options.TaskbarBounds,
            options.ScreenBounds,
            options.WorkArea,
            pixelWidth,
            pixelHeight);
    }

    private static uint GetTargetDpiScale(QuickTranslateLaunchOptions options)
    {
        var point = new UnmanagedMethods.POINT
        {
            X = options.WorkArea.Left,
            Y = options.WorkArea.Top
        };
        IntPtr monitor = UnmanagedMethods.MonitorFromPoint(point, UnmanagedMethods.MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero &&
            UnmanagedMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 &&
            dpiX > 0
            ? dpiX
            : 96;
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (!_revealAfterRender) return;

        _revealAfterRender = false;
        PositionNativeWindow();
        ApplyWindowMaterial();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!IsVisible) return;
            PositionNativeWindow();
            ApplyWindowMaterial();
            BeginOpenAnimation();
            InputTextBox.Focus();
        }));
    }

    private void PositionNativeWindow()
    {
        if (_placement is not { } placement) return;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        UnmanagedMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            placement.Left,
            placement.Top,
            0,
            0,
            UnmanagedMethods.SWP_NOSIZE |
            UnmanagedMethods.SWP_NOZORDER |
            UnmanagedMethods.SWP_NOACTIVATE);
    }

    private void ResizeForContent(bool expand)
    {
        double resultTextBoxHeight = expand
            ? CalculateResultTextBoxHeight()
            : CompactResultTextBoxHeight;
        ResultTextBox.Height = resultTextBoxHeight;

        double targetHeight = CompactPopupHeight + resultTextBoxHeight - CompactResultTextBoxHeight;
        if (StatusPanel.Visibility == Visibility.Visible)
        {
            targetHeight += ErrorStatusHeight;
        }

        AnimatePopupSize(targetHeight);
    }

    private double CalculateResultTextBoxHeight()
    {
        string result = ResultTextBox.Text;
        if (string.IsNullOrWhiteSpace(result)) return CompactResultTextBoxHeight;

        int visualLineCount = 0;
        foreach (string paragraph in result.Replace("\r\n", "\n").Split('\n'))
        {
            int displayUnits = paragraph.Sum(character => character <= 0x7F ? 1 : 2);
            visualLineCount += Math.Max(1, (int)Math.Ceiling(
                displayUnits / (double)ResultDisplayUnitsPerLine));
        }

        double contentHeight = ResultTextBoxVerticalPadding +
            visualLineCount * ResultTextBoxLineHeight;
        return Math.Clamp(contentHeight, CompactResultTextBoxHeight, MaximumResultTextBoxHeight);
    }

    private void BeginOpenAnimation()
    {
        Opacity = 0;
        PopupTranslation.Y = 8;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PopupTransitionDuration));
        PopupTranslation.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, PopupTransitionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimatePopupSize(double targetHeight)
    {
        if (_launchOptions == null || !IsVisible)
        {
            Height = targetHeight;
            if (_launchOptions != null) PositionBeforeShow(_launchOptions);
            return;
        }

        QuickTranslateLaunchOptions options = _launchOptions;
        double scale = GetTargetDpiScale(options) / 96d;
        QuickTranslatePlacement targetPlacement = GetPlacement(options, targetHeight);
        double targetTop = targetPlacement.Top / scale;
        double targetLeft = targetPlacement.Left / scale;
        if (Math.Abs(Height - targetHeight) < 0.5 && Math.Abs(Top - targetTop) < 0.5)
        {
            _placement = targetPlacement;
            Left = targetLeft;
            return;
        }

        int animationVersion = ++_layoutAnimationVersion;
        _placement = targetPlacement;
        Left = targetLeft;
        var heightAnimation = new DoubleAnimation(Height, targetHeight, PopupTransitionDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _layoutAnimationVersion) return;

            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            BeginAnimation(TopProperty, null);
            Top = targetTop;
        };
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(TopProperty, new DoubleAnimation(Top, targetTop, PopupTransitionDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ApplyWindowMaterial()
    {
        ApplyWpfUiTheme();
        ResolvedApplicationTheme theme = ApplicationThemeParser.Resolve(_settings.ApplicationTheme);
        QuickTranslateWindowMaterial material = QuickTranslateWindowMaterialParser.Parse(
            _settings.QuickTranslateWindowMaterial);

        // Mica is a DWM_SYSTEMBACKDROP_TYPE backdrop, which Windows 10 does not implement:
        // the attribute call fails and the card would stay fully transparent, leaving the
        // desktop showing through the window. Fall back to acrylic, which Windows 10 has.
        if (material == QuickTranslateWindowMaterial.Mica &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            material = QuickTranslateWindowMaterial.Acrylic;
        }

        RootCard.Background = material switch
        {
            QuickTranslateWindowMaterial.Solid => SolidBackground(theme),
            QuickTranslateWindowMaterial.Acrylic => MediaBrushes.Transparent,
            QuickTranslateWindowMaterial.Mica => MediaBrushes.Transparent,
            _ => SolidBackground(theme)
        };

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int cornerPreference = (int)UnmanagedMethods.DwmWindowCornerPreference.DWMWCP_ROUND;
            UnmanagedMethods.DwmSetWindowAttribute(
                handle,
                UnmanagedMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                sizeof(int));

            int backdropType = material == QuickTranslateWindowMaterial.Mica
                ? (int)UnmanagedMethods.DwmSystemBackdropType.DWMSBT_TRANSIENTWINDOW
                : (int)UnmanagedMethods.DwmSystemBackdropType.DWMSBT_NONE;
            UnmanagedMethods.DwmSetWindowAttribute(
                handle,
                UnmanagedMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                ref backdropType,
                sizeof(int));
        }

        if (material == QuickTranslateWindowMaterial.Acrylic)
        {
            ApplyAcrylicBackdrop(theme);
        }
        else
        {
            ClearAcrylicBackdrop(handle);
        }
    }

    private void ApplyWpfUiTheme()
    {
        _wpfUiThemeDictionary.Theme = ApplicationThemeParser.Resolve(_settings.ApplicationTheme)
            == ResolvedApplicationTheme.Dark
            ? WpfUiApplicationTheme.Dark
            : WpfUiApplicationTheme.Light;
    }

    private static SolidColorBrush SolidBackground(ResolvedApplicationTheme theme) =>
        theme == ResolvedApplicationTheme.Dark
            ? new SolidColorBrush(MediaColor.FromRgb(24, 32, 42))
            : new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));

    private void ApplyAcrylicBackdrop(ResolvedApplicationTheme theme)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var accent = new UnmanagedMethods.AccentPolicy
        {
            AccentState = UnmanagedMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 0,
            GradientColor = ToAccentColor(theme == ResolvedApplicationTheme.Dark
                ? MediaColor.FromArgb(AcrylicTintOpacity, 24, 32, 42)
                : MediaColor.FromArgb(AcrylicTintOpacity, 245, 247, 250)),
            AnimationId = 0
        };
        SetAccentPolicy(handle, accent);
    }

    private static void ClearAcrylicBackdrop(IntPtr handle) => SetAccentPolicy(handle, new UnmanagedMethods.AccentPolicy
    {
        AccentState = UnmanagedMethods.AccentState.ACCENT_DISABLED
    });

    private static void SetAccentPolicy(IntPtr handle, UnmanagedMethods.AccentPolicy accent)
    {
        int size = Marshal.SizeOf<UnmanagedMethods.AccentPolicy>();
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new UnmanagedMethods.WindowCompositionAttributeData
            {
                Attribute = UnmanagedMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = data,
                SizeOfData = size
            };
            UnmanagedMethods.SetWindowCompositionAttribute(handle, ref attribute);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static int ToAccentColor(MediaColor color) =>
        unchecked((int)((uint)color.A << 24 | (uint)color.B << 16 | (uint)color.G << 8 | color.R));

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isAlwaysOnTop || _isShowingDomainDialog) return;

        // ComboBox popups use a separate native surface. Wait until it has opened
        // before deciding whether deactivation came from a selector or the user.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isAlwaysOnTop && !_isShowingDomainDialog && !IsSelectorDropDownOpen())
            {
                Close();
            }
        }, DispatcherPriority.Input);
    }

    private bool IsSelectorDropDownOpen() =>
        ProviderBox.IsDropDownOpen || TargetLanguageBox.IsDropDownOpen || DomainBox.IsDropDownOpen;

    private void AlwaysOnTopButton_Checked(object sender, RoutedEventArgs e) => SetAlwaysOnTop(true);

    private void AlwaysOnTopButton_Unchecked(object sender, RoutedEventArgs e) => SetAlwaysOnTop(false);

    private void SetAlwaysOnTop(bool alwaysOnTop)
    {
        _isAlwaysOnTop = alwaysOnTop;
        Topmost = alwaysOnTop;
        AlwaysOnTopButton.ToolTip = alwaysOnTop ? "取消置顶" : "置顶窗口";

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        UnmanagedMethods.SetWindowPos(
            handle,
            alwaysOnTop ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            UnmanagedMethods.SWP_NOMOVE |
            UnmanagedMethods.SWP_NOSIZE |
            UnmanagedMethods.SWP_NOACTIVATE);
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            CancelActiveTranslation();
            return;
        }

        await TranslateAsync();
    }

    private async void InputTextBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        e.Handled = true;
        await TranslateAsync();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviders) return;

        UpdateDomainControls();
        if (SelectedProvider() is not { } provider) return;

        _settings.SelectedTranslationProviderId = provider.Id;
        _settings.Save();
    }

    private void DomainBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDomains) return;

        _settings.SelectedQuickTranslateDomain = TranslationDomainCatalog.ResolveSelected(
            _settings.QuickTranslateDomains,
            SelectedDomain());
        if (!_settings.Save(out string? errorMessage))
        {
            ShowError("无法保存翻译领域: " + errorMessage);
        }
    }

    private void TargetLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTargetLanguage) return;

        _settings.QuickTranslateTargetLanguage = SelectedTargetLanguage();
        if (!_settings.Save(out string? errorMessage))
        {
            ShowError("无法保存目标语言: " + errorMessage);
        }
    }

    private void AddDomainButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new QuickTranslateDomainDialog(_settings.QuickTranslateDomains) { Owner = this };
        _isShowingDomainDialog = true;
        bool? accepted;
        try
        {
            accepted = dialog.ShowDialog();
        }
        finally
        {
            _isShowingDomainDialog = false;
        }

        if (accepted != true) return;

        _settings.QuickTranslateDomains = TranslationDomainCatalog.Normalize(
            _settings.QuickTranslateDomains.Append(dialog.Domain));
        _settings.SelectedQuickTranslateDomain = dialog.Domain;
        PopulateDomainBox();
        if (!_settings.Save(out string? errorMessage))
        {
            ShowError("无法保存翻译领域: " + errorMessage);
        }
    }

    private async Task TranslateAsync()
    {
        string input = InputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowError("请输入要翻译的文本。");
            return;
        }

        TranslationProviderProfile? provider = SelectedProvider();
        if (provider == null)
        {
            ShowError("请先在设置中添加翻译服务商。");
            return;
        }

        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _translationCancellation.Token;
        int translationVersion = ++_translationVersion;

        SetTranslationState(true, provider);
        try
        {
            string targetLanguage = SelectedTargetLanguage();
            string domain = SelectedDomain();
            string translatedText = await TranslationService.TranslateAsync(
                input,
                targetLanguage,
                new TranslationConfiguration(
                    provider.Id,
                    provider.Provider,
                    provider.AppId,
                    provider.AppSecret,
                    provider.ApiBaseUrl,
                    provider.ExtraCredential,
                    provider.SystemPrompt,
                    domain,
                    _settings.EnableQuickTranslateAiPhonetic),
                cancellationToken);
            if (translationVersion != _translationVersion) return;

            ResultTextBox.Text = translatedText;
            ResizeForContent(!string.IsNullOrWhiteSpace(ResultTextBox.Text));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
            if (translationVersion == _translationVersion)
            {
                ShowError("无法连接翻译服务，请检查网络后重试。");
            }
        }
        catch (Exception exception)
        {
            if (translationVersion == _translationVersion)
            {
                ShowError(exception.Message);
            }
        }
        finally
        {
            if (translationVersion == _translationVersion && IsLoaded)
            {
                SetTranslationState(false);
                ResizeForContent(!string.IsNullOrWhiteSpace(ResultTextBox.Text) ||
                    StatusPanel.Visibility == Visibility.Visible);
            }
        }
    }

    private void ShowError(string message)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Message = message;
        StatusPanel.IsOpen = true;
        StatusPanel.Visibility = Visibility.Visible;
        ResizeForContent(true);
    }

    private void SetTranslationState(bool translating, TranslationProviderProfile? provider = null)
    {
        _isTranslating = translating;
        TranslateButton.IsEnabled = true;
        TranslateButton.Content = translating ? "取消" : "翻译";
        InputTextBox.IsEnabled = !translating;
        ProviderBox.IsEnabled = !translating;
        TargetLanguageBox.IsEnabled = !translating;
        DomainBox.IsEnabled = !translating;
        AddDomainButton.IsEnabled = !translating;
        ProgressPanel.Visibility = translating ? Visibility.Visible : Visibility.Collapsed;
        ResultLabel.Visibility = translating ? Visibility.Collapsed : Visibility.Visible;

        if (!translating)
        {
            _translationElapsedTimer.Stop();
            return;
        }

        StatusPanel.Visibility = Visibility.Collapsed;
        StatusPanel.IsOpen = false;
        _translationStartedAt = DateTimeOffset.UtcNow;
        _translationProgressPrefix = TranslationService.IsAiProvider(provider?.Provider)
            ? "AI 正在生成译文"
            : "翻译服务正在处理";
        UpdateTranslationProgress();
        _translationElapsedTimer.Start();
    }

    private void TranslationElapsedTimer_Tick(object? sender, EventArgs e) => UpdateTranslationProgress();

    private void UpdateTranslationProgress()
    {
        int elapsedSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - _translationStartedAt).TotalSeconds);
        ProgressText.Text = elapsedSeconds == 0
            ? _translationProgressPrefix + "..."
            : _translationProgressPrefix + "，已等待 " + elapsedSeconds + " 秒";
    }

    private void CancelActiveTranslation()
    {
        if (_translationCancellation?.IsCancellationRequested != false) return;

        _translationCancellation.Cancel();
        TranslateButton.IsEnabled = false;
        TranslateButton.Content = "取消中";
        ProgressText.Text = "正在取消翻译请求...";
    }

    private void PopulateProviderBox()
    {
        _loadingProviders = true;
        ProviderBox.Items.Clear();
        foreach (TranslationProviderProfile profile in _settings.TranslationProviders
            .Where(profile => !TranslationProviderProfiles.IsEmptyDraft(profile)))
        {
            ProviderBox.Items.Add(new ComboBoxItem
            {
                Content = profile.DisplayName,
                Tag = profile
            });
        }

        ProviderBox.SelectedItem = ProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                (item.Tag as TranslationProviderProfile)?.Id,
                _settings.SelectedTranslationProviderId,
                StringComparison.OrdinalIgnoreCase))
            ?? ProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _loadingProviders = false;
    }

    private void PopulateDomainBox()
    {
        _loadingDomains = true;
        _settings.QuickTranslateDomains = TranslationDomainCatalog.Normalize(_settings.QuickTranslateDomains);
        _settings.SelectedQuickTranslateDomain = TranslationDomainCatalog.ResolveSelected(
            _settings.QuickTranslateDomains,
            _settings.SelectedQuickTranslateDomain);
        DomainBox.Items.Clear();
        foreach (string domain in _settings.QuickTranslateDomains)
        {
            DomainBox.Items.Add(new ComboBoxItem { Content = domain, Tag = domain });
        }

        DomainBox.SelectedItem = DomainBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.SelectedQuickTranslateDomain,
                StringComparison.OrdinalIgnoreCase))
            ?? DomainBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _loadingDomains = false;
    }

    private void PopulateTargetLanguageBox()
    {
        _loadingTargetLanguage = true;
        try
        {
            string targetLanguage = QuickTranslateTargetLanguages.Normalize(
                _settings.QuickTranslateTargetLanguage);
            TargetLanguageBox.SelectedItem = TargetLanguageBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, targetLanguage,
                    StringComparison.OrdinalIgnoreCase))
            ?? TargetLanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally
        {
            _loadingTargetLanguage = false;
        }
    }

    private void UpdateDomainControls()
    {
        bool isAiProvider = SelectedProvider() is { } provider && TranslationService.IsAiProvider(provider.Provider);
        DomainBox.Visibility = isAiProvider ? Visibility.Visible : Visibility.Collapsed;
        AddDomainButton.Visibility = isAiProvider ? Visibility.Visible : Visibility.Collapsed;
        DomainLeadingGap.Width = new GridLength(isAiProvider ? 6 : 0);
        DomainColumn.Width = new GridLength(isAiProvider ? 110 : 0);
        DomainTrailingGap.Width = new GridLength(isAiProvider ? 6 : 0);
        AddDomainColumn.Width = new GridLength(isAiProvider ? 32 : 0);
    }

    private async void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        string text = ResultTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            CopyButtonText.Text = "已复制";
            CopyButtonIcon.Text = "\ue06c"; // lucide-check
            await Task.Delay(1500);
            CopyButtonText.Text = "复制";
            CopyButtonIcon.Text = "\ue09e"; // lucide-copy
        }
        catch
        {
        }
    }

    private TranslationProviderProfile? SelectedProvider() =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as TranslationProviderProfile;

    private string SelectedDomain() =>
        (DomainBox.SelectedItem as ComboBoxItem)?.Tag as string ?? TranslationDomainCatalog.General;

    private string SelectedTargetLanguage() => QuickTranslateTargetLanguages.Normalize(
        (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);
}
