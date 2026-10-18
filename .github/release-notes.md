Real-time and historical monitoring for every IIS website and application pool on Windows Server 2022 and 2025: CPU, memory, disk, network, response times, every request and its client IP, and the SQL Server load each pool creates, with 7 days of history.

## Download and install

1. Download **IISMonitor-win-x64.zip** below. Before extracting it, right-click it, choose **Properties**, tick **Unblock** and click **OK**.
2. Extract it and copy the folder to your IIS server.
3. Right-click **install-service.cmd** and choose **Run as administrator**. It installs IIS Monitor in `C:\Program Files\IISMonitor`, starts the `IISMonitor` Windows service and adds **IIS Monitor** to the Start menu.
4. Open **IIS Monitor** from the Start menu. If the toolbar shows **Enable response times**, click it once; on the **Requests** tab, click **Set up IIS logging…** to see every request.

**Requirements:** Windows Server 2022 or 2025 with IIS 10 (x64) and administrator rights. The package is self-contained, so there is no .NET runtime to install.

**Updating:** run `install-service.cmd` from the new version's folder. Settings and history are kept.
**Removing:** run `uninstall-service.cmd` as administrator; add `-RemoveData` to also delete history and settings.

## What you get

- **Overview**: CPU, RAM, requests per second and network for every app pool on four live charts.
- **App pools and Sites**: each pool's processes, memory, disk, network, queue and response times; each site's requests, bandwidth, p95 response time and errors.
- **Requests**: every request with its client IP, kind, status and timing, and the IPs sending the most.
- **Auto blacklist**: rules that block scanners and abusive IPs in Windows Firewall or IIS.
- **Database**: SQL Server load, blocking and slow queries per app pool. Nothing is installed on SQL Server.
- **History**: 7 days at 10-second resolution, recorded by a Windows service even when nobody is watching.

## Notes

- The executables aren't code-signed, so Windows SmartScreen may warn the first time. Unblocking the zip before extracting avoids most warnings.
- Found a problem or have an idea? [Open an issue](https://github.com/inanbd/IISMonitor/issues).
- IIS Monitor is free and open source under the [MIT License](https://github.com/inanbd/IISMonitor/blob/HEAD/LICENSE).

**SHA-256** of `IISMonitor-win-x64.zip`: `{SHA256}`
