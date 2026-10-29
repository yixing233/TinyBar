using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarInfo;

/// <summary>
/// Windows 11 exposes Mica through DWM_SYSTEMBACKDROP_TYPE, which does not exist on
/// Windows 10 (the call fails with E_INVALIDARG). The closest equivalent available here
/// is the undocumented SetWindowCompositionAttribute accent policy: acrylic blurs the
/// wallpaper behind the taskbar, which is what the Matter theme's translucent surface
/// relies on. Anything that needs real Mica or per-control XAML restyling cannot be
/// reproduced on this OS without hooking Explorer.
/// </summary>
public static class TaskbarAppearance
{
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentAcrylic = 4;

    /// <summary>White acrylic, matching the Matter theme's light translucent surface.</summary>
    public const byte DefaultOpacity = 200;
    public const int DefaultTintRgb = 0xFFFFFF;

    public static bool ApplyWhiteAcrylic(IntPtr window) =>
        ApplyAcrylic(window, DefaultOpacity, DefaultTintRgb);

    public static bool ApplyAcrylic(IntPtr window, byte opacity, int tintRgb)
    {
        if (window == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;

        int gradientColor = (opacity << 24) | (tintRgb & 0x00FFFFFF);
        return SetAccentPolicy(window, AccentAcrylic, gradientColor, 2);
    }

    public static bool ClearAcrylic(IntPtr window)
    {
        if (window == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;

        // Resetting the accent to "disabled" leaves the taskbar painted with an opaque
        // black backdrop, because Explorer's own accent policy is replaced rather than
        // restored. Toggling the system transparency flag makes the shell rebuild its
        // own policy, which is the only way to get the stock appearance back.
        return ForceShellAccentRebuild();
    }

    private static bool ForceShellAccentRebuild()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: true);
            if (key is null) return false;

            object? current = key.GetValue("EnableTransparency");
            int original = current is int value ? value : 1;

            // Only flip the flag; the shell repaints asynchronously, so blocking here would
            // freeze the UI without making the rebuild finish any sooner.
            key.SetValue("EnableTransparency", original == 1 ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("EnableTransparency", original, Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SetAccentPolicy(IntPtr window, int accentState, int gradientColor, int accentFlags)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(window, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
