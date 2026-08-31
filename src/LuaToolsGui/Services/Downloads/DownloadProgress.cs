namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// Byte-level progress from a streaming download. <see cref="TotalBytes"/> is null when the response
/// carried no Content-Length, in which case the UI shows an indeterminate bar.
/// </summary>
/// <remarks>
/// Replaces the <c>IProgress&lt;double?&gt;</c> contract on the manifest download paths. The byte counts
/// were always available inside the copy loop; folding them into a fraction and discarding them is what
/// made size, speed and ETA impossible to show. Downloads that are not queue jobs (the tool fetches in
/// <see cref="GithubProxy"/>, the unlocker, the plugin) still report a bare fraction.
/// </remarks>
public readonly record struct DownloadProgress(long BytesRead, long? TotalBytes)
{
    /// <summary>0..1 completion, or null when the total length is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? (double)BytesRead / TotalBytes.Value : null;
}

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread.
/// </summary>
/// <remarks>
/// Deliberately NOT <see cref="Progress{T}"/>: that type captures the creating SynchronizationContext and
/// posts every report to it. A download reports once per 80 KB chunk, so a 2 GB file would post ~25,000
/// messages to the WPF dispatcher and flood the UI thread; a depot run reports thousands of created-file
/// paths that are never displayed at all. <see cref="DownloadQueue"/> does its own time-throttled
/// marshalling instead, so reports must stay on the calling thread and be cheap.
/// </remarks>
public sealed class ProgressRelay<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
