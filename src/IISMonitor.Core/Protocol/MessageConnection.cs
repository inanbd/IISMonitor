using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IISMonitor.Core.Protocol;

public static class PipeProtocol
{
    public const string PipeName = "IISMonitor.v1";
    public const int Version = 1;

    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Settings = "settings";
    public const string Reply = "reply";
    public const string Error = "error";

    public const string UpdateSettings = "updateSettings";
    public const string QueryHistory = "queryHistory";
    public const string ListEntities = "listEntities";
    public const string QuerySlowQueries = "querySlowQueries";
    public const string EnableIisEtwLogging = "enableIisEtwLogging";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class Envelope
{
    public string Type { get; set; } = "";

    /// <summary>Request ID; replies carry the ID of the request they answer. 0 for notifications.</summary>
    public long Id { get; set; }

    public JsonElement? Payload { get; set; }

    public static Envelope Create<T>(string type, T payload, long id = 0) => new()
    {
        Type = type,
        Id = id,
        Payload = JsonSerializer.SerializeToElement(payload, PipeProtocol.Json),
    };

    public T Read<T>() =>
        Payload is { } payload
            ? payload.Deserialize<T>(PipeProtocol.Json) ?? throw new InvalidDataException($"Empty '{Type}' payload.")
            : throw new InvalidDataException($"Missing '{Type}' payload.");
}

public sealed class HelloPayload
{
    public int ProtocolVersion { get; set; }
    public string MachineName { get; set; } = "";
    public Settings.MonitorSettings Settings { get; set; } = new();
}

public sealed class ErrorPayload
{
    public string Message { get; set; } = "";
}

/// <summary>Newline-delimited JSON messages over a duplex stream. Sends are serialized; receive from one reader only.</summary>
public sealed class MessageConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public MessageConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, PipeProtocol.Json);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Returns null when the other side closed the connection.</summary>
    public async Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Length == 0)
                continue;
            return JsonSerializer.Deserialize<Envelope>(line, PipeProtocol.Json)
                ?? throw new InvalidDataException("Received an empty message.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
