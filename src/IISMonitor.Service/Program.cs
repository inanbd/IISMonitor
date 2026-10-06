using IISMonitor.Service;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);

// Running as a service also logs to the Windows Application event log, under the "IISMonitor" source.
builder.Services.AddWindowsService(options => options.ServiceName = "IISMonitor");
builder.Services.Configure<EventLogSettings>(settings => settings.SourceName = "IISMonitor");
builder.Services.AddHostedService<CollectorService>();

builder.Build().Run();
