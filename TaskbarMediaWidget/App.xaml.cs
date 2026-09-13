using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using TaskbarMediaWidget.Settings;

namespace TaskbarMediaWidget;

/// <summary>
/// Entry point for the whole app. Launched normally it opens the dashboard; launched with
/// <c>--startup</c> (the Run-key entry) it comes up silently in the tray with the saved settings
/// already applied, so a reboot needs nothing re-enabled.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TaskbarMediaWidget.SingleInstance";
    private const string ShowDashboardEventName = @"Local\TaskbarMediaWidget.ShowDashboard";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showDashboardSignal;
    private MainWindow? _widgetHost;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"AppDomain unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogException("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool silentStart = e.Args.Any(arg =>
            string.Equals(arg, StartupManager.StartupArgument, StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask the live instance to surface its dashboard, then bow out, so
            // double-clicking the exe again feels like re-opening the app rather than doing nothing.
            Logger.Log("Startup: another instance is already running — signalling it and exiting.");
            if (EventWaitHandle.TryOpenExisting(ShowDashboardEventName, out EventWaitHandle? existing))
            {
                existing.Set();
                existing.Dispose();
            }
            Shutdown();
            return;
        }

        _showDashboardSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDashboardEventName);
        ListenForShowDashboardRequests();

        // MainWindow hosts the taskbar strip *and* every background service, and its Loaded
        // handler is what starts them — Loaded only fires once the window is shown. It stays
        // invisible because RootBorder.Opacity starts at 0, so this Show() must not be removed.
        _widgetHost = new MainWindow();
        _widgetHost.Show();

        Logger.Log($"Startup: silent={silentStart}");
        if (!silentStart)
        {
            _widgetHost.OpenSettingsDashboard();
        }
    }

    /// <summary>Watches for a second launch asking us to bring the dashboard up.</summary>
    private void ListenForShowDashboardRequests()
    {
        var listener = new Thread(() =>
        {
            try
            {
                while (_showDashboardSignal!.WaitOne())
                {
                    Dispatcher.BeginInvoke(() => _widgetHost?.OpenSettingsDashboard());
                }
            }
            catch (Exception ex)
            {
                // Expected when the handle is torn down during shutdown.
                Logger.LogException("Single-instance listener stopped", ex);
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showDashboardSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.LogException("Dispatcher unhandled exception", e.Exception);
        // Log and continue rather than let a single bad event handler take down a background widget.
        e.Handled = true;
    }
}
