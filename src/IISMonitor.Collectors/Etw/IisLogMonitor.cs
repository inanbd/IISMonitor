using IISMonitor.Core.Collection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace IISMonitor.Collectors.Etw;

/// <summary>
/// Listens to the Microsoft-Windows-IIS-Logging ETW provider. With the site's log target set to
/// include ETW, IIS emits one event per completed request carrying the W3C log fields, so response
/// times arrive the moment each request finishes.
/// </summary>
internal sealed class IisLogMonitor : IDisposable
{
    public static readonly Guid ProviderId = new("7E8AD27F-B271-4EA2-A783-A47BDE29143B");

    private readonly string _sessionName;
    private readonly ResponseTimeAggregator _aggregator;
    private volatile ApplicationMatcher _matcher = new([]);
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile string? _error;
    private long _events;
    private long _unmatched;

    public IisLogMonitor(string sessionName, ResponseTimeAggregator aggregator)
    {
        _sessionName = sessionName;
        _aggregator = aggregator;
    }

    public bool Running => _session is not null && _error is null;

    public string? Error => _error;

    public long EventCount => Interlocked.Read(ref _events);

    public long UnmatchedCount => Interlocked.Read(ref _unmatched);

    public void UpdateSites(IEnumerable<SiteInfo> sites) => _matcher = new ApplicationMatcher(sites);

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new UnauthorizedAccessException("ETW tracing needs administrator rights.");

        _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
        var source = _session.Source;

        // The provider is manifest-based and registered by IIS; this parser decodes it from the registered manifest.
        var parser = new RegisteredTraceEventParser(source);
        parser.All += OnEvent;
        _session.EnableProvider(ProviderId, TraceEventLevel.Verbose);

        _thread = new Thread(() =>
        {
            try
            {
                source.Process();
            }
            catch (Exception e)
            {
                _error = "IIS log trace stopped: " + e.Message;
            }
        })
        {
            IsBackground = true,
            Name = "IISMonitor IIS log ETW",
        };
        _thread.Start();
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProviderGuid != ProviderId)
            return;

        var names = data.PayloadNames;
        if (names is null || names.Length == 0)
            return;

        var payload = new KeyValuePair<string, object?>[names.Length];
        for (var i = 0; i < names.Length; i++)
            payload[i] = new(names[i], SafeValue(data, i));

        if (!IisLogEventParser.TryParse(payload, out var e))
            return;

        Interlocked.Increment(ref _events);
        var matcher = _matcher;
        if (matcher.ResolveSiteId(e) is not { } siteId)
        {
            Interlocked.Increment(ref _unmatched);
            return;
        }

        _aggregator.Record(siteId, matcher.ResolveAppPool(siteId, e.UriStem), e.TimeTakenMs, e.StatusCode);
    }

    private static object? SafeValue(TraceEvent data, int index)
    {
        try
        {
            return data.PayloadValue(index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _session?.Dispose();
        }
        catch (Exception)
        {
        }

        _thread?.Join(TimeSpan.FromSeconds(5));
        _session = null;
    }
}
