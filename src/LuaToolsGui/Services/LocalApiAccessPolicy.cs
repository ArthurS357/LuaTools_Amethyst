using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>Outcome of an access check: whether to serve the request, and which origin (if any) the
/// <c>Access-Control-Allow-Origin</c> header should echo.</summary>
/// <param name="Allowed">False means answer 403 without reaching a handler.</param>
/// <param name="AllowOrigin">The single origin to echo, or null to emit no CORS headers at all.</param>
internal readonly record struct AccessDecision(bool Allowed, string? AllowOrigin)
{
    public static AccessDecision Deny { get; } = new(false, null);

    /// <summary>Allowed, with no CORS headers — a non-browser caller doesn't need them, and a wildcard
    /// here is precisely what made this server reachable from any web page.</summary>
    public static AccessDecision AllowWithoutCors { get; } = new(true, null);

    public static AccessDecision AllowOriginEcho(string origin) => new(true, origin);
}

/// <summary>
/// Decides who may call the loopback HTTP API on port 6767.
///
/// <para>
/// The endpoints behind it have real side effects — install or delete a lua, launch a URL through
/// ShellExecute, restart Steam. It previously answered every request with
/// <c>Access-Control-Allow-Origin: *</c> and no other check, so any page the user had open in any browser
/// could drive all of it: the preflight succeeded, so even JSON POSTs went through.
/// </para>
///
/// <para>
/// Split out of <c>HttpServerService</c> because the decision is pure and the transport is not.
/// <c>HttpListenerRequest</c> is sealed with no public constructor, so a policy expressed in terms of it
/// cannot be unit-tested at all; expressed in terms of three header strings, every branch can.
/// </para>
/// </summary>
internal static class LocalApiAccessPolicy
{
    /// <summary>Browser origins allowed to call this server directly (Steam's own store surfaces).</summary>
    public static readonly IReadOnlyList<string> AllowedOrigins =
    [
        "https://store.steampowered.com",
        "https://steamcommunity.com",
        "https://steamloopback.host",
    ];

    /// <summary>Host names accepted in the <c>Host</c> header, port stripped.</summary>
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>
    /// Evaluate a request from its <c>Host</c>, session-token and <c>Origin</c> headers.
    /// </summary>
    /// <param name="hostHeader">Raw <c>Host</c> header, with or without a port.</param>
    /// <param name="tokenHeader">Raw session-token header, if the caller sent one.</param>
    /// <param name="originHeader">Raw <c>Origin</c> header, if the caller sent one.</param>
    /// <param name="expectedToken">This launch's session token.</param>
    public static AccessDecision Evaluate(
        string? hostHeader, string? tokenHeader, string? originHeader, string expectedToken)
    {
        // 1. DNS-rebinding guard. HttpListener binds 127.0.0.1, but a hostile DNS name that resolves to
        //    127.0.0.1 still reaches this socket — carrying its own Host header. Pin it to literal loopback
        //    so a rebound name can't be used to reach the API from an arbitrary page.
        if (!IsLoopbackHost(hostHeader)) return AccessDecision.Deny;

        // 2. The app's own in-process client (CefInjectorService, which is what actually issues these calls
        //    — an HTTPS store page cannot fetch http://localhost, which is why the CDP bridge exists)
        //    authenticates with a per-launch token and so never depends on the origin list below.
        //    Compared in fixed time: the token is a secret, and a length-or-prefix-dependent comparison
        //    leaks it to a local attacker who can time requests.
        if (TokenMatches(tokenHeader, expectedToken)) return AccessDecision.AllowWithoutCors;

        // 3a. No Origin header: not a cross-origin browser fetch, since a browser always sends one for
        //     those. A same-machine, non-browser caller — allowed, and needs no CORS headers.
        if (string.IsNullOrEmpty(originHeader)) return AccessDecision.AllowWithoutCors;

        // 3b. A browser request: serve it only for a known Steam surface, echoing that exact origin.
        foreach (string allowed in AllowedOrigins)
            if (string.Equals(originHeader, allowed, StringComparison.OrdinalIgnoreCase))
                return AccessDecision.AllowOriginEcho(allowed);

        return AccessDecision.Deny;
    }

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
