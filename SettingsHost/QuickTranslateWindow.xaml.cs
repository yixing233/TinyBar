using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskbarInfo;
using Windows.Graphics;
using Windows.System;

namespace TinyBar.Settings;

public sealed partial class QuickTranslateWindow : Window
{
    private const int WindowWidth = 420;
    private const int WindowHeight = 350;
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AcrylicGradientColor = unchecked((int)0x70FAF3EE);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly QuickTranslateLaunchOptions _options;
    private readonly SettingsDocument _settings;
    private readonly string _settingsPath;
    private CancellationTokenSource? _translationCancellation;
    private bool _positioned;
    private bool _isAlwaysOnTop;
    private bool _loadingProviders;

    public QuickTranslateWindow(QuickTranslateLaunchOptions options, string settingsPath)
    {
        _options = options;
        _settingsPath = settingsPath;
        _settings = SettingsDocument.Load(settingsPath);
        InitializeComponent();
        PopulateProviderBox();

        IntPtr initialWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = initialWindowHandle == IntPtr.Zero ? 96 : GetDpiForWindow(initialWindowHandle);
        AppWindow.Resize(ScaleWindowSizeForDpi(WindowWidth, WindowHeight, dpi));
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TinyBar.ico");
        if (!File.Exists(iconPath)) iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LyricsX.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        ApplyWindowMaterial();
        PositionOnTargetScreen();
        Activated += Window_Activated;
        Closed += (_, _) => _translationCancellation?.Cancel();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_isAlwaysOnTop)
        {
            Close();
            return;
        }

        PositionOnTargetScreen();
        // Keep this in sync with the Windows 10 fallback in ApplyWindowMaterial.
        QuickTranslateWindowMaterial material = QuickTranslateWindowMaterialParser.Parse(
            _settings.QuickTranslateWindowMaterial);
        if (material == QuickTranslateWindowMaterial.Mica &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            material = QuickTranslateWindowMaterial.Acrylic;
        }

        if (material == QuickTranslateWindowMaterial.Acrylic)
        {
            DispatcherQueue.TryEnqueue(ApplyNativeAcrylicBackdrop);
        }
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private void PositionOnTargetScreen()
    {
        if (_positioned) return;

        SizeInt32 windowSize = AppWindow.Size;
        if (windowSize.Width <= 0 || windowSize.Height <= 0) return;
        QuickTranslatePlacement placement = QuickTranslateLayout.GetPlacement(
            _options.ButtonBounds,
            _options.TaskbarBounds,
            _options.ScreenBounds,
            _options.WorkArea,
            windowSize.Width,
            windowSize.Height);
        AppWindow.Move(new PointInt32(placement.Left, placement.Top));
        _positioned = true;
    }

    private static SizeInt32 ScaleWindowSizeForDpi(int width, int height, uint dpi) => new(
        (int)Math.Ceiling(width * Math.Max(dpi, 96) / 96d),
        (int)Math.Ceiling(height * Math.Max(dpi, 96) / 96d));

    private void ApplyWindowMaterial()
    {
        QuickTranslateWindowMaterial material = QuickTranslateWindowMaterialParser.Parse(
            _settings.QuickTranslateWindowMaterial);

        // MicaBackdrop needs the Windows 11 DWM backdrop pipeline. On Windows 10 it leaves
        // the window with no background at all, so fall back to acrylic, which is supported.
        if (material == QuickTranslateWindowMaterial.Mica &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            material = QuickTranslateWindowMaterial.Acrylic;
        }

        SystemBackdrop = material switch
        {
            QuickTranslateWindowMaterial.Solid => null,
            QuickTranslateWindowMaterial.Acrylic => null,
            _ => new MicaBackdrop()
        };

        RootLayout.Background = material == QuickTranslateWindowMaterial.Acrylic
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(1, 255, 255, 255))
            : null;

        if (material == QuickTranslateWindowMaterial.Acrylic)
        {
            ApplyNativeAcrylicBackdrop();
        }
    }

    private void ApplyNativeAcrylicBackdrop()
    {
        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = AcrylicGradientColor,
            AnimationId = 0
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = data,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(handle, ref attribute);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        await TranslateAsync();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviders) return;

        TranslationProviderProfile? profile = SelectedProvider();
        if (profile == null) return;
        _settings.SelectedTranslationProviderId = profile.Id;
        _settings.Save(_settingsPath);
    }

    private void AlwaysOnTopButton_Checked(object sender, RoutedEventArgs e)
    {
        SetAlwaysOnTop(true);
    }

    private void AlwaysOnTopButton_Unchecked(object sender, RoutedEventArgs e)
    {
        SetAlwaysOnTop(false);
    }

    private void SetAlwaysOnTop(bool alwaysOnTop)
    {
        _isAlwaysOnTop = alwaysOnTop;
        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero) return;

        SetWindowPos(
            handle,
            alwaysOnTop ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
        ToolTipService.SetToolTip(AlwaysOnTopButton, alwaysOnTop ? "取消置顶" : "置顶窗口");
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter ||
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;
        await TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        string input = InputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowError("请输入要翻译的文本。");
            return;
        }

        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _translationCancellation.Token;

        TranslateButton.IsEnabled = false;
        StatusInfoBar.IsOpen = false;
        StatusInfoBar.Visibility = Visibility.Collapsed;
        try
        {
            string targetLanguage = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            TranslationProviderProfile? provider = SelectedProvider();
            if (provider == null)
            {
                ShowError("请先在设置中添加翻译服务商。");
                return;
            }
            ResultTextBox.Text = await TranslationService.TranslateAsync(
                input,
                targetLanguage,
                new TranslationConfiguration(
                    provider.Id,
                    provider.Provider,
                    provider.AppId,
                    provider.AppSecret,
                    provider.ApiBaseUrl,
                    provider.ExtraCredential,
                    provider.SystemPrompt),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
            ShowError("无法连接翻译服务，请检查网络后重试。");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                TranslateButton.IsEnabled = true;
            }
        }
    }

    private void ShowError(string message)
    {
        StatusInfoBar.Visibility = Visibility.Visible;
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
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

    private TranslationProviderProfile? SelectedProvider() =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as TranslationProviderProfile;

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr windowHandle,
        ref WindowCompositionAttributeData data);

}
