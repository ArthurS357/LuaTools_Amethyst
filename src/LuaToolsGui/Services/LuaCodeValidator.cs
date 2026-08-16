using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// Screens ordinary Lua <b>application</b> code — a plugin's own backend — as opposed to a Steam manifest.
///
/// <para>
/// This exists because applying <see cref="LuaManifestValidator"/> to a plugin was a category error, and a
/// shipped one: installing the BetterSteamTools plugin failed with
/// <c>backend/main.lua: the lua contains 'require', which a Steam manifest never needs</c>. That message is
/// correct about manifests and irrelevant here. A Steam manifest is a tiny DSL of <c>addappid()</c> /
/// <c>setManifestid()</c> calls, so <c>require</c>, <c>pcall</c>, <c>setmetatable</c>, <c>_G</c> and
/// friends genuinely have no place in one. In a plugin backend they are just Lua — <c>require("utils")</c>
/// is how the language loads a module.
/// </para>
///
/// <para>
/// So the denylist here is a different, much shorter list: only constructs that execute code or spawn a
/// process. File I/O (<c>io.open</c>), environment reads (<c>os.getenv</c>), metatables, <c>pcall</c> and
/// module loading are all ordinary and are <b>not</b> flagged.
/// </para>
///
/// <para>
/// <b>Honest scope.</b> This is defence in depth, not a trust boundary. The same <c>plugin.zip</c> release
/// also ships <c>winmm.dll</c>, which steam.exe loads — if that release is hostile, the DLL wins long
/// before any Lua does. What actually protects the user is repository pinning plus the fail-closed digest
/// check upstream of this. Do not grow this list on the theory that it is holding a line; every entry
/// added is a plugin that stops installing.
/// </para>
/// </summary>
internal static partial class LuaCodeValidator
{
    /// <summary>
    /// Constructs that run a program or load native code. Deliberately short.
    ///
    /// <para>
    /// Absent on purpose, and each for a reason: <c>io.*</c> (a backend reading its own config is normal),
    /// <c>os.getenv</c> (reading the environment is normal), <c>require</c>/<c>dofile</c>/<c>loadfile</c>
    /// (module loading is the point of the language), <c>setmetatable</c>/<c>rawget</c>/<c>_G</c> (ordinary
    /// table work), <c>pcall</c>/<c>xpcall</c> (error handling — flagging it would reject every
    /// well-written script), and <c>collectgarbage</c>.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenCalls =
    [
        "os.execute",       // hands a string to the shell
        "io.popen",         // spawns a process and reads its output
        "package.loadlib",  // loads an arbitrary native library into the process
        "loadstring",       // removed in Lua 5.2; in modern code it is an obfuscation vector, not a feature
    ];

    /// <summary>
    /// The GLOBAL <c>load(...)</c> called with something other than a plain string literal — i.e. code
    /// assembled at runtime. <c>load("return 1")</c> is inspectable and allowed; <c>load(decode(blob))</c>
    /// is not.
    ///
    /// <para>
    /// The two lookbehinds are not defensive padding; without them this rule refuses ordinary code, which
    /// a test caught before it shipped. <c>load</c> is an everyday method name: <c>M:load()</c>,
    /// <c>self.load(path)</c> and <c>function M.load(path)</c> are a method call, a field call and a
    /// DEFINITION respectively, and a bare <c>\bload\s*\(</c> flags all three. Only an unqualified
    /// <c>load(</c> that is not being declared refers to Lua's global.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(?<![\w.:])(?<!\bfunction\s{1,40})load\s*\(\s*(?![""'\[])",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex DynamicLoadRegex();

