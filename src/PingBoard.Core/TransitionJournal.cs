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

    /// <summary>
    /// Cap on pinned open outages. Bounded like everything else here: in practice this holds one
    /// entry per target that is currently down, which is bounded by the board, but a long-running
    /// board whose targets are renamed could otherwise accumulate keys that will never be closed.
    /// </summary>
    private const int MaxOpen = 256;

    private readonly Lock _gate = new();
    private readonly StateTransition[] _items = new StateTransition[Capacity];
    private int _next;
    private int _count;

    /// <summary>
    /// The transition that opened each outage still in progress, kept outside the ring so that
    /// eviction cannot take it.
    /// <para>
    /// Without this a single noisy target erases everything else. A link flapping twice a minute
    /// fills five hundred entries in a few hours, and the ring dutifully discards the oldest —
    /// including the "went down" of a host that is <em>still down</em>. The outage log would then
    /// show nothing but the flapper, having silently dropped the one outage nobody had fixed yet,
    /// which is precisely when the log is worth reading. Closed outages still age out normally:
    /// they are history, and history is what a bounded buffer is allowed to forget.
    /// </para>
    /// </summary>
    private readonly Dictionary<(string Target, TransitionKind Kind), StateTransition> _open =
        new(new OpenKeyComparer());

    public void Add(in StateTransition transition)
    {
        lock (_gate)
        {
            _items[_next] = transition;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;

            TrackOpenLocked(transition);
        }
    }

    /// <summary>Maintains the pinned set: a down opens an outage, the matching up closes it.</summary>
    private void TrackOpenLocked(in StateTransition transition)
    {
        // A certificate warning is not an outage and never has a recovery, so pinning one would
        // pin it forever.
        if (transition.Kind == TransitionKind.Certificate) return;

        var key = (transition.TargetName, transition.Kind);

        if (transition.Up) { _open.Remove(key); return; }

        _open[key] = transition;

        // Evict the oldest if a pathological config has somehow exceeded the cap.
        if (_open.Count <= MaxOpen) return;

        var oldest = key;
        var oldestWhen = DateTimeOffset.MaxValue;

        foreach (var (k, v) in _open)
            if (v.When < oldestWhen) { oldestWhen = v.When; oldest = k; }

        _open.Remove(oldest);
    }

    /// <summary>Case-insensitive on the target name, matching every other lookup here.</summary>
    private sealed class OpenKeyComparer : IEqualityComparer<(string Target, TransitionKind Kind)>
    {
        public bool Equals((string Target, TransitionKind Kind) a, (string Target, TransitionKind Kind) b) =>
            a.Kind == b.Kind && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Target, TransitionKind Kind) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Target), k.Kind);
    }

    /// <summary>Transitions recorded at or after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<StateTransition> Since(DateTimeOffset since)
    {
        lock (_gate) return SinceLocked(since);
    }

    private List<StateTransition> SinceLocked(DateTimeOffset since)
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

    /// <summary>Everything retained in the ring, oldest first.</summary>
    public IReadOnlyList<StateTransition> Snapshot() => Since(DateTimeOffset.MinValue);

    /// <summary>
    /// The ring plus any pinned outage whose opening transition has already been evicted from it,
    /// oldest first. This is what should be written to disk.
    /// <para>
    /// Distinct from <see cref="Snapshot"/> because compaction rewrites the file from this, and
    /// writing the plain ring would discard on disk exactly the open outages the pinned set exists
    /// to protect in memory — reintroducing the bug one layer down.
    /// </para>
    /// </summary>
    public IReadOnlyList<StateTransition> SnapshotForPersist()
    {
        lock (_gate)
        {
            var ring = SinceLocked(DateTimeOffset.MinValue);
            if (_open.Count == 0) return ring;

            var present = new HashSet<(string, TransitionKind)>(new OpenKeyComparer());
            foreach (var t in ring)
                if (!t.Up) present.Add((t.TargetName, t.Kind));

            var merged = new List<StateTransition>(ring);

            foreach (var (key, open) in _open)
                if (!present.Contains(key)) merged.Add(open);

            merged.Sort(static (a, b) => a.When.CompareTo(b.When));
            return merged;
        }
    }

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

            _open.Clear();

            // Every restored transition is replayed through the open-outage tracker, not just the
            // ones that fit the ring: an outage that was open when the previous run ended must come
            // back pinned, or it would be dropped on the first eviction after startup.
            foreach (var t in transitions) TrackOpenLocked(t);

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

        // Same comparer as the pinned set, so a target whose name differs only in case cannot be
        // treated as two hosts by one of them and one by the other.
        var open = new Dictionary<(string, TransitionKind), Outage>(new OpenKeyComparer());
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

        // Outages whose opening transition has been evicted from the ring, but which are still
        // running. Without this a host that went down hours ago and has not come back vanishes
        // from the log entirely as soon as a noisier target fills the buffer.
        lock (_gate)
        {
            foreach (var (key, pinned) in _open)
            {
                if (open.ContainsKey((key.Target, key.Kind))) continue;

                closed.Add(new Outage(
                    pinned.TargetName, pinned.When, null, now - pinned.When, pinned.Status, pinned.Kind));
            }
        }

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
            _open.Clear();
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
