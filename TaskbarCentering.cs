using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarInfo;

/// <summary>
/// Windows 10's taskbar is a classic Win32 rebar, unlike Windows 11's XAML taskbar.
/// The task buttons all live inside a single MSTaskListWClass window, so centring them
/// means resizing and repositioning that window rather than moving individual buttons.
/// Sizes and positions here are physical pixels to match GetWindowRect/MoveWindow.
/// </summary>
public static class TaskbarCentering
{
    private const int ObjIdClient = -4;
    private static readonly Guid AccessibleIid = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static IntPtr FindTaskbarWindow() =>
        UnmanagedMethods.FindWindow("Shell_TrayWnd", null);

    public static IntPtr FindTaskListWindow(IntPtr taskbarWindow)
    {
        if (taskbarWindow == IntPtr.Zero) return IntPtr.Zero;

        IntPtr result = IntPtr.Zero;
        UnmanagedMethods.EnumChildWindows(taskbarWindow, (window, _) =>
        {
            var buffer = new StringBuilder(256);
            if (UnmanagedMethods.GetClassName(window, buffer, buffer.Capacity) > 0 &&
                buffer.ToString() == "MSTaskListWClass")
            {
                result = window;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindTaskSwitcherWindow(IntPtr taskbarWindow)
    {
        if (taskbarWindow == IntPtr.Zero) return IntPtr.Zero;

        IntPtr result = IntPtr.Zero;
        UnmanagedMethods.EnumChildWindows(taskbarWindow, (window, _) =>
        {
            var buffer = new StringBuilder(256);
            if (UnmanagedMethods.GetClassName(window, buffer, buffer.Capacity) > 0 &&
                buffer.ToString() == "MSTaskSwWClass")
            {
                result = window;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Returns the horizontal span of the visible task buttons in screen coordinates.
    /// The buttons are not exposed through toolbar messages on Windows 10, so their
    /// positions come from hit-testing the task list, because the buttons are only
    /// exposed through a late-bound accessibility proxy that managed code cannot drive.
    /// </summary>
    public static bool TryGetButtonSpan(IntPtr taskListWindow, out int left, out int right)
    {
        left = 0;
        right = 0;
        if (taskListWindow == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;

        object? accessibleObject = null;
        Guid iid = AccessibleIid;
        int hr = AccessibleObjectFromWindow(taskListWindow, ObjIdClient, ref iid, out accessibleObject);
        if (hr != 0 || accessibleObject is null)
        {
            DebugLog($"AccessibleObjectFromWindow hr=0x{hr:X8}");
            return false;
        }

        try
        {
            // Late-bound dispatch mirrors what the accessibility tools do: accLocation
            // takes four out-parameters, which dynamic binding marshals correctly.
            dynamic accessible = accessibleObject;
            int childCount = accessible.accChildCount;
            if (childCount <= 0)
            {
                DebugLog($"accChildCount={childCount}");
                return false;
            }

            int minLeft = int.MaxValue;
            int maxRight = int.MinValue;

            for (int i = 1; i <= childCount; i++)
            {
                try
                {
                    object x = 0;
                    object y = 0;
                    object width = 0;
                    object height = 0;
                    accessible.accLocation(ref x, ref y, ref width, ref height, i);

                    int childLeft = Convert.ToInt32(x);
                    int childWidth = Convert.ToInt32(width);
                    if (childWidth <= 0) continue;
                    if (childLeft < minLeft) minLeft = childLeft;
                    if (childLeft + childWidth > maxRight) maxRight = childLeft + childWidth;
                }
                catch
                {
                    // Hidden or transient children expose no location.
                }
            }

            if (maxRight <= minLeft)
            {
                DebugLog($"span invalid: childCount={childCount} {minLeft}..{maxRight}");
                return false;
            }

            left = minLeft;
            right = maxRight;
            return true;
        }
        catch (Exception ex)
        {
            DebugLog($"TryGetButtonSpan failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (Marshal.IsComObject(accessibleObject)) Marshal.ReleaseComObject(accessibleObject);
        }
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        _ => 0
    };
    /// <summary>
    /// Diagnostic trace, compiled only in Debug builds. Writes to
    /// %LOCALAPPDATA%\TinyBar\taskbar-centering.log so layout problems can be reproduced
    /// on machines that cannot be attached to a debugger.
    /// </summary>
    [Conditional("DEBUG")]
    public static void DebugLog(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyBar",
                "taskbar-centering.log");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the feature.
        }
    }

    public static (int Left, int Top, int Width, int Height)? ComputeCenteredBounds(
        IntPtr taskbarWindow,
        IntPtr taskSwitcherWindow,
        IntPtr taskListWindow)
    {
        if (taskbarWindow == IntPtr.Zero || taskSwitcherWindow == IntPtr.Zero) return null;
        if (!UnmanagedMethods.GetWindowRect(taskbarWindow, out UnmanagedMethods.RECT taskbarRect)) return null;
        if (!UnmanagedMethods.GetWindowRect(taskSwitcherWindow, out UnmanagedMethods.RECT windowRect)) return null;

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        if (taskbarWidth <= 0 || taskbarHeight <= 0) return null;

        if (!TryGetButtonSpan(taskListWindow, out int contentLeft, out int contentRight))
        {
            DebugLog($"TryGetButtonSpan failed for list={taskListWindow}");
            return null;
        }

        int contentWidth = contentRight - contentLeft;
        if (contentWidth <= 0 || contentWidth > taskbarWidth) return null;

        // The buttons do not start exactly at the window edge; keep that inset on both sides.
        int inset = Math.Max(0, contentLeft - windowRect.Left);
        int width = Math.Min(taskbarWidth, contentWidth + inset * 2);
        int contentTargetLeft = (taskbarWidth - contentWidth) / 2;
        int left = contentTargetLeft - inset;

        int maxLeft = Math.Max(0, taskbarWidth - width);
        left = Math.Clamp(left, 0, maxLeft);
        return (left, windowRect.Top, width, taskbarHeight);
    }

    public static bool ApplyCenteredLayout((int Left, int Top, int Width, int Height) bounds, IntPtr taskSwitcherWindow)
    {
        if (taskSwitcherWindow == IntPtr.Zero) return false;

        // MoveWindow works in the parent's client coordinates, so convert from screen space.
        var point = new UnmanagedMethods.POINT { X = bounds.Left, Y = bounds.Top };
        IntPtr parent = UnmanagedMethods.GetParent(taskSwitcherWindow);
        if (parent == IntPtr.Zero) return false;
        if (!UnmanagedMethods.ScreenToClient(parent, ref point)) return false;

        return UnmanagedMethods.MoveWindow(
            taskSwitcherWindow,
            point.X,
            point.Y,
            bounds.Width,
            bounds.Height,
            true);
    }

    public static bool RestoreDefaultLayout(IntPtr taskbarWindow, IntPtr taskSwitcherWindow)
    {
        if (taskbarWindow == IntPtr.Zero || taskSwitcherWindow == IntPtr.Zero) return false;
        if (!UnmanagedMethods.GetWindowRect(taskbarWindow, out UnmanagedMethods.RECT taskbarRect)) return false;
        if (!UnmanagedMethods.GetWindowRect(taskSwitcherWindow, out UnmanagedMethods.RECT windowRect)) return false;

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

        // Windows 10 keeps the task list between the start/search area and the tray.
        IntPtr rebar = UnmanagedMethods.FindWindowEx(taskbarWindow, IntPtr.Zero, "ReBarWindow32", null);
        int left = windowRect.Left;
        if (rebar != IntPtr.Zero &&
            UnmanagedMethods.GetWindowRect(rebar, out UnmanagedMethods.RECT rebarRect))
        {
            left = rebarRect.Left + 2;
        }

        IntPtr tray = UnmanagedMethods.FindWindowEx(taskbarWindow, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = taskbarRect.Right;
        if (tray != IntPtr.Zero &&
            UnmanagedMethods.GetWindowRect(tray, out UnmanagedMethods.RECT trayRect))
        {
            right = trayRect.Left;
        }

        int width = Math.Max(1, right - left);
        return ApplyCenteredLayout((left, taskbarRect.Top, width, taskbarHeight), taskSwitcherWindow);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        int id,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessible
    {
        [PreserveSig] int get_accParent([MarshalAs(UnmanagedType.IDispatch)] out object parent);
        [PreserveSig] int get_accChildCount(out int childCount);
        [PreserveSig] int get_accChild(object varChild, [MarshalAs(UnmanagedType.IDispatch)] out object child);
        [PreserveSig] int get_accName(object varChild, [MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int get_accValue(object varChild, [MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int get_accDescription(object varChild, [MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int get_accRole(object varChild, [MarshalAs(UnmanagedType.Struct)] out object role);
        [PreserveSig] int get_accState(object varChild, [MarshalAs(UnmanagedType.Struct)] out object state);
        [PreserveSig] int get_accHelp(object varChild, [MarshalAs(UnmanagedType.BStr)] out string help);
        [PreserveSig] int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string helpFile, object varChild, out int topic);
        [PreserveSig] int get_accKeyboardShortcut(object varChild, [MarshalAs(UnmanagedType.BStr)] out string shortcut);
        [PreserveSig] int get_accFocus([MarshalAs(UnmanagedType.Struct)] out object focus);
        [PreserveSig] int get_accSelection([MarshalAs(UnmanagedType.Struct)] out object selection);
        [PreserveSig] int get_accDefaultAction(object varChild, [MarshalAs(UnmanagedType.BStr)] out string defaultAction);
        [PreserveSig] int accSelect(int flagsSelect, object varChild);
        [PreserveSig] int accLocation(out int left, out int top, out int width, out int height, object varChild);
        [PreserveSig] int accNavigate(int navDir, object varStart, [MarshalAs(UnmanagedType.Struct)] out object endUp);
        [PreserveSig] int accHitTest(int xLeft, int yTop, [MarshalAs(UnmanagedType.Struct)] out object child);
        [PreserveSig] int accDoDefaultAction(object varChild);
        [PreserveSig] int set_accName(object varChild, [MarshalAs(UnmanagedType.BStr)] string name);
        [PreserveSig] int set_accValue(object varChild, [MarshalAs(UnmanagedType.BStr)] string value);
    }
}
