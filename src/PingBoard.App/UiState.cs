using PingBoard.Core;

namespace PingBoard.App;

/// <summary>
/// Window placement, column visibility and the last config opened.
/// <para>
/// Kept separate from the board config on purpose: this is per-machine UI trivia, and mixing it
/// into the file the user hand-edits and copies between machines would mean carrying one
/// machine's window coordinates onto another.
/// </para>
/// </summary>
public sealed class UiState
{
    private const string Section = "Ui";

    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public string HiddenColumns { get; set; } = string.Join(',', ColumnLayoutDefaults);
    public string? LastConfigPath { get; set; }

    /// <summary>
    /// Notification mute, as an ISO-8601 instant or the literal "indefinite"; empty when not muted.
    /// Persisted so an indefinite mute is not quietly lifted by a restart — see
    /// <see cref="NotificationMute"/> for why this one deadline uses the wall clock.
    /// </summary>
    public string MuteUntil { get; set; } = "";

    /// <summary>Board zoom, as a percentage. Per-machine, like everything else in this file.</summary>
    public int ZoomPercent { get; set; } = 100;

    /// <summary>Size columns to their content rather than to the fixed natural widths.</summary>
    public bool AutoFitColumns { get; set; } = true;

    /// <summary>Column display order, as a comma-separated list of ids. Blank means the default.</summary>
    public string ColumnOrder { get; set; } = "";

    /// <summary>
    /// Ask GitHub for a newer release shortly after launch. Opt-out, and visible in the menu: a
    /// monitoring tool that quietly phones home on every start is a reasonable thing to object to.
    /// </summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// When the last startup check ran, ISO-8601. Used to check roughly daily rather than on every
    /// launch — this app is normally started once and left running for weeks, but a machine that
    /// reboots often should not generate a request every time.
    /// </summary>
    public string LastUpdateCheck { get; set; } = "";

    /// <summary>
    /// "System", "Light", "Dark" or "Matrix". System follows the Windows setting live; Matrix is
    /// the green-phosphor terminal palette layered over Dark.
    /// </summary>
    public string Theme { get; set; } = "System";

    private static IEnumerable<string> ColumnLayoutDefaults => ViewModels.ColumnLayout.DefaultHidden;

    public static UiState Load()
    {
        var state = new UiState();

        try
        {
            if (!File.Exists(AppPaths.UiStateFile)) return state;

            var section = IniFile.Load(AppPaths.UiStateFile).Find(Section);
            if (section is null) return state;

            state.WindowX = section.GetInt(nameof(WindowX), 0);
            state.WindowY = section.GetInt(nameof(WindowY), 0);
            state.WindowWidth = section.GetInt(nameof(WindowWidth), 0);
            state.WindowHeight = section.GetInt(nameof(WindowHeight), 0);
            state.HiddenColumns = section.GetString(nameof(HiddenColumns), state.HiddenColumns);
            state.Theme = section.GetString(nameof(Theme), state.Theme);
            state.MuteUntil = section.GetString(nameof(MuteUntil), "");
            state.ZoomPercent = Math.Clamp(section.GetInt(nameof(ZoomPercent), 100), 70, 250);
            state.AutoFitColumns = section.GetBool(nameof(AutoFitColumns), true);
            state.ColumnOrder = section.GetString(nameof(ColumnOrder), "");
            state.CheckUpdatesOnStartup = section.GetBool(nameof(CheckUpdatesOnStartup), true);
            state.LastUpdateCheck = section.GetString(nameof(LastUpdateCheck), "");

            var last = section.GetString(nameof(LastConfigPath), "");
            state.LastConfigPath = last.Length > 0 ? last : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable UI state file just means falling back to defaults.
        }

        return state;
    }

    public void Save()
    {
        try
        {
            var ini = new IniFile();
            var section = ini.GetOrAdd(Section);
            section.Comment = "PingBoard window state. Generated — safe to delete.";

            section.Set(nameof(WindowX), WindowX);
            section.Set(nameof(WindowY), WindowY);
            section.Set(nameof(WindowWidth), WindowWidth);
            section.Set(nameof(WindowHeight), WindowHeight);
            section.Set(nameof(HiddenColumns), HiddenColumns);
            section.Set(nameof(Theme), Theme);
            if (MuteUntil.Length > 0) section.Set(nameof(MuteUntil), MuteUntil);
            if (ZoomPercent != 100) section.Set(nameof(ZoomPercent), ZoomPercent);
            section.Set(nameof(AutoFitColumns), AutoFitColumns);
            if (ColumnOrder.Length > 0) section.Set(nameof(ColumnOrder), ColumnOrder);
            section.Set(nameof(CheckUpdatesOnStartup), CheckUpdatesOnStartup);
            if (LastUpdateCheck.Length > 0) section.Set(nameof(LastUpdateCheck), LastUpdateCheck);
            if (LastConfigPath is { Length: > 0 } path) section.Set(nameof(LastConfigPath), path);

            ini.SaveAtomic(AppPaths.UiStateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing window placement is not worth interrupting the user over.
        }
    }
}
