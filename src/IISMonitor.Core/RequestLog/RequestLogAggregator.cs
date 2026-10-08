namespace IISMonitor.Core.RequestLog;

/// <summary>
/// Collects tracked requests in memory (called on the ETW thread) as one row per app pool, minute,
/// client IP, URL, method and status, until the recorder writes them.
/// </summary>
public sealed class RequestLogAggregator
{
    public const int MaxUrlLength = 512;
    public const int DefaultMaxPendingRows = 100_000;
    public const string UnknownClient = "(unknown)";

    private const long MinuteMs = 60_000;

    private readonly object _gate = new();
    private readonly int _maxPendingRows;
    private Dictionary<Key, RequestLogEntry> _pending = new(KeyComparer.Instance);
    private long _recorded;
    private long _dropped;

    public RequestLogAggregator(int maxPendingRows = DefaultMaxPendingRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingRows);
        _maxPendingRows = maxPendingRows;
    }

    /// <summary>Requests recorded since start (including dropped ones).</summary>
    public long RecordedRequests => Interlocked.Read(ref _recorded);

    /// <summary>Requests not itemised because the pending-row cap was reached, since start.</summary>
    public long DroppedRequests => Interlocked.Read(ref _dropped);

    public int PendingRows
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Thread-safe.</summary>
    public void Record(string appPool, DateTime timeUtc, string? clientIp, string? method, string uriStem,
                       int status, int subStatus, double timeTakenMs)
    {
        var timestamp = ToUnixMs(timeUtc);
        var key = new Key(
            appPool ?? "",
            FloorToMinute(timestamp),
            string.IsNullOrEmpty(clientIp) || clientIp == "-" ? UnknownClient : clientIp,
            NormalizeUrl(uriStem),
            method is null || method == "-" ? "" : method,
            status,
            subStatus);
        var timeMs = double.IsFinite(timeTakenMs) && timeTakenMs > 0 ? timeTakenMs : 0;

        lock (_gate)
        {
            Interlocked.Increment(ref _recorded);
            if (_pending.TryGetValue(key, out var entry))
            {
                entry.Hits++;
                entry.TotalTimeMs += timeMs;
                if (timestamp < entry.FirstUnixMs)
                    entry.FirstUnixMs = timestamp;
                if (timestamp > entry.LastUnixMs)
                    entry.LastUnixMs = timestamp;
                return;
            }

            if (_pending.Count >= _maxPendingRows)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            _pending.Add(key, new RequestLogEntry
            {
                AppPool = key.Pool,
                MinuteUnixMs = key.Minute,
                ClientIp = key.ClientIp,
                Url = key.Url,
                Method = key.Method,
                Status = status,
                SubStatus = subStatus,
                Hits = 1,
                FirstUnixMs = timestamp,
                LastUnixMs = timestamp,
                TotalTimeMs = timeMs,
            });
        }
    }

    /// <summary>Removes and returns everything recorded so far.</summary>
    public List<RequestLogEntry> Drain()
    {
        Dictionary<Key, RequestLogEntry> drained;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];
            drained = _pending;
            _pending = new Dictionary<Key, RequestLogEntry>(KeyComparer.Instance);
        }

        return [.. drained.Values];
    }

    /// <summary>Unix milliseconds; an Unspecified time is taken as UTC, a Local one is converted.</summary>
    internal static long ToUnixMs(DateTime time)
    {
        var utc = time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time,
        };
        return (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>Start of the minute that contains <paramref name="unixMs"/>.</summary>
    internal static long FloorToMinute(long unixMs) => unixMs - FloorMod(unixMs, MinuteMs);

    internal static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "/";
        if (url.Length <= MaxUrlLength)
            return url;

        // Don't split a surrogate pair; the stored text must stay valid UTF-16.
        var length = MaxUrlLength - 1;
        if (char.IsHighSurrogate(url[length - 1]))
            length--;
        return string.Concat(url.AsSpan(0, length), "…");
    }

    private static long FloorMod(long value, long divisor) => ((value % divisor) + divisor) % divisor;

    private readonly record struct Key(string Pool, long Minute, string ClientIp, string Url, string Method, int Status, int SubStatus);

    /// <summary>App pool names compare ignoring case (as IIS does); everything else is ordinal.</summary>
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key x, Key y) =>
            x.Minute == y.Minute
            && x.Status == y.Status
            && x.SubStatus == y.SubStatus
            && string.Equals(x.Url, y.Url, StringComparison.Ordinal)
            && string.Equals(x.ClientIp, y.ClientIp, StringComparison.Ordinal)
            && string.Equals(x.Method, y.Method, StringComparison.Ordinal)
            && string.Equals(x.Pool, y.Pool, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Key key) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Pool),
            key.Minute,
            StringComparer.Ordinal.GetHashCode(key.ClientIp),
            StringComparer.Ordinal.GetHashCode(key.Url),
            StringComparer.Ordinal.GetHashCode(key.Method),
            key.Status,
            key.SubStatus);
    }
}
