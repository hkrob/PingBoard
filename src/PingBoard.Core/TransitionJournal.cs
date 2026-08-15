using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Remembers recent transitions so the board can say what happened while nobody was looking.
/// <para>
/// This closes the one gap the rest of the design leaves open. Notifications fire on transitions
/// and are then gone; the CSV records everything but nobody opens it; the board itself shows the
/// present, not the last two hours. So a target that dropped for four minutes while the window was
/// in the tray, and recovered, leaves no trace anyone will ever see — which is precisely the event
/// worth knowing about, because an intermittent fault is the hard kind to catch.
/// </para>
/// <para>
/// Fixed capacity, like every other buffer here. Two hundred transitions is weeks of a healthy
/// board and still bounded on a flapping one.
/// </para>
/// </summary>
public sealed class TransitionJournal
{
    private const int Capacity = 200;

    private readonly Lock _gate = new();
    private readonly StateTransition[] _items = new StateTransition[Capacity];
    private int _next;
    private int _count;

    public void Add(in StateTransition transition)
    {
        lock (_gate)
        {
            _items[_next] = transition;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Transitions recorded at or after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<StateTransition> Since(DateTimeOffset since)
    {
        lock (_gate)
        {
            var result = new List<StateTransition>();
            var start = _count == Capacity ? _next : 0;

            for (var i = 0; i < _count; i++)
            {
                ref readonly var item = ref _items[(start + i) % Capacity];
                if (item.When >= since) result.Add(item);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// One line describing what happened between <paramref name="since"/> and
    /// <paramref name="now"/>, or <c>null</c> when nothing did.
    /// <para>
    /// Null rather than "nothing happened" on purpose. A message saying all was well is a message
    /// that costs attention and returns none, and this application spends its whole design budget
    /// on not doing that. Silence already means everything is fine.
    /// </para>
    /// </summary>
    /// <param name="maxNamed">
    /// How many targets to name before summarising the rest. A line listing forty hosts is not
    /// read, it is dismissed.
    /// </param>
    public string? Summarise(DateTimeOffset since, DateTimeOffset now, int maxNamed = 3)
    {
        var events = Since(since);
        if (events.Count == 0) return null;

        // Per target, in the order they first appeared, so the line reads chronologically.
        var order = new List<string>();
        var outages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastDown = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var stillDown = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var longest = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in events)
        {
            if (!outages.ContainsKey(e.TargetName))
            {
                order.Add(e.TargetName);
                outages[e.TargetName] = 0;
                stillDown[e.TargetName] = false;
                longest[e.TargetName] = TimeSpan.Zero;
            }

            if (e.Up)
            {
                stillDown[e.TargetName] = false;
                if (e.DownFor > longest[e.TargetName]) longest[e.TargetName] = e.DownFor;
            }
            else
            {
                outages[e.TargetName]++;
                stillDown[e.TargetName] = true;
                lastDown[e.TargetName] = e.When;
            }
        }

        var sb = new StringBuilder();
        sb.Append("While you were away (").Append(Duration(now - since)).Append("): ");

        var named = 0;

        foreach (var name in order)
        {
            if (named == maxNamed) break;
            if (named > 0) sb.Append("; ");

            sb.Append(name);

            if (stillDown[name])
            {
                sb.Append(" went down at ")
                  .Append(lastDown[name].ToString("HH:mm", CultureInfo.CurrentCulture))
                  .Append(" and is still down");
            }
            else if (outages[name] > 1)
            {
                sb.Append(" dropped ").Append(outages[name]).Append(" times, longest ")
                  .Append(Duration(longest[name])).Append(", now up");
            }
            else
            {
                sb.Append(" was down for ").Append(Duration(longest[name])).Append(", now up");
            }

            named++;
        }

        var remaining = order.Count - named;
        if (remaining > 0) sb.Append("; and ").Append(remaining).Append(remaining == 1 ? " other" : " others");

        sb.Append('.');
        return sb.ToString();
    }

    /// <summary>Coarse and readable. Nobody needs seconds on an outage measured in hours.</summary>
    private static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)span.TotalSeconds)}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m";

        return $"{(int)span.TotalDays}d {span.Hours}h";
    }
}
