using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace TaskbarMediaWidget.Windows;

/// <summary>
/// Monitor enumeration and per-monitor DPI lookup, using raw Win32 (no System.Windows.Forms
/// dependency). Physical pixel rects (from SHAppBarMessage/GetWindowRect) are always device
/// pixels regardless of DPI awareness mode, so callers must divide by a monitor's own DPI to get
/// WPF DIPs — that's what <see cref="ToDips"/> is for.
/// </summary>
internal static class DisplayManager
{
    private const uint MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    public static IntPtr PrimaryMonitor => MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTONEAREST);

    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>Returns every monitor's handle currently attached to the desktop.</summary>
    public static List<IntPtr> EnumerateMonitors()
    {
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            monitors.Add(hMonitor);
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>DPI scale factor (1.0 == 96 DPI) for the given monitor.</summary>
    public static double GetScaleFactor(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero) return 1.0;

        if (GetDpiForMonitor(monitorHandle, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    /// <summary>Converts a physical-pixel rect to WPF DIPs using the given monitor's DPI.</summary>
    public static Rect ToDips(Rect physicalPixels, IntPtr monitorHandle)
    {
        double scale = GetScaleFactor(monitorHandle);
        if (scale <= 0) scale = 1.0;

        return new Rect(
            physicalPixels.X / scale,
            physicalPixels.Y / scale,
            physicalPixels.Width / scale,
            physicalPixels.Height / scale);
    }

    /// <summary>
    /// Resolves a monitor by its top-left corner in virtual-desktop coordinates — the one monitor
    /// identity both this Win32 code and Electron's screen.getAllDisplays() can produce and match
    /// without reconciling device-name formats between the two processes.
    /// </summary>
    public static IntPtr? FindMonitorByPosition(int x, int y)
    {
        foreach (IntPtr handle in EnumerateMonitors())
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info) && info.rcMonitor.Left == x && info.rcMonitor.Top == y)
            {
                return handle;
            }
        }
        return null;
    }

    /// <summary>Full monitor bounds (including the taskbar) in physical pixels — the same space window rects use.</summary>
    public static Rect GetMonitorBoundsPhysical(IntPtr monitorHandle)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitorHandle, ref info)) return Rect.Empty;

        return new Rect(info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
    }

    /// <summary>Full monitor bounds (including the taskbar) in DIPs.</summary>
    public static Rect GetMonitorBounds(IntPtr monitorHandle)
    {
        Rect physical = GetMonitorBoundsPhysical(monitorHandle);
        return physical.IsEmpty ? Rect.Empty : ToDips(physical, monitorHandle);
    }

    /// <summary>Monitor work-area bounds (excludes the taskbar on whichever edge it's docked) in DIPs.</summary>
    public static Rect GetWorkAreaBounds(IntPtr monitorHandle)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitorHandle, ref info)) return Rect.Empty;

        var physical = new Rect(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        return ToDips(physical, monitorHandle);
    }
}
