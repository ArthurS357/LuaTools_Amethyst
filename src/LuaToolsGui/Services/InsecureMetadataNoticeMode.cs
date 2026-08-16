namespace LuaToolsGui.Services;

/// <summary>How often to disclose the cleartext source-availability lookup.</summary>
public enum InsecureMetadataNoticeMode
{
    /// <summary>One notice per session, per endpoint. The default: informs without becoming wallpaper.</summary>
    Once,

    /// <summary>A notice before every call. Noisy on purpose — for auditing what the app actually contacts.</summary>
    Always,

    /// <summary>No notice. The request is unchanged; only the disclosure is suppressed.</summary>
    Off,
}

/// <summary>Parsing for the <c>InsecureMetadataNotice</c> setting.</summary>
public static class InsecureMetadataNoticeModeExtensions
{
    /// <summary>
    /// Parse the settings.json value, falling back to <see cref="InsecureMetadataNoticeMode.Once"/>.
    ///
    /// <para>
    /// An unrecognised value deliberately falls back to <c>Once</c> rather than <c>Off</c>: a typo
    /// ("nver", "false", "no") must not silently switch a privacy disclosure off. Failing toward MORE
    /// disclosure is the safe direction here.
    /// </para>
    /// </summary>
    /// <param name="value">Raw setting value, or null when unset.</param>
    /// <param name="legacyWarnEnabled">
    /// The superseded <c>WarnOnInsecureMetadata</c> boolean, consulted only when <paramref name="value"/>
    /// is unset, so an existing opt-out keeps working.
    /// </param>
    public static InsecureMetadataNoticeMode ParseNoticeMode(string? value, bool? legacyWarnEnabled = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return legacyWarnEnabled is false ? InsecureMetadataNoticeMode.Off : InsecureMetadataNoticeMode.Once;

        return value.Trim().ToLowerInvariant() switch
        {
            "always" or "every" or "each" => InsecureMetadataNoticeMode.Always,
            "off" or "never" or "none" => InsecureMetadataNoticeMode.Off,
            _ => InsecureMetadataNoticeMode.Once,
        };
    }
}
