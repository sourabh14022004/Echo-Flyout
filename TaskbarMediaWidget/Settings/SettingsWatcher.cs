using System;
using System.IO;
using System.Windows.Threading;

namespace TaskbarMediaWidget.Settings;

/// <summary>
/// Watches settings.json for changes made by the separate Electron dashboard and reloads live.
/// This is the entire sync mechanism between the two apps — no IPC, just a shared file. Writers
/// (editors, Node's fs.writeFile) commonly produce multiple filesystem events per logical save,
/// so changes are debounced onto a short timer rather than reloading on every raw event.
/// </summary>
public sealed class SettingsWatcher : IDisposable
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(150);

    public event EventHandler? SettingsChanged;

    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounceTimer;
    private readonly Dispatcher _dispatcher;

    public SettingsWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };

        string path = AppSettings.SettingsPath;
        string? dir = Path.GetDirectoryName(path);
        if (dir == null) return;

        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            // FileSystemWatcher events fire on a threadpool thread — DispatcherTimer.Start/Stop
            // must be called from the UI thread that owns it.
            _watcher.Changed += (_, _) => _dispatcher.BeginInvoke(RestartDebounce);
            _watcher.Created += (_, _) => _dispatcher.BeginInvoke(RestartDebounce);
        }
        catch
        {
            // No watcher, no live-reload — settings still work via the normal Load()/Save() path.
        }
    }

    private void RestartDebounce()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _watcher?.Dispose();
    }
}
