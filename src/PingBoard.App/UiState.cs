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

    /// <summary>"System", "Light" or "Dark". System follows the Windows setting live.</summary>
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
            if (LastConfigPath is { Length: > 0 } path) section.Set(nameof(LastConfigPath), path);

            ini.SaveAtomic(AppPaths.UiStateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing window placement is not worth interrupting the user over.
        }
    }
}
