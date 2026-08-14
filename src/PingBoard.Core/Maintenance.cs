using System.Globalization;

namespace PingBoard.Core;

/// <summary>
/// Scheduled quiet hours for a target: probing continues, alerting does not.
/// <para>
/// <b>Probing deliberately keeps running.</b> A maintenance window says "I already know about
/// this", not "stop watching" — the board still shows what happened and the history still records
/// it, so you can see afterwards whether the host came back when it should have. Stopping the
/// probes would throw that away for no gain.
/// </para>
/// <para>
/// Written as <c>[days ]HH:mm-HH:mm</c>, comma-separated:
/// <code>
/// Maintenance=02:00-04:00
/// Maintenance=Sat 22:00-02:00, Sun 03:00-05:00
/// Maintenance=Mon-Fri 01:30-02:00
/// </code>
/// A window whose end is before its start runs through midnight.
/// </para>
/// </summary>
public sealed class MaintenanceSchedule
{
    private readonly List<Window> _windows = [];

    /// <summary>The text this was parsed from, so it round-trips through the config unchanged.</summary>
    public string Raw { get; private init; } = "";

    public bool IsEmpty => _windows.Count == 0;

    public static readonly MaintenanceSchedule None = new();

    /// <summary>
    /// Parses a schedule, ignoring anything malformed. A typo silences nothing rather than
    /// silencing everything — failing open would be the dangerous direction for a monitor.
    /// </summary>
    public static MaintenanceSchedule Parse(string? text)
    {
        var raw = text?.Trim() ?? "";
        var schedule = new MaintenanceSchedule { Raw = raw };

        if (raw.Length == 0) return schedule;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryParseWindow(part, out var window))
                schedule._windows.Add(window);

        return schedule;
    }

    /// <summary>True when <paramref name="when"/> falls inside any window. Local time throughout.</summary>
    public bool Contains(DateTimeOffset when)
    {
        if (_windows.Count == 0) return false;

        var local = when.LocalDateTime;
        var minutes = (local.Hour * 60) + local.Minute;
        var day = local.DayOfWeek;

        foreach (var window in _windows)
            if (window.Contains(day, minutes))
                return true;

        return false;
    }

    private static bool TryParseWindow(string text, out Window window)
    {
        window = default;

        // Optional day prefix, separated from the times by whitespace.
        var days = AllDays;
        var span = text;

        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            if (!TryParseDays(text[..lastSpace], out days)) return false;
            span = text[(lastSpace + 1)..];
        }

        var dash = span.IndexOf('-');
        if (dash <= 0) return false;

        if (!TryParseTime(span[..dash], out var start)) return false;
        if (!TryParseTime(span[(dash + 1)..], out var end)) return false;

        // Equal start and end would be a zero-length window; treat it as a mistake rather than as
        // a 24-hour silence, which is the more damaging reading.
        if (start == end) return false;

        window = new Window(days, start, end);
        return true;
    }

    private static bool TryParseTime(string text, out int minutes)
    {
        minutes = 0;
        var trimmed = text.Trim();

        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return false;

        if (!int.TryParse(trimmed[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
            return false;

        if (!int.TryParse(trimmed[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
            return false;

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;

        minutes = (hour * 60) + minute;
        return true;
    }

    private const byte AllDays = 0b0111_1111;

    private static bool TryParseDays(string text, out byte mask)
    {
        mask = 0;

        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');

            if (dash > 0)
            {
                if (!TryParseDay(token[..dash], out var from)) return false;
                if (!TryParseDay(token[(dash + 1)..], out var to)) return false;

                // Inclusive, and wraps: "Fri-Mon" is Friday through Monday.
                for (var day = from; ; day = (day + 1) % 7)
                {
                    mask |= (byte)(1 << day);
                    if (day == to) break;
                }
            }
            else
            {
                if (!TryParseDay(token, out var day)) return false;
                mask |= (byte)(1 << day);
            }
        }

        return mask != 0;
    }

    private static bool TryParseDay(string text, out int day)
    {
        day = text.Trim().ToLowerInvariant() switch
        {
            "sun" or "sunday" => 0,
            "mon" or "monday" => 1,
            "tue" or "tues" or "tuesday" => 2,
            "wed" or "weds" or "wednesday" => 3,
            "thu" or "thur" or "thurs" or "thursday" => 4,
            "fri" or "friday" => 5,
            "sat" or "saturday" => 6,
            _ => -1,
        };

        return day >= 0;
    }

    /// <param name="Days">Bit per day, Sunday is bit 0.</param>
    /// <param name="StartMinutes">Minutes past local midnight, inclusive.</param>
    /// <param name="EndMinutes">Minutes past local midnight, exclusive.</param>
    private readonly record struct Window(byte Days, int StartMinutes, int EndMinutes)
    {
        public bool Contains(DayOfWeek day, int minutes)
        {
            if (StartMinutes < EndMinutes)
                return IsDay(day) && minutes >= StartMinutes && minutes < EndMinutes;

            // Runs through midnight. The evening half belongs to the named day; the morning half
            // belongs to the day after, which is what "Sat 22:00-02:00" plainly means.
            if (minutes >= StartMinutes) return IsDay(day);

            return minutes < EndMinutes && IsDay(Previous(day));
        }

        private bool IsDay(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

        private static DayOfWeek Previous(DayOfWeek day) => (DayOfWeek)(((int)day + 6) % 7);
    }
}
