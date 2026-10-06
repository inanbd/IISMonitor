using IISMonitor.Service;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "IISMonitor");
builder.Logging.AddEventLog(new EventLogSettings { SourceName = "IISMonitor", LogName = "Application" });
builder.Services.AddHostedService<CollectorService>();

builder.Build().Run();
