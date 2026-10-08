using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>
/// Decides who may call the loopback HTTP API on port 6767.
///
/// <para>
/// The endpoints behind it have real side effects — install or delete a lua, launch a URL through
/// ShellExecute, restart Steam. It once answered every request with <c>Access-Control-Allow-Origin: *</c>,
/// then (after the first fix) with anything that carried no <c>Origin</c> header or an allow-listed Steam
/// one. Both leave the door open to any web page: a plain <c>GET</c> by navigation or <c>&lt;img&gt;</c>
/// carries no <c>Origin</c> (Fetch spec), so "no Origin means not a browser" is false. The only
/// legitimate caller is the app's own <c>CefInjectorService</c>, which authenticates with a per-launch
/// token, so the token is now required for everything and Origin plays no part.
/// </para>
///
/// <para>
/// Split out of <c>HttpServerService</c> because the decision is pure and the transport is not.
/// <c>HttpListenerRequest</c> is sealed with no public constructor, so a policy expressed in terms of it
/// cannot be unit-tested at all; expressed in terms of two header strings, every branch can.
/// </para>
/// </summary>
internal static class LocalApiAccessPolicy
{
    /// <summary>Host names accepted in the <c>Host</c> header, port stripped.</summary>
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>
    /// True only for a literal-loopback <c>Host</c> AND the correct session token.
    /// </summary>
    /// <param name="hostHeader">Raw <c>Host</c> header, with or without a port.</param>
    /// <param name="tokenHeader">Raw session-token header, if the caller sent one.</param>
    /// <param name="expectedToken">This launch's session token.</param>
    public static bool Evaluate(string? hostHeader, string? tokenHeader, string expectedToken) =>
        // Host first: a rebound DNS name still reaches this socket carrying its own Host header, and a
        // valid token from such a host must not be honoured. Token compared in fixed time, since it is a
        // secret a local attacker could otherwise time out.
        IsLoopbackHost(hostHeader) && TokenMatches(tokenHeader, expectedToken);

    /// <summary>True when the Host header names literal loopback. The port is stripped first; for an IPv6
    /// literal (<c>[::1]:6767</c>) the closing bracket keeps the address's own colons out of the way.</summary>
    private static bool IsLoopbackHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;

        ReadOnlySpan<char> host = hostHeader.AsSpan().Trim();
        int colon = host.LastIndexOf(':');
        int bracket = host.LastIndexOf(']');
        if (colon > bracket) host = host[..colon]; // strip ":port", never an IPv6 literal's own colons

        foreach (string loopback in LoopbackHosts)
            if (host.Equals(loopback, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool TokenMatches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}
