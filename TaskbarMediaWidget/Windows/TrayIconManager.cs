using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarMediaWidget.Windows;

/// <summary>
/// The system tray icon — the app's only visible entry point to Settings/Exit, since the docked
/// widget itself is a tool window with no title bar or menu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    private readonly NotifyIcon _notifyIcon;
    private readonly IntPtr _iconHandle;

    public TrayIconManager()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        Icon icon = CreateIcon(out _iconHandle);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Echo Flyout",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Icon CreateIcon(out IntPtr handle)
    {
        handle = IntPtr.Zero;

        // 1. Try loading app mascot icon from WPF application resource pack URI
        try
        {
            var streamResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (streamResource?.Stream != null)
            {
                using var stream = streamResource.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch { }

        // 2. Try loading from executable icon embedded by .csproj
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                var exeIcon = Icon.ExtractAssociatedIcon(exePath);
                if (exeIcon != null)
                {
                    return exeIcon;
                }
            }
        }
        catch { }

        // 3. Fallback: Draw default programmatic icon
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 0, 103, 192));
            g.FillEllipse(background, 0, 0, 32, 32);

            using var foreground = new SolidBrush(Color.White);
            Point[] playTriangle = { new(12, 9), new(12, 23), new(24, 16) };
            g.FillPolygon(foreground, playTriangle);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }
}
