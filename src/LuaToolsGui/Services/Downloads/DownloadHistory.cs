using System.Globalization;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// The persisted shape of a finished download. Deliberately a flat POCO of primitives: a
/// <see cref="DownloadJob"/> holds delegates and cannot be serialized, so history records what happened
/// rather than anything that could be resumed.
/// </summary>
/// <remarks>
/// Every field is either a number, an enum name or text the app itself composed from a resource string.
/// Nothing carried in a job's credentials — a Hubcap key, a bearer token, a signed URL — reaches this
/// record: <see cref="ManifestJobFactory"/> builds <c>Title</c>/<c>SubTitle</c> from the game name and the
/// source's display name only, and <c>Message</c> is a localized install result. <c>RevealPath</c> is the
/// one filesystem path kept, because "Show in folder" has nothing to open without it; it is sanitized on
/// the way in by <see cref="Sanitized"/>.
/// </remarks>
public sealed record DownloadHistoryRecord(
    string Id,
    string Kind,
    long AppId,
    string Title,
    string SubTitle,
    long Bytes,
    string Status,
    string? Message,
    long CompletedAtMs,

    /// <summary>
    /// What "Show in folder" opens for this entry: the installed file, or the depot output folder.
    /// </summary>
    /// <remarks>
    /// Trailing and nullable so a cache.json written before this field existed still deserializes — the
    /// property is simply absent and lands as null, which reads as "nothing to show" and hides the menu
    /// item rather than offering a dead path.
    /// </remarks>
    string? RevealPath = null)
{
    /// <summary>
    /// The record as it is safe to persist: free text run through <see cref="LogSanitizer"/>.
    /// </summary>
    /// <remarks>
    /// <c>Message</c> is the only field built from something the app did not compose itself — a failure
    /// path can put an exception message there, and those are assembled from HTTP bodies elsewhere in the
    /// app (see <c>AuthService</c>). cache.json is plain text in the roaming profile, so it gets the same
    /// treatment the crash log does rather than being trusted because it usually holds nothing sensitive.
    /// </remarks>
    public DownloadHistoryRecord Sanitized() => this with
    {
        Title = LogSanitizer.Sanitize(Title),
        SubTitle = LogSanitizer.Sanitize(SubTitle),
        Message = Message is null ? null : LogSanitizer.Sanitize(Message),
    };
}

/// <summary>A finished download as shown in the Downloads page's history list.</summary>
public sealed class DownloadHistoryEntry
{
    public DownloadHistoryEntry(DownloadHistoryRecord record)
    {
        Record = record;
        Status = Enum.TryParse<DownloadStatus>(record.Status, out var s) ? s : DownloadStatus.Completed;
    }

    public DownloadHistoryRecord Record { get; }
    public DownloadStatus Status { get; }

    public string Id => Record.Id;
    public long AppId => Record.AppId;
    public string Title => Record.Title;
    public string SubTitle => Record.SubTitle;
    public string? Message => Record.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Record.Message);
    public bool Failed => Status is DownloadStatus.Failed;

    public string SizeLabel => Record.Bytes > 0 ? ByteSize.Format(Record.Bytes) : "";

    public string StatusLabel => Status switch
    {
        DownloadStatus.Completed => Resources.Strings.Downloads_Status_Completed,
        DownloadStatus.Failed => Resources.Strings.Downloads_Status_Failed,
        _ => Resources.Strings.Downloads_Status_Cancelled,
    };

    /// <summary>See <see cref="DownloadItem.CanCopyAppId"/>.</summary>
    public bool CanCopyAppId => Record.AppId > 0;

    public bool CanShowInFolder => !string.IsNullOrWhiteSpace(Record.RevealPath);

    public string WhenLabel => DateTimeOffset.FromUnixTimeMilliseconds(Record.CompletedAtMs)
        .LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    public static DownloadHistoryRecord From(DownloadItem item, DownloadStatus status) => new(
        item.Id,
        item.Job.Kind.ToString(),
        item.AppId,
        item.Title,
        item.SubTitle,
        item.BytesRead,
        status.ToString(),
        item.Message,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        item.RevealPath);
}