    /// <summary>A module/chunk loaded from a URL rather than from disk. Same qualification guard: a
    /// plugin's own <c>client.require(url)</c> helper is not Lua's <c>require</c>.</summary>
    [GeneratedRegex(@"(?<![\w.:])(?:require|dofile|loadfile|load)\s*\(\s*[""'][^""']*https?://",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex RemoteLoadRegex();

    /// <summary>Lua <c>\xNN</c> and <c>\NNN</c> character escapes, counted to gauge obfuscation.</summary>
    [GeneratedRegex(@"\\(x[0-9A-Fa-f]{2}|\d{1,3})", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex EscapeRegex();

    /// <summary>
    /// Escape-density thresholds for "this file is a blob pretending to be source".
    ///
    /// <para>
    /// Both must be exceeded, and they are set far above anything real code reaches. An escape-heavy
    /// string table, a binary blob embedded as a literal, or a file full of <c>\n</c> in messages stays
    /// well under: ordinary source is a few escapes per hundred lines, not hundreds of them making up a
    /// third of the file. The count alone is not enough (a large legitimate file could accumulate them);
    /// the ratio alone is not enough (a five-character file is trivially 100%).
    /// </para>
    ///
    /// <para>
    /// Shuffled or meaningless variable names are deliberately NOT screened for. Minified and generated
    /// Lua is legitimate and looks identical to "obfuscated" by every heuristic worth writing, so the
    /// check would be a false-positive generator with no attacker cost.
    /// </para>
    /// </summary>
    private const int MinEscapesToSuspect = 200;
    private const double MinEscapeRatio = 0.30;

    /// <summary>Screen Lua application source. Never throws — a screening failure must not become a crash.</summary>
    public static LuaScreenResult Screen(string lua)
    {
        if (string.IsNullOrWhiteSpace(lua)) return LuaScreenResult.Accept(0);

        try
        {
            foreach (string rawLine in lua.Split('\n'))
            {
                // Same comment handling as the manifest screen: a denylisted name after "--" is inert, and
                // flagging it would reject a file over its own documentation.
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                foreach (string forbidden in ForbiddenCalls)
                {
                    if (ContainsIdentifier(line, forbidden))
                        return LuaScreenResult.Reject($"calls '{forbidden}', which runs code outside the plugin");
                }

                if (RemoteLoadRegex().IsMatch(line))
                    return LuaScreenResult.Reject("loads a module or chunk from a URL");

                if (DynamicLoadRegex().IsMatch(line))
                    return LuaScreenResult.Reject("builds code at runtime and loads it");
            }

            if (IsEscapeBlob(lua))
                return LuaScreenResult.Reject("is mostly character escapes rather than source");
        }
        catch (RegexMatchTimeoutException)
        {
            return LuaScreenResult.Reject("could not be screened (pathological content)");
        }

        // Unlike a manifest, application code has no fixed vocabulary, so there is nothing meaningful to
        // count as "unrecognised" — reporting every line would be noise, not a signal.
        return LuaScreenResult.Accept(0);
    }

    private static bool IsEscapeBlob(string lua)
    {
        int escapes = EscapeRegex().Matches(lua).Count;
        if (escapes < MinEscapesToSuspect) return false;

        int significant = 0;
        foreach (char c in lua)
            if (!char.IsWhiteSpace(c)) significant++;

        // Each match is at least 2 characters (\d) and at most 4 (\xNN); charge the minimum so the ratio
        // cannot be inflated by the estimate itself.
        return significant > 0 && (double)(escapes * 2) / significant >= MinEscapeRatio;
    }

    private static string StripComment(string line)
    {
        int marker = line.IndexOf("--", StringComparison.Ordinal);
        return marker >= 0 ? line[..marker] : line;
    }

    /// <summary>Case-insensitive whole-token match, so <c>io.popen</c> does not match inside
    /// <c>studio.popenings</c>.</summary>
    private static bool ContainsIdentifier(string line, string identifier)
    {
        int from = 0;
        while (true)
        {
            int at = line.IndexOf(identifier, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            char before = at == 0 ? ' ' : line[at - 1];
            int afterIndex = at + identifier.Length;
            char after = afterIndex >= line.Length ? ' ' : line[afterIndex];

            if (!IsIdentifierChar(before) && !IsIdentifierChar(after)) return true;
            from = at + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
