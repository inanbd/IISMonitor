using System.Collections.Concurrent;
using IISMonitor.Core.Collection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace IISMonitor.Collectors.Etw;

/// <summary>
/// Real-time kernel ETW session that counts, per process, file read/write bytes and TCP/UDP bytes
/// sent and received — the same data Resource Monitor shows. Requires administrator rights.
/// </summary>
internal sealed class KernelIoMonitor : IDisposable
{
    private readonly string _sessionName;
    private readonly ConcurrentDictionary<int, Totals> _totals = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile string? _error;
    private long _lostEvents;

    public KernelIoMonitor(string sessionName) => _sessionName = sessionName;

    public bool Running => _session is not null && _error is null;

    public string? Error => _error;

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new UnauthorizedAccessException("Kernel tracing needs administrator rights.");

        _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 64 };
        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Process
            | KernelTraceEventParser.Keywords.Thread
            | KernelTraceEventParser.Keywords.NetworkTCPIP
            | KernelTraceEventParser.Keywords.FileIOInit);

        var kernel = _session.Source.Kernel;
        kernel.TcpIpSend += e => Add(e.ProcessID, Kind.NetSent, e.size);
        kernel.TcpIpRecv += e => Add(e.ProcessID, Kind.NetReceived, e.size);
        kernel.TcpIpSendIPV6 += e => Add(e.ProcessID, Kind.NetSent, e.size);
        kernel.TcpIpRecvIPV6 += e => Add(e.ProcessID, Kind.NetReceived, e.size);
        kernel.UdpIpSend += e => Add(e.ProcessID, Kind.NetSent, e.size);
        kernel.UdpIpRecv += e => Add(e.ProcessID, Kind.NetReceived, e.size);
        kernel.UdpIpSendIPV6 += e => Add(e.ProcessID, Kind.NetSent, e.size);
        kernel.UdpIpRecvIPV6 += e => Add(e.ProcessID, Kind.NetReceived, e.size);
        kernel.FileIORead += e => Add(e.ProcessID, Kind.DiskRead, e.IoSize);
        kernel.FileIOWrite += e => Add(e.ProcessID, Kind.DiskWrite, e.IoSize);

        var source = _session.Source;
        _thread = new Thread(() =>
        {
            try
            {
                source.Process();
            }
            catch (Exception e)
            {
                _error = "Kernel trace stopped: " + e.Message;
            }
        })
        {
            IsBackground = true,
            Name = "IISMonitor kernel ETW",
        };
        _thread.Start();
    }

    /// <summary>Cumulative totals for the given processes; also forgets processes that are gone.</summary>
    public Dictionary<int, IoTotals> Read(IReadOnlyCollection<int> pids, IReadOnlySet<int> alivePids)
    {
        foreach (var pid in _totals.Keys)
        {
            if (!alivePids.Contains(pid))
                _totals.TryRemove(pid, out _);
        }

        if (_session is { } session)
            Interlocked.Exchange(ref _lostEvents, session.EventsLost);

        var result = new Dictionary<int, IoTotals>(pids.Count);
        foreach (var pid in pids)
        {
            if (_totals.TryGetValue(pid, out var t))
            {
                result[pid] = new IoTotals(
                    Interlocked.Read(ref t.DiskRead),
                    Interlocked.Read(ref t.DiskWrite),
                    Interlocked.Read(ref t.NetSent),
                    Interlocked.Read(ref t.NetReceived));
            }
        }

        return result;
    }

    public long LostEvents => Interlocked.Read(ref _lostEvents);

    private void Add(int pid, Kind kind, int bytes)
    {
        if (pid <= 0 || bytes <= 0)
            return;

        var totals = _totals.GetOrAdd(pid, static _ => new Totals());
        switch (kind)
        {
            case Kind.DiskRead:
                Interlocked.Add(ref totals.DiskRead, bytes);
                break;
            case Kind.DiskWrite:
                Interlocked.Add(ref totals.DiskWrite, bytes);
                break;
            case Kind.NetSent:
                Interlocked.Add(ref totals.NetSent, bytes);
                break;
            case Kind.NetReceived:
                Interlocked.Add(ref totals.NetReceived, bytes);
                break;
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
            // The session may already be gone; nothing else to clean up.
        }

        _thread?.Join(TimeSpan.FromSeconds(5));
        _session = null;
    }

    private enum Kind
    {
        DiskRead,
        DiskWrite,
        NetSent,
        NetReceived,
    }

    private sealed class Totals
    {
        public long DiskRead;
        public long DiskWrite;
        public long NetSent;
        public long NetReceived;
    }
}
