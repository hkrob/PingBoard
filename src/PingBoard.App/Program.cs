using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PingBoard.App;

/// <summary>
/// Custom entry point, replacing the XAML-generated one (see <c>DISABLE_XAML_GENERATED_MAIN</c>
/// in the project file) so single-instance redirection can run before the UI starts.
/// </summary>
public static class Program
{
    /// <summary>Config file chosen on the command line with <c>--config &lt;path&gt;</c>.</summary>
    public static string? RequestedConfigPath { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        RequestedConfigPath = ParseConfigArg(args);

        // Instance identity is keyed on the config file, so `--config a.ini` and `--config b.ini`
        // are legitimately separate instances while a second launch of the same board just
        // surfaces the window already running.
        if (RedirectToExistingInstance()) return 0;

        Application.Start(initParams =>
        {
            _ = initParams;
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

            // Application.Start owns the lifetime; constructing App registers it as Current.
            _ = new App();
        });

        return 0;
    }

    private static string? ParseConfigArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        return null;
    }

    private static bool RedirectToExistingInstance()
    {
        var key = "PingBoard:" + (RequestedConfigPath?.ToLowerInvariant() ?? "<default>");
        var instance = AppInstance.FindOrRegisterForKey(key);

        if (instance.IsCurrent)
        {
            instance.Activated += (_, e) =>
            {
                // Arrives on a background thread; hop to the UI thread before touching the window.
                if (Application.Current is App app)
                    App.Window?.DispatcherQueue.TryEnqueue(() => app.OnRedirected(e));
            };
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // RedirectActivationToAsync must not be awaited inline on the STA thread — doing so
        // deadlocks against the COM apartment. Hand it to the threadpool and block here instead.
        using var done = new ManualResetEventSlim(false);
        _ = Task.Run(async () =>
        {
            try { await instance.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(5));

        return true;
    }
}
