using Microsoft.Win32;

namespace PingBoard.App;

/// <summary>
/// Registers the app to start when the user signs in, via the per-user <c>Run</c> key.
/// <para>
/// HKCU rather than HKLM, and no scheduled task: writing to the current user's hive needs no
/// elevation, so the toggle works from the menu without a UAC prompt and without the installer
/// having to run as administrator. A monitor you have to remember to launch is a monitor that is
/// not running on the morning something breaks.
/// </para>
/// <para>
/// The command line deliberately carries no <c>--config</c>. The app already falls back to
/// <c>LastConfigPath</c> from its UI state, so a board switched after autostart was enabled is
/// still the one that opens at login — pinning the path here would go stale the moment the user
/// opened a different board.
/// </para>
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PingBoard";

    /// <summary>
    /// Started by the autostart entry, so the window stays hidden and only the tray icon appears.
    /// Popping a window open on every login is how a background tool earns itself a right-click
    /// and a disable.
    /// </summary>
    public const string MinimizedSwitch = "--minimized";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Turns autostart on or off.
    /// </summary>
    /// <returns>Null on success, otherwise a message for the banner — a locked-down machine can
    /// deny this, and silently failing would leave the menu showing a state that is not real.</returns>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return "Could not open the Windows startup registry key.";

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            if (ExecutablePath() is not { Length: > 0 } exe)
                return "Could not determine the application path, so autostart was not enabled.";

            // Quoted: the install path contains spaces under Program Files, and an unquoted Run
            // value would be parsed as a different executable plus arguments.
            key.SetValue(ValueName, $"\"{exe}\" {MinimizedSwitch}", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                     or UnauthorizedAccessException
                                     or IOException)
        {
            return "Could not change the startup setting — " + ex.Message;
        }
    }

    /// <summary>
    /// The real executable, not the managed assembly. <see cref="Environment.ProcessPath"/> is the
    /// apphost under self-contained deployment, which is what actually has to be launched.
    /// </summary>
    private static string? ExecutablePath() => Environment.ProcessPath;
}
