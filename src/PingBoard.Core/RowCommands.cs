using System.Globalization;
using System.Text.RegularExpressions;

namespace PingBoard.Core;

/// <summary>
/// The decisions behind the per-row right-click menu, kept here rather than in the view so they
/// can be tested without a window.
/// </summary>
public static partial class RowCommands
{
    /// <summary>
    /// The rows a right-click command acts on.
    /// <para>
    /// The Explorer rule, because that is what people's hands already know: right-clicking a row
    /// that is part of the selection acts on the whole selection; right-clicking any other row
    /// acts on that row alone and ignores the selection. Acting on the selection regardless would
    /// let a right-click on one host remove three others that happened to be highlighted.
    /// </para>
    /// </summary>
    public static IReadOnlyList<T> TargetsFor<T>(T clicked, IReadOnlyList<T> selection) where T : class
    {
        foreach (var item in selection)
            if (ReferenceEquals(item, clicked)) return selection;

        return [clicked];
    }

    /// <summary>
    /// A name for a duplicate of <paramref name="name"/> that <paramref name="taken"/> does not
    /// already claim: <c>gw (copy)</c>, then <c>gw (copy 2)</c>, <c>gw (copy 3)</c>…
    /// <para>
    /// Duplicating a copy numbers from its original rather than nesting — <c>gw (copy)</c> gives
    /// <c>gw (copy 2)</c>, not <c>gw (copy) (copy)</c> — since target names are what the board,
    /// the logs and the alerts show, and they should stay readable.
    /// </para>
    /// </summary>
    public static string CopyName(string name, Func<string, bool> taken)
    {
        var stem = CopySuffix().Replace(name.Trim(), "");
        if (stem.Length == 0) stem = name.Trim();

        var candidate = stem + " (copy)";
        for (var n = 2; taken(candidate); n++)
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} (copy {n})");

        return candidate;
    }

    [GeneratedRegex(@"\s*\(copy(?: \d+)?\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();
}
