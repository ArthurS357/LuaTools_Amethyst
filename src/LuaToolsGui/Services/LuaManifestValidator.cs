using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>Outcome of screening a manifest lua before it is installed.</summary>
/// <param name="Rejected">True when the file must not be installed.</param>
/// <param name="Reason">Why it was rejected (null when accepted).</param>
/// <param name="UnrecognizedLines">Lines that are neither blank, a comment, nor a known manifest call.
/// Not a rejection on its own — just something worth recording.</param>
internal readonly record struct LuaScreenResult(bool Rejected, string? Reason, int UnrecognizedLines)
{
    public static LuaScreenResult Accept(int unrecognized) => new(false, null, unrecognized);
    public static LuaScreenResult Reject(string reason) => new(true, reason, 0);
}

/// <summary>
/// Screens a manifest <c>.lua</c> before LuaTools writes it into Steam's <c>config\stplug-in</c>.
///
/// <para>
/// These files are not inert data. The unlocker (SteamTools / OpenSteamTool) interprets them <b>inside
/// steam.exe</b>, so their contents are code running in the Steam process. They arrive from the lua.tools
/// API, from Hubcap, from a zip, and from files the user drags onto the window — and nothing checked what
/// was in them.
/// </para>
///
/// <para>
/// The screen is deliberately asymmetric, because a false positive here breaks legitimate installs while a
/// false negative only fails to catch an unusual attack:
/// </para>
/// <list type="bullet">
///   <item>A small denylist of constructs with no business in a manifest — process execution, file and
///   network I/O, dynamic code loading — <b>rejects</b> the file.</item>
///   <item>Anything else that isn't a recognised manifest call is merely <b>counted and logged</b>. New
///   legitimate directives keep working without a code change.</item>
/// </list>
/// </summary>
internal static partial class LuaManifestValidator
{
    /// <summary>Calls that let a manifest escape being a manifest. Matched as whole identifiers so a hex
    /// blob or an appid can't trip them.</summary>
    private static readonly string[] ForbiddenCalls =
    [
        "os.execute", "os.remove", "os.rename", "os.exit", "os.tmpname", "os.getenv",
        "io.open", "io.popen", "io.write", "io.lines", "io.read", "io.output", "io.input",
        "loadstring", "loadfile", "dofile", "require", "rawset", "rawget", "setmetatable", "getmetatable",
        "package.loadlib", "package.cpath", "package.path",
        "debug.getinfo", "debug.sethook", "debug.setupvalue",
        "collectgarbage", "_G",
    ];

    /// <summary>The directives a real manifest lua is made of.</summary>
    [GeneratedRegex(
        @"^\s*(addappid|setManifestid|adddlc|addlicense|adddepot)\s*\(",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex KnownDirectiveRegex();

    /// <summary>Bare `load(` / `pcall(` style dynamic execution, as a whole word.</summary>
    [GeneratedRegex(@"\b(load|pcall|xpcall|dofile)\s*\(", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex DynamicLoadRegex();

    /// <summary>Screen lua source. Never throws — a screening failure must not become an install crash.</summary>
    public static LuaScreenResult Screen(string lua)
    {
        if (string.IsNullOrWhiteSpace(lua)) return LuaScreenResult.Accept(0);

        int unrecognized = 0;

        try
        {
            foreach (string rawLine in lua.Split('\n'))
            {
                // Strip line comments before matching. A denylisted name after "--" is inert, and flagging
                // it would reject files over their own documentation.
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                foreach (string forbidden in ForbiddenCalls)
                {
                    if (ContainsIdentifier(line, forbidden))
                        return LuaScreenResult.Reject(
                            $"contains '{forbidden}', which a Steam manifest never needs");
                }

                if (DynamicLoadRegex().IsMatch(line))
                    return LuaScreenResult.Reject("contains dynamic code loading");

                if (!KnownDirectiveRegex().IsMatch(line)) unrecognized++;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return LuaScreenResult.Reject("could not be screened (pathological content)");
        }

        return LuaScreenResult.Accept(unrecognized);
    }

    /// <summary>Everything before the first <c>--</c>. Long-form <c>--[[ ]]</c> comments start with <c>--</c>
    /// too, so their opening line is cut here as well; a denylisted call on a later line of such a block is
    /// still rejected, which errs toward safety.</summary>
    private static string StripComment(string line)
    {
        int marker = line.IndexOf("--", StringComparison.Ordinal);
        return marker >= 0 ? line[..marker] : line;
    }

    /// <summary>Case-insensitive match of <paramref name="identifier"/> as a whole token, so
    /// <c>"io.open"</c> does not match inside <c>"studio.opened"</c>.</summary>
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
