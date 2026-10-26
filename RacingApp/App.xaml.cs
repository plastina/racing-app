using System.Threading;
using System.Windows;

namespace RacingApp;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\RacingStudio.SingleInstance";
    private const string ShowEventName = @"Local\RacingStudio.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--apply-saved"))
        {
            // Headless: apply and exit (kept for scripts; the logon task now uses --tray).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await StartupApplier.RunAsync();
            Shutdown(code);
            return;
        }

        // One instance only: two apps would fight over the same LED controllers. A second launch just brings
        // the running one to the front.
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        bool startInTray = e.Args.Contains("--tray");
        var window = new MainWindow { StartInTray = startInTray };
        if (startInTray)
        {
            // Avoid a flash while the window initialises; OnLoaded hides it.
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
        }

        window.Show();
        SessionEnding += (_, _) => window.PrepareForSessionEnd();

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        })
        { IsBackground = true, Name = "RacingStudio.ShowListener" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (second instance); nothing to release.
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
