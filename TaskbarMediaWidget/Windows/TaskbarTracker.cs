using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TaskbarMediaWidget.Windows;

/// <summary>
/// Tracks one taskbar's live geometry, edge, and whether it is actually on screen.
///
/// Split of responsibilities: <c>SHAppBarMessage</c> is authoritative for the docked <em>edge</em>,
/// while the live rect comes from <c>GetWindowRect</c> on the taskbar window — the appbar rect is
/// only the taskbar's reserved area and stays put when auto-hide slides the bar away, so it can't
/// be used to tell whether the bar is showing.
///
/// Updates are raised via <see cref="TaskbarChanged"/> from a scoped WinEventHook on Explorer (for
/// near-instant reaction to move/resize/show/hide) plus a low-frequency safety-net poll, since an
/// auto-hide slide doesn't reliably raise a location-changed event on every Windows build.
/// </summary>
public sealed class TaskbarTracker : IDisposable
{
    private const int ABM_GETSTATE = 0x00000004;
    private const int ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABS_AUTOHIDE = 0x0000001;

    // Hook range is [SHOW .. LOCATIONCHANGE], which also covers HIDE (0x8003) and REORDER (0x8004).
    // REORDER is the one that fires on Win+D, when the shell reshuffles the z-order.
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // An auto-hidden taskbar slides almost entirely off-screen, leaving a sliver. If this little
    // of it (physical pixels, measured perpendicular to its edge) still overlaps the monitor,
    // it isn't really showing.
    private const int VisibleThresholdPx = 4;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>Raised whenever the tracked taskbar's geometry, edge, or visibility changes.</summary>
    public event EventHandler<TaskbarInfo?>? TaskbarChanged;

    /// <summary>
    /// Raised on <em>every</em> refresh — hook or poll — whether or not anything changed.
    /// Needed for z-order: the shell reshuffles z-order on Win+D without moving the taskbar, so a
    /// change-gated event never fires and the widget would be left sitting behind the bar.
    /// </summary>
    public event EventHandler<TaskbarInfo?>? Refreshed;

    private readonly Func<IntPtr> _resolveTargetMonitor;
    private readonly DispatcherTimer _timer;
    private readonly WinEventDelegate _winEventProc;

    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _hookedExplorerHwnd = IntPtr.Zero;
    private TaskbarInfo? _last;

    /// <param name="resolveTargetMonitor">
    /// Called each refresh to determine which monitor's taskbar to track (e.g. from
    /// <see cref="Settings.AppSettings"/>). Return <see cref="IntPtr.Zero"/> for "primary".
    /// </param>
    public TaskbarTracker(Func<IntPtr> resolveTargetMonitor)
    {
        _resolveTargetMonitor = resolveTargetMonitor;
        _winEventProc = OnWinEvent;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        EnsureHook();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        RemoveHook();
    }

