using System;
using System.Windows;

namespace TaskbarMediaWidget.Windows;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

/// <summary>
/// Snapshot of one taskbar's geometry/state, in physical (device) pixels. Consumers convert to
/// DIPs using the DPI of <see cref="MonitorHandle"/> (see <see cref="DisplayManager"/>).
/// </summary>
/// <param name="Bounds">The taskbar's <em>live</em> on-screen rect (from GetWindowRect), not its docked reservation.</param>
/// <param name="IsCollapsed">Auto-hide has slid the bar off-screen, so it isn't actually showing.</param>
/// <param name="IsObscured">Something (fullscreen video, a game, any topmost window) is covering the bar.</param>
public sealed record TaskbarInfo(
    IntPtr Hwnd,
    IntPtr MonitorHandle,
    Rect Bounds,
    TaskbarEdge Edge,
    bool AutoHideEnabled,
    bool IsCollapsed,
    bool IsObscured)
{
    /// <summary>The widget rides on the taskbar, so it may only show when the taskbar itself is on screen.</summary>
    public bool IsVisible => !IsCollapsed && !IsObscured;
}
