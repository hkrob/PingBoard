using System.Globalization;

namespace PingBoard.App;

/// <summary>
/// Silences desktop notifications for a while, or until switched back on.
/// <para>
/// Scope is deliberately narrow: this suppresses the toast and the tray balloon — the things that
/// interrupt you — and does <em>not</em> touch webhook or email alerting. Those exist precisely to
/// reach you when you are not at this machine, and silently dropping them because someone muted a
/// popup during maintenance would defeat the point. The menu says "desktop notifications" so the
/// boundary is visible rather than surprising.
/// </para>
/// <para>
/// <b>Wall clock, unusually.</b> Everywhere else durations use <see cref="Environment.TickCount64"/>
/// because an NTP correction must not corrupt an elapsed time. A mute deadline has to survive a
/// restart, though, and a monotonic tick does not: it resets to near zero at boot, so a persisted
/// one would either expire instantly or never. The trade is accepted knowingly — the worst case is
/// a mute that ends early or late after someone changes the system clock, against the alternative
/// of an indefinite mute that quietly lifts itself at the next restart.
/// </para>
/// </summary>
public static class NotificationMute
{
    private const string Indefinite = "indefinite";

    private static DateTimeOffset? _until;
    private static bool _forever;

    /// <summary>True while notifications should be suppressed. Expiry is evaluated on read.</summary>
    public static bool IsMuted
    {
        get
        {
            if (_forever) return true;
            if (_until is not { } until) return false;

            if (DateTimeOffset.Now < until) return true;

            // Lapsed — clear it so the button and the status bar stop claiming otherwise.
            _until = null;
            return false;
        }
    }

    public static void MuteFor(TimeSpan duration)
    {
        _forever = false;
        _until = DateTimeOffset.Now + duration;
    }

    public static void MuteIndefinitely()
    {
        _forever = true;
        _until = null;
    }

    public static void Unmute()
    {
        _forever = false;
        _until = null;
    }

    /// <summary>
    /// Short description for the status bar, or null when not muted. Shown continuously and on
    /// purpose: a monitor you forgot you silenced is worse than one that never alerted, because
    /// you are trusting it.
    /// </summary>
    public static string? Describe()
    {
        if (!IsMuted) return null;
        if (_forever) return "notifications muted";

        return _until is { } until
            ? "notifications muted until " + until.ToString("HH:mm", CultureInfo.CurrentCulture)
            : null;
    }

    /// <summary>Round-trips through the UI state file. Empty string means "not muted".</summary>
    public static string Serialize()
    {
        if (_forever) return Indefinite;
        return _until is { } until && DateTimeOffset.Now < until
            ? until.ToString("o", CultureInfo.InvariantCulture)
            : "";
    }

    public static void Restore(string value)
    {
        _forever = false;
        _until = null;

        if (value.Length == 0) return;

        if (value.Equals(Indefinite, StringComparison.OrdinalIgnoreCase))
        {
            _forever = true;
            return;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) && DateTimeOffset.Now < parsed)
        {
            _until = parsed;
        }
    }
}
