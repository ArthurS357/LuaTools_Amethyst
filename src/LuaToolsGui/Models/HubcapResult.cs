using System.Net;

namespace LuaToolsGui.Models;

/// <summary>
/// The outcome of a Hubcap call, as a closed set of cases rather than "the value, or null".
///
/// <para>
/// Every metadata call used to end in <c>catch { return null; }</c>, which collapsed four unrelated
/// situations — a rejected key, a spent daily quota, a server fault, and no network at all — into one
/// indistinguishable answer. That had two visible consequences. Validating a key while offline reported
/// the key as bad, so users deleted a perfectly good credential; and a transient failure while browsing
/// locked the premium source rows exactly as if the quota were exhausted.
/// </para>
///
/// <para>
/// The hierarchy is sealed by a private constructor, so a <c>switch</c> over the cases below is
/// exhaustive and stays exhaustive: no other assembly can add a case that silently falls through an
/// existing match.
/// </para>
/// </summary>
/// <typeparam name="T">The payload carried by <see cref="Ok"/>.</typeparam>
public abstract record HubcapResult<T>
{
    private HubcapResult() { }

    /// <summary>The call succeeded and <paramref name="Value"/> is the parsed response.</summary>
    public sealed record Ok(T Value) : HubcapResult<T>;

    /// <summary>401 — Hubcap rejected the key. It is wrong, revoked, or past
    /// <c>api_key_expires_at</c>. The only case that justifies telling the user their key is bad.</summary>
    public sealed record Unauthorized : HubcapResult<T>;

    /// <summary>404 — Hubcap has no manifest for this app. A normal answer, not a fault.</summary>
    public sealed record NotFound : HubcapResult<T>;

    /// <summary>429 — the key's daily allowance is spent. <paramref name="RetryAfter"/> comes from the
    /// response header when Hubcap sends one; null means it didn't say.</summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : HubcapResult<T>;

    /// <summary>Any other non-success status. Hubcap answered, so the key and the network are fine —
    /// the service itself is unhappy.</summary>
    public sealed record Failed(HttpStatusCode Status) : HubcapResult<T>;

    /// <summary>The request never produced a response: DNS failure, refused connection, TLS error, or the
    /// metadata deadline expiring. Distinct from every case above because retrying may well work.</summary>
    public sealed record Offline(Exception Cause) : HubcapResult<T>;

    /// <summary>
    /// The payload when the call succeeded, otherwise <c>default</c>.
    ///
    /// <para>
    /// For call sites where the distinction genuinely carries no weight — the cosmetic "12/25" usage
    /// badge, which is simply absent when unknown. Anywhere the answer changes what the user is told,
    /// match on the cases instead; that is the entire point of this type.
    /// </para>
    /// </summary>
    public T? ValueOrDefault => this is Ok ok ? ok.Value : default;

    /// <summary>True only for <see cref="Ok"/>.</summary>
    public bool IsOk => this is Ok;
}
