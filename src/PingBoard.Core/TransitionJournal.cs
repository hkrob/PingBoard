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
    /// <summary>
    /// Raised from 200 when the journal became durable. Two hundred was sized for "what happened
    /// while the window was in the tray", which is hours; once it survives restarts the same buffer
    /// is answering "what has this board done lately", which is weeks.
    /// </summary>
    public const int Capacity = 500;

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

    /// <summary>Everything retained, oldest first.</summary>
    public IReadOnlyList<StateTransition> Snapshot() => Since(DateTimeOffset.MinValue);

    /// <summary>
    /// Refills the journal from a previous run, discarding whatever it held. Only the newest
    /// <see cref="Capacity"/> entries are kept if the file holds more.
    /// </summary>
    public void Restore(IReadOnlyList<StateTransition> transitions)
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;

            var start = Math.Max(0, transitions.Count - Capacity);

            for (var i = start; i < transitions.Count; i++)
            {
                _items[_next] = transitions[i];
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }
    }

    /// <summary>
    /// Pairs the recorded transitions into outages, newest first.
    /// <para>
    /// A down and the recovery that follows it are one event to a reader and two rows here, which
    /// is the right storage shape and the wrong reading shape: nobody wants to scroll a list
    /// looking for the matching half. The pairing is done on demand rather than stored, because a
    /// stored pair cannot represent the outage that has not ended yet.
    /// </para>
    /// </summary>
    /// <param name="now">
    /// Used only to age still-open outages. Passed in rather than read from the clock so the
    /// result is a pure function of its inputs and can be asserted on.
    /// </param>
    public IReadOnlyList<Outage> Outages(DateTimeOffset now)
    {
        var events = Snapshot();
        var open = new Dictionary<(string, TransitionKind), Outage>();
        var closed = new List<Outage>();

        foreach (var e in events)
        {
            if (e.Kind == TransitionKind.Certificate) continue;
            var key = (e.TargetName, e.Kind);

            if (!e.Up)
            {
                // A second down without an intervening recovery should not lose the first. It
                // cannot happen from one target, but a restored file can be missing rows.
                if (open.TryGetValue(key, out var orphan)) closed.Add(orphan);
                open[key] = new Outage(e.TargetName, e.When, null, TimeSpan.Zero, e.Status, e.Kind);
                continue;
            }

            if (open.Remove(key, out var started))
            {
                closed.Add(started with { End = e.When, Duration = e.DownFor });
                continue;
            }

            // Recovered with no matching start — the down half aged out of the buffer, or was
            // written by a run whose file has since been trimmed. The duration is still known, so
            // the outage is reconstructed backwards from it rather than dropped.
            if (e.DownFor > TimeSpan.Zero)
            {
                closed.Add(new Outage(
                    e.TargetName, e.When - e.DownFor, e.When, e.DownFor, TargetStatus.Unknown, e.Kind));
            }
        }

        foreach (var still in open.Values)
            closed.Add(still with { Duration = now - still.Start });

        closed.Sort(static (a, b) => b.Start.CompareTo(a.Start));
        return closed;
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
        // Real outages only.
        //
        // Everything below reads "not up" as "went down", which was the whole vocabulary when this
        // was written. It no longer is: a certificate transition is always Up:false and never has a
        // matching recovery, so left unfiltered it would be counted as an outage and would leave
        // that host marked still-down forever — the banner announcing that a host with a
        // certificate expiring in two months "went down at 14:34 and is still down".
        var events = Since(since).Where(e => e.Kind == TransitionKind.Hard).ToList();
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
    internal static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)span.TotalSeconds)}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m";

        return $"{(int)span.TotalDays}d {span.Hours}h";
    }
}

/// <summary>
/// One period during which a target was down, or degraded — a down transition and the recovery
/// that closed it, read as the single event a person would call it.
/// </summary>
/// <param name="End">Null while the outage is still running.</param>
/// <param name="Cause">
/// The status that opened the outage. <see cref="TargetStatus.Unknown"/> when only the recovery
/// half survives in the journal.
/// </param>
public readonly record struct Outage(
    string TargetName,
    DateTimeOffset Start,
    DateTimeOffset? End,
    TimeSpan Duration,
    TargetStatus Cause,
    TransitionKind Kind)
{
    public bool Ongoing => End is null;

    /// <summary>Coarse, human duration — the same rendering the away banner uses.</summary>
    public string DurationText => TransitionJournal.Duration(Duration);
}
