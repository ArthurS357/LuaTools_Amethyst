using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuaToolsGui.Services;

/// <summary>
/// Caches Steam cover images on disk and decoded frozen bitmaps in memory so each is downloaded and
/// decoded once, then shared between Manage and Depots without holding file handles or churning GC.
/// </summary>
public class CoverCache
{
    private static readonly string CoversDir =
        Path.Combine(AppDataMigrator.CurrentDirectory, "covers");

    // Cards draw the cover 248 DIP wide. Decoding at the system DPI keeps it crisp at 125-150% instead of
    // upscaling a 248 px bitmap; capped at Steam's 460 px header width (no gain past the source).
    private const int CardCoverWidthDip = 248;
    private const int SourceHeaderWidthPx = 460;
    internal static readonly int CardDecodePixelWidth = DecodeWidthForScale(SystemDpiScale());
    // ~200 covers at 100% (≈23 MB). Higher-DPI decodes are larger, so pin fewer to hold the same byte budget,
    // never below two pages of the largest page size (48).
    internal static readonly int MaxStrongDecodedEntries =
        Math.Max(96, 200 * CardCoverWidthDip * CardCoverWidthDip / (CardDecodePixelWidth * CardDecodePixelWidth));

    internal static int DecodeWidthForScale(double scale) =>
        (int)Math.Clamp(Math.Ceiling(CardCoverWidthDip * scale), CardCoverWidthDip, SourceHeaderWidthPx);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static double SystemDpiScale()
    {
        try { return GetDpiForSystem() / 96.0; }
        catch (EntryPointNotFoundException) { return 1; } // pre-1607 Windows: keep the 100% width
    }

    // Reject only truncated/empty bodies. We don't gate on a size threshold: some legit covers are
    // tiny (e.g. Undertale 391540's header.jpg is ~5 KB — mostly black, highly compressible), and a
    // size gate wrongly rejected them. Validity is decided by JPEG magic bytes instead (see IsJpeg).
    private const int MinValidBytes = 512;

    // Steam's grey "Header Capsule" placeholder. The predictable CDN URL
    // (cdn.../steam/apps/<id>/header.jpg) serves this static image for apps that have no CDN header yet
    // (typically newer releases, e.g. Silent Hill f 2947440). It's a *valid* JPEG, so the magic-byte
    // check passes and it would otherwise be cached as the cover. We fingerprint it exactly (byte length
    // + SHA-256) and reject it so the caller falls back to the appdetails header_image (the real art).
    private const int PlaceholderLength = 9816;
    private const string PlaceholderSha256 =
        "732ec27f2af650fe079f1c83b0bb0c712a322dc175f383504176724675ad2700";

    /// <summary>True if these bytes are Steam's "Header Capsule" placeholder (not a real cover).</summary>
    private static bool IsHeaderCapsulePlaceholder(byte[] b)
    {
        if (b.Length != PlaceholderLength) return false;
        var hash = Convert.ToHexString(SHA256.HashData(b));
        return hash.Equals(PlaceholderSha256, StringComparison.OrdinalIgnoreCase);
    }

    // A cover is a ~20-100 KB header.jpg; anything near this is not one.
    internal const int MaxCoverBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    // Redirects are followed by hand (SendCheckingRedirectsAsync), so the handler must not follow them itself.
    public CoverCache() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal CoverCache(HttpMessageHandler handler) => _http = new HttpClient(handler) { Timeout = FetchBudget };
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task<string?>> _inFlight = new();
    private readonly ConcurrentDictionary<long, byte> _noCover = new(); // appids with no usable cover (this session)
    private readonly ConcurrentDictionary<long, string> _validatedPaths = new();
    // Strong LRU + weak fallback share one lock so invalidation can't race a weak-ref promotion.
    private readonly Dictionary<long, WeakReference<ImageSource>> _weakDecoded = new();
    private readonly object _decodedLock = new();
    private readonly LinkedList<long> _lruOrder = new();
    private readonly Dictionary<long, (ImageSource Image, LinkedListNode<long> Node)> _strongLru = new();

    private static string PathFor(long appid) => Path.Combine(CoversDir, $"{appid}.jpg");

    /// <summary>True if the bytes start with the JPEG magic number (FF D8 FF).</summary>
    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>Local cover path if already cached, else null. A cover cached as the grey "Header
    /// Capsule" placeholder by an older build is purged here once per session (memoized in RAM after the
    /// first check so grid/list switches and reloads don't re-hash files).</summary>
    public string? GetLocalPath(long appid)
    {
        if (_validatedPaths.TryGetValue(appid, out string? known))
        {
            if (File.Exists(known)) return known;
            _validatedPaths.TryRemove(appid, out _);
            InvalidateDecoded(appid);
            return null;
        }

        string p = PathFor(appid);
        if (!File.Exists(p)) return null;

        try
        {
            if (new FileInfo(p).Length == PlaceholderLength &&
                IsHeaderCapsulePlaceholder(File.ReadAllBytes(p)))
            {
                File.Delete(p);
                InvalidateDecoded(appid);
                return null; // stale placeholder removed → caller re-resolves via header_image
            }
        }
        catch { /* if we can't check/delete, just use what's there */ }

        _validatedPaths[appid] = p;
        return p;
    }

    /// <summary>Return an already-decoded, frozen cover image from memory, or null on miss.</summary>
    public ImageSource? TryGetDecoded(long appid)
    {
        lock (_decodedLock)
        {
            if (_strongLru.TryGetValue(appid, out var entry))
            {
                _lruOrder.Remove(entry.Node);
                _lruOrder.AddFirst(entry.Node);
                return entry.Image;
            }

            if (_weakDecoded.TryGetValue(appid, out var weak) && weak.TryGetTarget(out var resurrected))
                return StoreDecodedLocked(appid, resurrected);

            return null;
        }
    }

