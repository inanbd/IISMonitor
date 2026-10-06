using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using IISMonitor.Collectors;
using IISMonitor.Core.Protocol;

namespace IISMonitor.Service;

/// <summary>Runs the monitor engine and the named pipe that dashboards connect to.</summary>
public sealed class CollectorService(ILogger<CollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var engine = new MonitorEngine(new MonitorEngineOptions
        {
            EtwSessionPrefix = "IISMonitor-Service",
            Log = (message, exception) => logger.LogWarning(exception, "{Message}", message),
        });

        engine.Start();
        logger.LogInformation("IISMonitor collector started; sampling every {Interval} ms.", engine.Settings.SampleIntervalMs);

        var server = new PipeServer(engine, CreatePipe, (message, exception) => logger.LogWarning(exception, "{Message}", message));
        try
        {
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        logger.LogInformation("IISMonitor collector stopping.");
    }

    /// <summary>Only administrators (and SYSTEM) may connect: the data describes the whole server.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
