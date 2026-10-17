using IISMonitor.Core.Collection;
using IISMonitor.Core.Requests;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace IISMonitor.Collectors.Etw;

/// <summary>
/// Listens to the Microsoft-Windows-IIS-Logging ETW provider. With the site's log target set to
/// include ETW, IIS emits one event (ID 6200) per completed request carrying the W3C log fields,
/// so response times, the request log and the blacklist see each request the moment it finishes.
/// </summary>
internal sealed class IisLogMonitor : IDisposable
{
    public static readonly Guid ProviderId = new("7E8AD27F-B271-4EA2-A783-A47BDE29143B");

    private readonly string _sessionName;
    private readonly RequestPipeline _pipeline;
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile string? _error;
    private long _events;
    private long _failures;

    public IisLogMonitor(string sessionName, RequestPipeline pipeline)
    {
        _sessionName = sessionName;
        _pipeline = pipeline;
    }

    public bool Running => _session is not null && _error is null;

    public string? Error => _error;

    public long EventCount => Interlocked.Read(ref _events);

    /// <summary>Events the request pipeline threw on (counted, skipped).</summary>
    public long FailureCount => Interlocked.Read(ref _failures);

    /// <summary>Events the session dropped (buffers full); the request log and the blacklist miss those.</summary>
    public long EventsLost
    {
        get
        {
            try
            {
                return _session?.EventsLost ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new UnauthorizedAccessException("ETW tracing needs administrator rights.");

        // Larger buffers than the default: every request is an event, and hit counts depend on all of them.
        _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 64 };
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
        // The request event is ID 6200; other events don't carry time-taken and fail to parse below.
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
        try
        {
            _pipeline.Process(e with { TimeUtc = data.TimeStamp.ToUniversalTime() });
        }
        catch (Exception)
        {
            // One odd request must not stop the trace.
            Interlocked.Increment(ref _failures);
        }
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
