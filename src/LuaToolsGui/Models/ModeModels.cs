using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

/// <summary>The Steam fixes. Mutually exclusive — only one is active at a time. Each fetches its
/// own files from its own release; switching overwrites as needed but doesn't delete old files.</summary>
public enum UnlockerMode { SteamTools, OpenSteamTools, CloudRedirect, OpenSteamToolsNightly }

/// <summary>How a mode's files are delivered/installed.</summary>
public enum ModeKind
{
    Loose,  // loose DLL assets copied into the Steam root (each verified by its own digest)
    Zip,    // a single release zip to download, verify, and extract
    Cli,    // download a CLI tool and run it; it patches/deploys everything itself
}

/// <summary>State of a mode's files vs. the latest GitHub release.</summary>
public enum ModeStatus
{
    Unknown,        // offline / GitHub unreachable / Steam not located
    NotInstalled,
    UpToDate,
    UpdateAvailable,
}

/// <summary>
/// Static description of one unlocker backend: where its files come from and what to place/clean.
/// </summary>
public sealed record ModeDefinition(
    UnlockerMode Mode,
    string DisplayName,
    string Description,
    ModeKind Kind,
    string Owner,
    string Repo,
    string? FixedTag,        // e.g. "ST"; null → use the repo's latest release
    string[] PlaceFiles,     // files that end up in the Steam root (for status/verify)
    string? ZipAssetPattern, // e.g. "OpenSteamTool-{version}-Release.zip"; null unless Kind == Zip
    string? CliAssetName,    // Kind == Cli: the tool to download (e.g. "CloudRedirectCLI.exe")
    string? CliArgs,         // Kind == Cli: args to run it with (e.g. "/stfixer")
    string? VerifyFile,      // Kind == Cli: the file whose digest confirms success (e.g. "cloud_redirect.dll")
    string? HiddenUnlessFile = null); // if set, the card is hidden unless this file exists in the Steam root (or the mode is active)

// ── GitHub release API DTOs ─────────────────────────────────────────
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    // published_at is reliable for ordering; the /releases list array itself sorts by created_at
    // (which can be identical across tags created at once), so sort by this to find the latest.
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = [];
}

public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; } // "sha256:<hex>"
}

/// <summary>Queried state for one mode — what a Mode-page card binds to.</summary>
public sealed record ModeState(
    UnlockerMode Mode,
    ModeStatus Status,
    bool IsActive,           // is this the currently-selected (active) mode
    string? LatestVersion);  // resolved release tag (for display)

/// <summary>State of the CloudRedirect add-on (a feature of the OpenSteamTool Nightly build), derived
/// from disk (cloud_redirect.dll presence + [cloud] enabled in opensteamtool.toml) and the latest
/// CloudRedirect release.</summary>
public sealed record CloudRedirectAddonState(
    bool Installed,          // cloud_redirect.dll is present in the Steam root
    bool Enabled,            // opensteamtool.toml has [cloud] enabled = true
    bool UpdateAvailable,    // on-disk dll differs from the latest release asset
    string? LatestVersion);  // latest CloudRedirect release tag (for display), null if unknown

/// <summary>Outcome of installing/switching to a mode. Mirrors LuaInstaller.InstallResult.</summary>
public sealed record ModeInstallResult(bool Success, string? Error, IReadOnlyList<string> Failed)
{
    public static ModeInstallResult Ok() => new(true, null, []);
    public static ModeInstallResult Fail(string error) => new(false, error, []);
}
