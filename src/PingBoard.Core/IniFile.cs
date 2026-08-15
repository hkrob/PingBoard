using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// A minimal INI document: ordered sections, each an ordered set of key/value pairs.
/// <para>
/// Hand-rolled rather than using <c>GetPrivateProfileString</c>. The kernel32 API is nominally the
/// "built-in" route, but it is ANSI-quirky, demands an absolute path, and round-trips Unicode
/// badly. Eighty lines of <see cref="StreamReader"/> keeps the file genuinely hand-editable, which
/// was the point of choosing INI in the first place.
/// </para>
/// </summary>
public sealed class IniFile
{
    private readonly List<IniSection> _sections = [];

    public IReadOnlyList<IniSection> Sections => _sections;

    public IniSection GetOrAdd(string name)
    {
        var existing = _sections.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var section = new IniSection(name);
        _sections.Add(section);
        return section;
    }

    public IniSection? Find(string name) =>
        _sections.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sections whose name starts with <paramref name="prefix"/>, e.g. <c>Target:</c>.</summary>
    public IEnumerable<IniSection> WithPrefix(string prefix) =>
        _sections.Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        IniSection? current = null;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] is ';' or '#') continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close <= 1) continue;
                current = ini.GetOrAdd(line[1..close].Trim());
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Strip trailing inline comments, but only when preceded by whitespace, so a value
            // that legitimately contains ';' or '#' survives.
            var cut = FindInlineComment(value);
            if (cut >= 0) value = value[..cut].TrimEnd();

            if (key.Length == 0) continue;
            (current ??= ini.GetOrAdd("")).Set(key, value);
        }

        return ini;
    }

    private static int FindInlineComment(string value)
    {
        for (var i = 1; i < value.Length; i++)
            if (value[i] is ';' or '#' && char.IsWhiteSpace(value[i - 1]))
                return i - 1;
        return -1;
    }

    public static IniFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var section in _sections)
        {
            if (section.Comment is { Length: > 0 } comment)
                foreach (var line in comment.Split('\n'))
                    sb.Append("; ").AppendLine(line.TrimEnd());

            sb.Append('[').Append(section.Name).AppendLine("]");
            foreach (var (key, value) in section.Entries)
                sb.Append(key).Append('=').AppendLine(value);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes atomically: full content to a sibling temp file, flushed to disk, then renamed over
    /// the destination in a single atomic operation, with the previous version kept as
    /// <c>.bak</c>.
    /// <para>
    /// A naive truncate-then-write leaves a partial or zero-byte config if the process dies
    /// partway, which for a tool that autosaves on a timer eventually means losing the target list.
    /// </para>
    /// <para>
    /// <b>Why not <see cref="File.Replace(string, string, string)"/>:</b> it moves the destination
    /// to the backup and <em>then</em> renames the temp into place, so a kill between those two
    /// steps leaves no config file at all. A torture test that hard-killed a writer mid-save hit
    /// exactly that window. <see cref="File.Move(string, string, bool)"/> maps to <c>MoveFileEx</c>
    /// with <c>MOVEFILE_REPLACE_EXISTING</c>, which is a true atomic rename on the same volume:
    /// the destination is always either the old file or the new one, never absent.
    /// </para>
    /// </summary>
    public void SaveAtomic(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = full + ".tmp";
        var backup = full + ".bak";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(Serialize());
            writer.Flush();
            // Force to disk before the rename, so a power cut cannot leave the rename committed
            // while the contents are still in the write cache.
            stream.Flush(flushToDisk: true);
        }

        // Snapshot the previous version by copying rather than moving. A crash during this step
        // leaves the live file untouched — only the backup would be stale.
        if (File.Exists(full))
        {
            try { File.Copy(full, backup, overwrite: true); }
            catch (IOException) { /* a stale backup is not worth failing the save over */ }
        }

        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// Loads <paramref name="path"/>, falling back to the <c>.bak</c> written by
    /// <see cref="SaveAtomic"/> if the main file is missing or unparseable, and clearing any
    /// orphaned <c>.tmp</c> left by an interrupted save.
    /// </summary>
    public static IniFile LoadResilient(string path)
    {
        var full = Path.GetFullPath(path);
        var backup = full + ".bak";
        var temp = full + ".tmp";

        // An orphaned temp file means a previous save was interrupted before the rename. Its
        // contents were never committed, so it is noise.
        if (File.Exists(temp))
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }

        if (File.Exists(full))
        {
            try { return Load(full); }
            catch (IOException) { /* fall through to the backup */ }
        }

        if (File.Exists(backup))
        {
            var recovered = Load(backup);
            // Restore it, so the next run does not depend on the backup again.
            try { File.Copy(backup, full, overwrite: true); } catch (IOException) { /* best effort */ }
            return recovered;
        }

        return new IniFile();
    }
}

/// <summary>One <c>[section]</c> and its ordered key/value entries.</summary>
public sealed class IniSection(string name)
{
    private readonly List<KeyValuePair<string, string>> _entries = [];

    public string Name { get; } = name;

    /// <summary>Written above the section header as <c>;</c> comments. Newline-separated.</summary>
    public string? Comment { get; set; }

    public IReadOnlyList<KeyValuePair<string, string>> Entries => _entries;

    public void Set(string key, string value)
    {
        var index = _entries.FindIndex(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var entry = new KeyValuePair<string, string>(key, value);
        if (index >= 0) _entries[index] = entry;
        else _entries.Add(entry);
    }

    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    /// <summary>
    /// Written with "0.##" rather than the round-trip form, so a threshold of five is written as
    /// <c>5</c> and not <c>5</c> in some cultures and <c>5,0</c> in others. Invariant throughout:
    /// this file is read by the program, not by a locale.
    /// </summary>
    public void Set(string key, double value) =>
        Set(key, value.ToString("0.##", CultureInfo.InvariantCulture));

    public void SetOptional(string key, int? value)
    {
        if (value is { } v) Set(key, v);
    }

    public void SetOptional(string key, double? value)
    {
        if (value is { } v) Set(key, v);
    }

    public string? Get(string key)
    {
        var index = _entries.FindIndex(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? _entries[index].Value : null;
    }

    public string GetString(string key, string fallback) => Get(key) ?? fallback;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public long GetLong(string key, long fallback) =>
        long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public int? GetIntOrNull(string key) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public double? GetDoubleOrNull(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool GetBool(string key, bool fallback) => Get(key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => fallback,
    };

    public DateTimeOffset? GetDate(string key) =>
        DateTimeOffset.TryParse(Get(key), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var v) ? v : null;
}
