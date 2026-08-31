using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>One depot's AES-256 decryption key, as recorded in Steam's own config.vdf.</summary>
internal readonly record struct DepotKey(long DepotId, string Key);

/// <summary>A parsed KeyValues node: either a leaf string or a nested section.</summary>
internal abstract record VdfNode
{
    private VdfNode() { }

    internal sealed record Leaf(string Value) : VdfNode;

    internal sealed record Section(Dictionary<string, VdfNode> Children) : VdfNode;
}

/// <summary>
/// Reads Valve KeyValues (config.vdf) and pulls out the per-depot decryption keys Steam stores there.
///
/// <para>
/// This is the PARSER half of what upstream keeps in <c>DonateKeysService</c>. The other half — uploading
/// those keys to a community pool over cleartext HTTP, on by default — is deliberately absent and must not
/// come back; see the "Removed: outbound data collection" note in <see cref="AppConfig"/>. Keys read here
/// never leave the machine: the only consumer is <see cref="DepotDownloaderService.ResolveKeys"/>, which
/// hands them to a local child process so it can decrypt depot chunks.
/// </para>
/// </summary>
internal static partial class SteamConfigVdf
{
    /// <summary>Guards against a hand-edited or corrupt file recursing the walk off the stack. Steam's own
    /// config.vdf nests about six deep; anything past this is not a file we need to understand.</summary>
    private const int MaxDepth = 64;

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex DepotIdRegex();

    /// <summary>
    /// An AES-256 depot key, hex-encoded.
    ///
    /// <para>
    /// Upstream's pattern is <c>^[a-zA-Z0-9]{64}$</c>, which accepts 64 characters that are not hex at all
    /// (<c>g</c>–<c>z</c>). Such a value passes validation, is written into the keys file, and then throws
    /// <see cref="FormatException"/> the moment anything calls <see cref="Convert.FromHexString"/> on it.
    /// Checking for hex here is what makes "validated pair" mean the same thing as "usable key".
    /// </para>
    /// </summary>
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex KeyRegex();

    /// <summary>
    /// Every <c>(depot, key)</c> pair in a config.vdf. Only validated pairs are returned; an unreadable or
    /// empty document yields an empty list rather than throwing.
    /// </summary>
    public static IReadOnlyList<DepotKey> ExtractKeys(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var result = new List<DepotKey>();
        Walk(ParseVdf(content), 0);
        return result;

        void Walk(VdfNode.Section node, int depth)
        {
            if (depth >= MaxDepth) return;

            foreach (var (name, child) in node.Children)
            {
                if (child is not VdfNode.Section section) continue;

                if (section.Children.TryGetValue("DecryptionKey", out var dk)
                    && dk is VdfNode.Leaf { Value: var key })
                {
                    // The section NAME is the depot id; the key hangs off it.
                    if (IsValidPair(name, key)) result.Add(new DepotKey(long.Parse(name), key));
                }
                else
                {
                    Walk(section, depth + 1); // DecryptionKey nests deep under depots/<id>
                }
            }
        }
    }

    /// <summary>Whether a section name and its DecryptionKey are both well-formed enough to use.</summary>
    public static bool IsValidPair(string? depotId, string? key) =>
        depotId is not null && key is not null
        && DepotIdRegex().IsMatch(depotId) && KeyRegex().IsMatch(key);

    /// <summary>
    /// Minimal recursive-descent KeyValues reader: <c>"quoted"</c> tokens, <c>{ }</c> nesting and
    /// <c>//</c> line comments.
    /// </summary>
    /// <remarks>
    /// Unlike the upstream parser this honours backslash escapes inside a quoted token. Upstream scans for
    /// the next raw <c>"</c>, so a value containing <c>\"</c> terminates the token early and every
    /// subsequent key/value pair in the file is read one position out of step — which silently loses the
    /// depot keys that follow it. Steam writes escapes in this file (Windows paths use <c>\\</c>), so this
    /// is a real input, not a hypothetical one.
    /// </remarks>
    public static VdfNode.Section ParseVdf(string content)
    {
        var root = new VdfNode.Section([]);
        var stack = new Stack<VdfNode.Section>();
        stack.Push(root);
        string? pendingKey = null;

        int pos = 0, len = content.Length;
        while (pos < len)
        {
            char c = content[pos];

            if (c == '/' && pos + 1 < len && content[pos + 1] == '/')
            {
                int nl = content.IndexOf('\n', pos);
                pos = nl < 0 ? len : nl + 1;
            }
            else if (c == '"')
            {
                if (ReadQuoted(content, ref pos) is not { } token) break; // unterminated
                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek().Children[pendingKey] = new VdfNode.Leaf(token);
                    pendingKey = null;
                }
            }
            else if (c == '{')
            {
                if (pendingKey is not null)
                {
                    var child = new VdfNode.Section([]);
                    stack.Peek().Children[pendingKey] = child;
                    // Past the cap the section is parsed but not pushed, so its contents land in the
                    // parent instead of growing the stack without bound.
                    if (stack.Count < MaxDepth) stack.Push(child);
                    pendingKey = null;
                }
                pos++;
            }
            else if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pos++;
            }
            else
            {
                pos++;
            }
        }

        return root;
    }

    /// <summary>
    /// Read one quoted token, starting at its opening quote. Leaves <paramref name="pos"/> just past the
    /// closing quote, or returns null when the token never closes.
    /// </summary>
    private static string? ReadQuoted(string content, ref int pos)
    {
        var sb = new StringBuilder();
        int i = pos + 1;

        while (i < content.Length)
        {
            char c = content[i];
            if (c == '\\' && i + 1 < content.Length)
            {
                // Valve escapes \\ \" \n \t; anything else keeps the character as written.
                char next = content[i + 1];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                i += 2;
                continue;
            }
            if (c == '"')
            {
                pos = i + 1;
                return sb.ToString();
            }
            sb.Append(c);
            i++;
        }

        pos = content.Length;
        return null;
    }
}