    /// <summary>
    /// Return a cached frozen cover bitmap, or decode <paramref name="localPath"/> once at thumbnail width
    /// (<see cref="CardDecodePixelWidth"/>) and retain it in the bounded LRU + weak cache.
    /// </summary>
    public ImageSource? GetOrDecode(long appid, string localPath)
    {
        if (TryGetDecoded(appid) is { } hit) return hit;
        if (string.IsNullOrWhiteSpace(localPath)) return null;

        var decoded = DecodeCoverFile(localPath);
        if (decoded is null) return null;

        lock (_decodedLock) return StoreDecodedLocked(appid, decoded);
    }

    private ImageSource StoreDecodedLocked(long appid, ImageSource candidate)
    {
        if (_strongLru.TryGetValue(appid, out var existing))
        {
            _lruOrder.Remove(existing.Node);
            _lruOrder.AddFirst(existing.Node);
            return existing.Image;
        }

        if (_strongLru.Count >= MaxStrongDecodedEntries && _lruOrder.Last is { } oldest)
        {
            _lruOrder.RemoveLast();
            _strongLru.Remove(oldest.Value); // demoted, still reachable through _weakDecoded while alive
        }

        _strongLru[appid] = (candidate, _lruOrder.AddFirst(appid));
        _weakDecoded[appid] = new WeakReference<ImageSource>(candidate);
        return candidate;
    }

    /// <summary>Decoded covers currently pinned by the strong LRU (bounded by MaxStrongDecodedEntries).</summary>
    internal int StrongDecodedCount
    {
        get { lock (_decodedLock) return _strongLru.Count; }
    }

    private void InvalidateDecoded(long appid)
    {
        lock (_decodedLock)
        {
            if (_strongLru.Remove(appid, out var entry))
                _lruOrder.Remove(entry.Node);
            _weakDecoded.Remove(appid);
        }
    }

    private static ImageSource? DecodeCoverFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read fully in EndInit → file handle released
            // WPF's process-wide URI cache would hand back the old bitmap after a re-download to the same path.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = CardDecodePixelWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True if we already determined this appid has no usable cover (don't keep retrying).</summary>
    public bool IsKnownMissing(long appid) => _noCover.ContainsKey(appid);

    /// <summary>Remember that an appid has no cover from any source, so we stop attempting it.</summary>
    public void MarkMissing(long appid) => _noCover[appid] = 0;

    /// <summary>
    /// Return the cached cover path, downloading from <paramref name="remoteUrl"/> first if needed.
    /// Rejects too-small responses (placeholder / error pages). Concurrent calls share one download.
    /// </summary>
    public Task<string?> EnsureAsync(long appid, string remoteUrl, CancellationToken ct = default)
    {
        string path = PathFor(appid);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (string.IsNullOrWhiteSpace(remoteUrl)) return Task.FromResult<string?>(null);

        // One download per appid even if asked concurrently (prefetch + page view).
        return _inFlight.GetOrAdd(appid, _ => DownloadAsync(appid, path, remoteUrl, ct));
    }

    /// <summary>
    /// The cover's bytes, or null for anything that is not one (a refused redirect hop throws
    /// <see cref="ApiException"/>; the caller treats any failure as "no cover"). <paramref name="remoteUrl"/> comes from
    /// the catalog or lua.tools, so it gets the same screening as a signed download (https, public named
    /// host, every redirect hop checked) and the body is read under a size cap and a deadline. As elsewhere
    /// the host NAME is checked, not what it resolves to (see <see cref="LuaToolsApiClient.IsFollowableSignedUrl"/>).
    /// </summary>
    internal async Task<byte[]?> FetchAsync(string remoteUrl, CancellationToken ct)
    {
        if (!LuaToolsApiClient.IsFollowableSignedUrl(remoteUrl)) return null;

        using var res = await LuaToolsApiClient.SendCheckingRedirectsAsync(_http, new Uri(remoteUrl), FetchBudget, ct);
        // A 404 etc. is simply "no cover from this source".
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxCoverBytes) return null;

        using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
        body.CancelAfter(FetchBudget);
        byte[]? bytes = await ReadCappedAsync(await res.Content.ReadAsStreamAsync(body.Token), MaxCoverBytes, body.Token);

        // A real cover is a JPEG: reject error/HTML bodies that slip through with a 200 by the magic bytes,
        // not the size. The predictable CDN URL can also serve Steam's grey placeholder (a valid JPEG) —
        // reject it so the caller falls back to the real appdetails header_image.
        return bytes is null || bytes.Length < MinValidBytes || !IsJpeg(bytes) || IsHeaderCapsulePlaceholder(bytes)
            ? null
            : bytes;
    }

    /// <summary>The whole stream, or null as soon as it holds more than <paramref name="max"/> bytes.</summary>
    internal static async Task<byte[]?> ReadCappedAsync(Stream source, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<string?> DownloadAsync(long appid, string path, string remoteUrl, CancellationToken ct)
    {
        try
        {
            if (await FetchAsync(remoteUrl, ct) is not { } bytes) return null;

            await _ioGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(CoversDir);
                await File.WriteAllBytesAsync(path, bytes, ct);
                _validatedPaths[appid] = path;
                InvalidateDecoded(appid);
            }
            finally
            {
                _ioGate.Release();
            }
            return path;
        }
        catch
        {
            return null; // 404 / offline / write error — caller decides on fallback
        }
        finally
        {
            _inFlight.TryRemove(appid, out _);
        }
    }
}
