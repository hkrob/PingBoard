using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PingBoard.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    public static MainWindow? Window { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An unhandled exception in WinUI 3 can terminate the process with no dialog and no log
        // entry. Wiring this on day one turns a silent disappearance into something diagnosable.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            CrashLog.Write(e.Exception);
            ShowCrashAndExit(e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write(ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        _window = new MainWindow();
        Window = _window;

        // Autostart launches straight to the tray. The window is constructed either way — the
        // engine and the tray icon live behind it — it simply never gets shown.
        if (Program.StartMinimized) _window.StartHidden();
        else _window.Activate();
    }

    /// <summary>
    /// Brings the existing window forward. Called when a second launch is redirected here by the
    /// single-instance guard in <see cref="Program"/>.
    /// </summary>
    public void OnRedirected(AppActivationArguments _)
    {
        _window?.BringToFront();
    }

    private void ShowCrashAndExit(Exception ex)
    {
        try
        {
            _window?.ShowFatalError(ex);
        }
        catch (Exception)
        {
            Exit();
        }
    }
}
