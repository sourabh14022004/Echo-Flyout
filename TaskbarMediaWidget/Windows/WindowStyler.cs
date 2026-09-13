using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarMediaWidget.Windows;

/// <summary>
/// Win32/DWM styling helpers: hiding a window from Alt+Tab/the taskbar app list, keeping it
/// topmost without stealing focus, and applying Windows 11 dark-mode/rounded-corner window
/// attributes. All calls are best-effort — DWM attributes that don't exist on the running Windows
/// build simply fail their HRESULT and are ignored rather than throwing.
///
/// Deliberately NOT using DWMWA_SYSTEMBACKDROP_TYPE (Mica/Acrylic): plain WPF windows aren't
/// hooked into DWM's composition pipeline the way native Win32/WinUI3 apps are, and applying a
/// system backdrop to one is a known incompatibility — it can leave the window's own WPF-rendered
/// content invisible, showing only a blank backdrop. The semi-transparent brush on the content
/// Border plus real DWM corner-rounding gets a close, reliable approximation without that risk.
/// </summary>
internal static class WindowStyler
{
    private const int GWL_EXSTYLE = -20;
    private const int GWL_HWNDPARENT = -8;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Marks the window as a tool window so it never shows up in Alt+Tab or as its own taskbar icon.</summary>
    public static void MakeToolWindow(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Owns the window by the given taskbar HWND (so DWM keeps its z-order tied to the taskbar's,
    /// e.g. during "show desktop") and re-asserts topmost without stealing focus or moving it —
    /// call this after every reposition.
    /// </summary>
    public static void PinAboveTaskbar(Window window, IntPtr taskbarHwnd)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || taskbarHwnd == IntPtr.Zero) return;

        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWL_HWNDPARENT, taskbarHwnd);
        }
        else
        {
            SetWindowLong(hwnd, GWL_HWNDPARENT, taskbarHwnd.ToInt32());
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// The window's actual on-screen rect in physical pixels — the same coordinate space
    /// SHAppBarMessage/GetWindowRect report the taskbar in, so the two can be compared directly
    /// to verify the widget is exactly flush with the bar rather than eyeballing it.
    /// </summary>
    public static Rect GetPhysicalBounds(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return Rect.Empty;

        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Applies real Windows 11 DWM corner rounding to a window (safe/reliable on WPF, unlike system backdrop types).</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Toggles the DWM immersive dark-mode frame (affects backdrop tinting) to match system theme.</summary>
    public static void SetDarkMode(Window window, bool isDark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