    public void Dispose() => Stop();

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        // Explorer's process hosts many windows; only react to top-level taskbar-shaped ones.
        if (idObject != 0 /* OBJID_WINDOW */) return;
        Refresh();
    }

    private void EnsureHook()
    {
        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero)
        {
            RemoveHook();
            return;
        }

        if (_hook != IntPtr.Zero && _hookedExplorerHwnd == shellTray && IsWindow(_hookedExplorerHwnd))
        {
            return; // already hooked to the current Explorer instance
        }

        RemoveHook();

        GetWindowThreadProcessId(shellTray, out uint explorerPid);
        if (explorerPid == 0) return;

        _hook = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, explorerPid, 0, WINEVENT_OUTOFCONTEXT);
        _hookedExplorerHwnd = shellTray;
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _hookedExplorerHwnd = IntPtr.Zero;
    }

    private void Refresh()
    {
        EnsureHook(); // self-heals across explorer.exe restarts

        IntPtr targetMonitor = _resolveTargetMonitor();
        if (targetMonitor == IntPtr.Zero) targetMonitor = DisplayManager.PrimaryMonitor;

        IntPtr taskbarHwnd = FindTaskbarForMonitor(targetMonitor);
        if (taskbarHwnd == IntPtr.Zero)
        {
            PublishIfChanged(null);
            Refreshed?.Invoke(this, null);
            return;
        }

        var info = QueryTaskbar(taskbarHwnd, targetMonitor);
        PublishIfChanged(info);
        Refreshed?.Invoke(this, info);
    }

    private void PublishIfChanged(TaskbarInfo? info)
    {
        if (info == _last) return;
        _last = info;
        TaskbarChanged?.Invoke(this, info);
    }

    /// <summary>
    /// Finds the taskbar window that lives on <paramref name="monitor"/> — the primary
    /// "Shell_TrayWnd", or one of the per-monitor "Shell_SecondaryTrayWnd" instances when
    /// "show taskbar on all displays" is enabled.
    /// </summary>
    private static IntPtr FindTaskbarForMonitor(IntPtr monitor)
    {
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && DisplayManager.MonitorOf(primary) == monitor)
        {
            return primary;
        }

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (DisplayManager.MonitorOf(secondary) == monitor)
            {
                return secondary;
            }
        }

        // Fall back to the primary taskbar rather than showing nothing if monitor resolution
        // is momentarily stale (e.g. a display was just unplugged).
        return primary;
    }

    private static TaskbarInfo QueryTaskbar(IntPtr taskbarHwnd, IntPtr monitor)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = taskbarHwnd
        };
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);

        var stateData = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = taskbarHwnd };
        IntPtr state = SHAppBarMessage(ABM_GETSTATE, ref stateData);
        bool autoHideEnabled = ((uint)state.ToInt64() & ABS_AUTOHIDE) != 0;

        // The edge is the one thing the appbar API is authoritative about.
        var edge = data.uEdge switch
        {
            0 => TaskbarEdge.Left,
            1 => TaskbarEdge.Top,
            2 => TaskbarEdge.Right,
            _ => TaskbarEdge.Bottom
        };

        // Geometry, however, must come from the window rect. ABM_GETTASKBARPOS reports the
        // taskbar's *docked reservation*, which does NOT shrink or move when auto-hide slides the
        // bar off-screen — the window rect is the only thing that actually tracks where the bar is.
        Rect dockedBounds = new(data.rc.Left, data.rc.Top,
            data.rc.Right - data.rc.Left, data.rc.Bottom - data.rc.Top);

        Rect bounds = GetWindowRect(taskbarHwnd, out RECT wr)
            ? new Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top)
            : dockedBounds;

        bool isCollapsed = !IsWindowVisible(taskbarHwnd)
            || VisibleThickness(bounds, monitor, edge) <= VisibleThresholdPx;

        // Only worth hit-testing the bar's location if it's on screen in the first place.
        bool isObscured = !isCollapsed && IsTaskbarObscured(taskbarHwnd, bounds);

        return new TaskbarInfo(taskbarHwnd, monitor, bounds, edge, autoHideEnabled, isCollapsed, isObscured);
    }

    /// <summary>How much of the taskbar still overlaps its monitor, measured across its docked edge.</summary>
    private static double VisibleThickness(Rect taskbar, IntPtr monitor, TaskbarEdge edge)
    {
        Rect monitorBounds = DisplayManager.GetMonitorBoundsPhysical(monitor);
        if (monitorBounds.IsEmpty) return edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? taskbar.Height : taskbar.Width;

        Rect visible = Rect.Intersect(taskbar, monitorBounds);
        if (visible.IsEmpty) return 0;

        return edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? visible.Height : visible.Width;
    }

    /// <summary>
    /// Whether something is covering the taskbar (a fullscreen video, a game, any topmost window).
    ///
    /// Asks the question directly — "what window is actually on screen at the middle of the bar?"
    /// — rather than trying to infer it. Heuristics for this are unreliable in both directions:
    /// comparing the foreground window's rect to the monitor false-positives on merely *maximized*
    /// windows (Windows inflates their rect by the invisible resize border), and
    /// SHQueryUserNotificationState reports fullscreen for borderless-fullscreen editors that
    /// aren't covering the bar at all. Hit-testing the bar's own location has neither problem and
    /// covers every cause of occlusion with one check.
    /// </summary>
    private static bool IsTaskbarObscured(IntPtr taskbarHwnd, Rect taskbarBounds)
    {
        var midpoint = new POINT
        {
            X = (int)(taskbarBounds.Left + taskbarBounds.Width / 2),
            Y = (int)(taskbarBounds.Top + taskbarBounds.Height / 2)
        };

        IntPtr atPoint = WindowFromPoint(midpoint);
        if (atPoint == IntPtr.Zero) return true;

        IntPtr root = GetAncestor(atPoint, GA_ROOT);
        if (root == taskbarHwnd) return false;

        // Win11 hosts the bar's buttons in XAML-island windows that aren't children of
        // Shell_TrayWnd, so also accept anything belonging to the taskbar's own process — and to
        // ours, since our widget sits on the bar and would otherwise report the bar as covered.
        GetWindowThreadProcessId(root, out uint rootPid);
        GetWindowThreadProcessId(taskbarHwnd, out uint taskbarPid);

        return rootPid != taskbarPid && rootPid != GetCurrentProcessId();
    }

    /// <summary>
    /// Best-effort lookup of the notification-area (tray/clock) rect within the given taskbar, in
    /// physical pixels, so the widget can dock flush against it. Purely a placement refinement —
    /// callers must fall back gracefully when this returns null (it's not used for visibility or
    /// core geometry, which come from the authoritative SHAppBarMessage query above).
    /// </summary>
    public static Rect? TryGetNotifyAreaBounds(IntPtr taskbarHwnd)
    {
        if (taskbarHwnd == IntPtr.Zero) return null;

        IntPtr trayNotify = FindWindowEx(taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero) return null;

        if (!GetWindowRect(trayNotify, out RECT rect)) return null;

        return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
