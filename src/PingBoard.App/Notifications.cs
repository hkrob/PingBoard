using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace PingBoard.App;

/// <summary>
/// Windows toast notifications via the Windows App SDK, with a guaranteed fallback.
/// <para>
/// <b>Why the fallback exists.</b> <c>AppNotificationManager.Register</c> fails under a
/// <em>self-contained</em> Windows App SDK deployment: it needs
/// <c>Microsoft.WindowsAppRuntime.Insights.Resource.dll</c>, which ships with the installed
/// framework runtime and is not part of the self-contained payload (it is not in any NuGet package
/// — only a header is). Verified on this machine: the call throws <c>0x8007007E</c> even though
/// <c>Microsoft.WindowsAppRuntime.2</c> 2.3.1 is registered, because a self-contained app does not
/// use it.
/// </para>
/// <para>
/// Rather than give up self-contained deployment — the whole point of which is that the output
/// folder runs on a clean machine with nothing installed — callers fall back to a tray balloon.
/// On Windows 10 and 11 the shell renders balloon tips as ordinary toasts and files them in the
/// notification centre, so the user-visible result is the same.
/// </para>
/// <para>
/// Notifications fire on state transitions only — never on individual failed probes — and the
/// engine suppresses transitions entirely while suspended, so a wake from sleep is silent.
/// </para>
/// </summary>
public static class Notifications
{
    private static bool _registered;
    private static bool _unavailable;

    /// <summary>True when the richer Windows App SDK path is usable.</summary>
    public static bool ToastsAvailable => _registered;

    public static void Initialize()
    {
        if (_registered || _unavailable) return;

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Expected under self-contained deployment; recorded once, then we use the balloon.
            CrashLog.Write(ex);
            _unavailable = true;
        }
    }

    /// <summary>
    /// Shows a toast. Returns false when the platform path is unavailable, so the caller can fall
    /// back to the tray balloon instead of the notification silently disappearing.
    /// </summary>
    public static bool Show(string title, string body)
    {
        Initialize();
        if (!_registered) return false;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return false;
        }
    }

    public static void Shutdown()
    {
        if (!_registered) return;

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }
        finally
        {
            _registered = false;
        }
    }
}
