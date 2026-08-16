using System.Collections.Concurrent;

namespace LuaToolsGui.Services;

/// <summary>
/// Tells the user, once per session, when LuaTools has to talk to a server over plain HTTP.
///
/// <para>
/// This fork removed every cleartext transfer that carried a SECRET (see the DonateKeys note on
/// <see cref="AppConfig"/>). One cleartext call survives — the source-availability lookup
/// <c>GET {ManifestBackendUrl}/check_apis?appid=…</c> — because that host serves no usable TLS: :443
/// answers with Traefik's self-signed placeholder certificate and 404s every real route. Dropping the
/// call would remove a working feature; leaving it silent would hide a real, if small, privacy cost.
/// </para>
///
/// <para>
/// The cost is METADATA, not credentials: no token and no key is sent, but an observer on the path
/// learns which Steam appid was looked up, and can forge the answer. So this is a NOTICE, not a consent
/// gate — it never blocks, never prompts, and the request proceeds either way. Users who already know
/// can silence it with <c>"WarnOnInsecureMetadata": false</c> in settings.json.
/// </para>
///
/// <para>
/// Default cadence is once per SESSION and per endpoint key: this lookup runs on essentially every page
/// of search results, and a toast per request would be noise that trains people to ignore it. Users who
/// want the noise — auditing what the app actually contacts — can set
/// <c>"InsecureMetadataNotice": "always"</c>, and <c>"off"</c> silences it. The request itself is
/// unaffected by all three; to stop the call happening at all, use
/// <c>"EnableSourceAvailabilityChecks": false</c>.
/// </para>
/// </summary>
public sealed class InsecureTransportNotice(SettingsService settings, ToastService toast)
{
    private readonly ConcurrentDictionary<string, byte> _alreadyWarned = new(StringComparer.Ordinal);

    /// <summary>
    /// Decides whether a notice is due. Pure so the throttling rule is testable without WPF or a
    /// settings file behind it.
    /// </summary>
    /// <param name="mode">The user's <c>InsecureMetadataNotice</c> preference.</param>
    /// <param name="alreadyWarnedForKey">Whether this endpoint key already produced a notice.</param>
    internal static bool ShouldNotify(InsecureMetadataNoticeMode mode, bool alreadyWarnedForKey) => mode switch
    {
        InsecureMetadataNoticeMode.Off => false,
        InsecureMetadataNoticeMode.Always => true,   // every call, regardless of history
        _ => !alreadyWarnedForKey,                   // Once
    };

    /// <summary>
    /// Show the cleartext-metadata notice for <paramref name="host"/>, subject to the configured mode.
    /// Never throws and never blocks: a failure to warn must not break the request it describes.
    /// </summary>
    public void NotifyMetadataOverHttp(string host)
    {
        try
        {
            // Claim first: TryAdd is atomic, so concurrent lookups can't produce a burst of toasts in
            // Once mode. In Always mode the claim is irrelevant — every call notifies by design.
            bool firstTime = _alreadyWarned.TryAdd(host, 0);
            if (!ShouldNotify(settings.InsecureMetadataNotice, alreadyWarnedForKey: !firstTime)) return;

            PluginLog.Log($"privacy: source-availability lookup uses plain HTTP ({host}); " +
                          "the requested appid is visible to the network path");

            toast.Show(
                Resources.Strings.Privacy_HttpMetadata_Title,
                string.Format(Resources.Strings.Privacy_HttpMetadata_Body, host));
        }
        catch
        {
            // A notice is best-effort by definition.
        }
    }
}
