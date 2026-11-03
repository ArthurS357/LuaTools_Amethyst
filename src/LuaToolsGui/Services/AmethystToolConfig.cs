using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// The pure half of the "Denuvo Mode (Aliases)" switch: reads and rewrites the one key it owns,
/// <c>[compatibility] denuvo_aliases</c>, in the text of <c>amethysttool.toml</c>.
///
/// <para>
/// <b>Line-level surgery, not a TOML round trip.</b> The file is the user's (and AmethystTool's) config,
/// with comments, ordering and keys this app knows nothing about. Parsing it into a model and serialising
/// it back would drop every comment and reorder the rest, so this touches exactly one line — or adds one
/// line, or one two-line section — and leaves every other byte, line ending included, as it was.
/// </para>
///
/// <para>
/// Lines inside a multi-line string (<c>"""</c> / <c>'''</c>) are never read as headers or keys, so a
/// <c>[compatibility]</c> that only appears inside a string value cannot be mistaken for the table.
/// </para>
/// </summary>
internal static partial class AmethystToolConfig
{
    public const string FileName = "amethysttool.toml";
    public const string Section = "compatibility";
    public const string Key = "denuvo_aliases";

    // [compatibility] — a plain table header, NOT [[compatibility]] (array of tables). Trailing comment allowed.
    [GeneratedRegex(@"^\s*\[\s*compatibility\s*\]\s*(#.*)?$")]
    private static partial Regex SectionHeader();

    // Any table / array-of-tables header ends the current section.
    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex AnyHeader();

    // denuvo_aliases = <value>  [# comment] — the value group stops before a comment.
    [GeneratedRegex(@"^(?<lead>\s*denuvo_aliases\s*=\s*)(?<value>[^#]*?)(?<tail>\s*(#.*)?)$")]
    private static partial Regex KeyLine();

    // compatibility.denuvo_aliases = ... written as a dotted key at the root, before any header.
    [GeneratedRegex(@"^(?<lead>\s*compatibility\s*\.\s*denuvo_aliases\s*=\s*)(?<value>[^#]*?)(?<tail>\s*(#.*)?)$")]
    private static partial Regex DottedKeyLine();

    // compatibility = { ... } or any other root-level compatibility.* key: the table is defined inline or
    // implicitly, and adding a [compatibility] header next to it would make the file invalid TOML.
    [GeneratedRegex(@"^\s*compatibility\s*[.=]")]
    private static partial Regex RootCompatibility();

    /// <summary>
    /// Whether the switch is on in <paramref name="toml"/>. Absent key, absent section or any value other
    /// than <c>true</c> reads as off — off is AmethystTool's own default.
    /// </summary>
    public static bool ReadDenuvoAliases(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);

        bool inSection = false, atRoot = true;
        foreach (var line in ScanLines(SplitLines(toml)))
        {
            if (line.InString) continue;

            if (AnyHeader().IsMatch(line.Text))
            {
                atRoot = false;
                inSection = SectionHeader().IsMatch(line.Text);
                continue;
            }

            Match m = inSection ? KeyLine().Match(line.Text)
                : atRoot ? DottedKeyLine().Match(line.Text)
                : Match.Empty;
            if (m.Success) return m.Groups["value"].Value.Trim() == "true";
        }
        return false;
    }

    /// <summary>
    /// <paramref name="toml"/> with <c>[compatibility] denuvo_aliases</c> set to <paramref name="enabled"/>.
    /// Every other line is returned unchanged.
    /// </summary>
    /// <exception cref="FormatException">
    /// The file defines <c>compatibility</c> as an inline table or through root-level dotted keys other than
    /// the one this edits. Adding a <c>[compatibility]</c> header next to that would produce a file
    /// AmethystTool refuses to load, so the edit is refused instead.
    /// </exception>
    public static string SetDenuvoAliases(string toml, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(toml);

        string value = enabled ? "true" : "false";
        string newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = SplitLines(toml);
        var scanned = ScanLines(lines).ToList();

        int header = -1, sectionEnd = lines.Count;
        bool atRoot = true, edited = false, rootDefinesTable = false;

        for (int i = 0; i < scanned.Count; i++)
        {
            var line = scanned[i];
            if (line.InString) continue;

            if (AnyHeader().IsMatch(line.Text))
            {
                atRoot = false;
                if (header >= 0 && sectionEnd == lines.Count) sectionEnd = i;
                if (header < 0 && SectionHeader().IsMatch(line.Text)) header = i;
                continue;
            }

            if (atRoot)
            {
                if (DottedKeyLine().Match(line.Text) is { Success: true } dotted)
                {
                    lines[i] = Replace(dotted, value);
                    edited = true;
                }
                else if (RootCompatibility().IsMatch(line.Text))
                {
                    rootDefinesTable = true;
                }
                continue;
            }

            // Inside [compatibility] only. Every matching line is set, so a file that (invalidly) repeats the
            // key cannot end up saying both things.
            if (header >= 0 && sectionEnd == lines.Count && KeyLine().Match(line.Text) is { Success: true } m)
            {
                lines[i] = Replace(m, value);
                edited = true;
            }
        }

        if (edited) return Join(lines, newline);

        if (rootDefinesTable)
            throw new FormatException(
                $"{FileName} defines '{Section}' outside a [{Section}] header; edit it by hand.");

        string keyLine = $"{Key} = {value}";

        if (header >= 0)
        {
            // After the section's last non-blank line, so the blank line that separates it from the next
            // header stays where it was.
            int insertAt = sectionEnd;
            while (insertAt - 1 > header && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            lines.Insert(insertAt, keyLine);
            return Join(lines, newline);
        }

        // No section anywhere: append one, separated by a blank line from whatever came before.
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0) lines.Add("");
        lines.Add($"[{Section}]");
        lines.Add(keyLine);
        lines.Add(""); // trailing newline
        return Join(lines, newline);
    }

    private static string Replace(Match m, string value) =>
        m.Groups["lead"].Value + value + m.Groups["tail"].Value;

    /// <summary>Lines without their terminators; <see cref="Join"/> puts back the file's own style.</summary>
    private static List<string> SplitLines(string text) =>
        [.. text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l)];

    private static string Join(List<string> lines, string newline) => string.Join(newline, lines);

    private readonly record struct ScannedLine(string Text, bool InString);

    /// <summary>
    /// Marks each line that starts inside a multi-line string. A line that OPENS one is still a real line
    /// (its key is real); only the lines after it, up to and including the closing one, are string content.
    /// </summary>
    private static IEnumerable<ScannedLine> ScanLines(List<string> lines)
    {
        string? open = null; // the delimiter of the multi-line string we are inside, if any
        foreach (string text in lines)
        {
            bool startsInString = open is not null;
            int pos = 0;
            while (true)
            {
                if (open is not null)
                {
                    int close = text.IndexOf(open, pos, StringComparison.Ordinal);
                    if (close < 0) break;
                    pos = close + 3;
                    open = null;
                    continue;
                }

                int basic = text.IndexOf("\"\"\"", pos, StringComparison.Ordinal);
                int literal = text.IndexOf("'''", pos, StringComparison.Ordinal);
                int hash = text.IndexOf('#', pos);
                int next = MinNonNegative(basic, literal);
                if (next < 0 || (hash >= 0 && hash < next)) break; // nothing opens, or it is in a comment

                open = next == basic ? "\"\"\"" : "'''";
                pos = next + 3;
            }
            yield return new ScannedLine(text, startsInString);
        }
    }

    private static int MinNonNegative(int a, int b) =>
        a < 0 ? b : b < 0 ? a : Math.Min(a, b);
}
