# IIS Monitor

A Windows app that watches every IIS website and application pool on a server in real time:
CPU, memory, disk, network, processes, SQL Server connections and response times. It also
keeps up to 7 days of history.

- **Live view**: opens straight onto live grids and charts. You choose the update frequency
  (0.25 s to 1 min) from the toolbar.
- **History**: one averaged row per app pool, site and the server every 10 seconds (configurable),
  kept for 7 days (configurable), browsable by hour, day or week.
- **Always collecting**: a small Windows service collects data continuously, so history has no
  gaps when nobody has the dashboard open.

Target platforms: **Windows Server 2022 and 2025** (IIS 10), x64.

## What is measured, and how

| Metric | Per | Source |
|---|---|---|
| CPU %, private memory, working set, threads, handles | app pool and each of its processes | Win32 process APIs on each `w3wp.exe` and every process it started |
| Process count | app pool | `w3wp.exe` per pool (several for web gardens) plus child processes (out-of-process ASP.NET Core, PHP FastCGI, iisnode, …) |
| Disk read/write bytes/sec | app pool, process | Kernel ETW file I/O events, the same data Resource Monitor uses. If ETW is off, process I/O counters are used instead |
| Network sent/received bytes/sec | app pool | The HTTP traffic of the sites the pool serves (IIS `Web Service` counters, credited to the pool of each site's root application) **plus** the pool's own outbound traffic |
| Outbound sent/received bytes/sec | app pool, process | Kernel ETW TCP/UDP events: traffic the pool's processes create themselves (SQL Server, external APIs) |
| Bandwidth (HTTP) bytes/sec | site | IIS `Web Service` performance counters |
| Requests/sec, current connections | site | IIS `Web Service` performance counters |
| Requests/sec, active requests | app pool | IIS `W3SVC_W3WP` performance counters |
| Request queue length | app pool | `HTTP Service Request Queues` performance counters |
| SQL Server connections | app pool, process | Established TCP connections from the pool's processes to SQL Server ports (1433, plus any port a local `sqlservr.exe` listens on, plus ports you add) |
| SQL Server sessions (active / idle) | app pool, process | *Optional.* `sys.dm_exec_sessions` on the SQL Servers you configure (`host_process_id` is the client PID) |
| Database load, blocking, slow queries | app pool | *Optional.* Every second, the running requests in `sys.dm_exec_requests`, matched to app pools by client PID (see [Database tab](#database-tab)) |
| SQL Server CPU and memory | server | The local `sqlservr.exe` process |
| Response time avg / p95 / max, 4xx and 5xx/sec | site and app pool | IIS sends each finished request to ETW (the `Microsoft-Windows-IIS-Logging` provider), including `time-taken` |
| Server CPU and memory | server | `GetSystemTimes`, `GlobalMemoryStatusEx` |

### Things worth knowing

1. **CPU, memory, disk and DB connections belong to an app pool, not a site.** IIS runs each
   pool in its own processes. If several sites share one pool, those numbers can't be split
   between them. Requests, bandwidth and response time are still per site. One pool per site
   gives the clearest picture.
2. **Incoming HTTP traffic doesn't go through `w3wp.exe`.** The kernel driver HTTP.sys owns
   ports 80/443, so Windows charges that traffic to the System process. That's why website
   traffic comes from IIS counters. A pool's network total adds its sites' website traffic
   (credited to the pool running each site's root application) to the outbound traffic its
   processes create; the per-process numbers are outbound only. Loopback traffic between a
   pool's own processes (for example w3wp.exe relaying requests to an out-of-process ASP.NET
   Core app) is not counted again.
3. **Live response times need IIS's ETW log target.** Click **Enable response times** in the
   toolbar once. It sets the W3C log target to `File, ETW` (log files are still written as
   before) and switches on the log fields `s-sitename`, `cs-uri-stem`, `sc-status` and
   `time-taken` if they are off. This changes `applicationHost.config`; the button asks first.
   The IIS **HTTP Logging** role service (`Web-Http-Logging`) must be installed. Sites that
   use central binary logging can't be traced.
4. **SQL Server connection counting over TCP** sees network connections. If an app talks to a
   SQL Server on the same machine over shared memory or named pipes, add a connection string
   in *Settings*: SQL Server then reports the sessions itself, active versus idle included,
   and the Database tab lights up.
5. **CPU %** is a share of the whole machine (all cores), like Task Manager's Processes tab.
6. Pools that are idle (no worker process yet) show 0 processes, not an error. Process IDs
   change on every recycle; the app re-maps them on every update.

## Architecture

```
┌──────────────────────────── IIS server ─────────────────────────────┐
│                                                                     │
│  IISMonitor.Service (Windows service, LocalSystem)                  │
│   └─ MonitorEngine ── every N ms ──┬─ IIS config (Microsoft.Web.Administration)
│        │                           ├─ Process table + Win32 process counters
│        │                           ├─ IIS performance counters
│        │                           ├─ Kernel ETW session (file + TCP/UDP bytes)
│        │                           ├─ IIS log ETW session (response times)
│        │                           ├─ TCP table (SQL Server connections)
│        │                           └─ SQL Server DMVs (optional)
│        ├─ history → %ProgramData%\IISMonitor\history.db (SQLite)
│        └─ live snapshots ──► named pipe "IISMonitor.v1" (Administrators only)
│                                        │
│  IISMonitor.exe (WPF dashboard) ◄──────┘
│   live grids & charts · history browser · settings                  │
│   If the service isn't running, it runs the same engine in-process  │
│   ("standalone mode") so the live view still works.                 │
└─────────────────────────────────────────────────────────────────────┘
```

| Project | Target | Purpose |
|---|---|---|
| `src/IISMonitor.Core` | `net10.0` | Models, snapshot composition, response-time statistics, metric catalog, SQLite history, settings, pipe protocol. Platform-neutral and unit-tested. |
| `src/IISMonitor.Collectors` | `net10.0-windows` | Windows data sources and the `MonitorEngine` that drives them. |
| `src/IISMonitor.Service` | `net10.0-windows` | The Windows service host. |
| `src/IISMonitor.Dashboard` | `net10.0-windows10.0.19041.0` | The WPF dashboard (`IISMonitor.exe`), charts by ScottPlot. |
| `tests/*` | | xUnit tests. |

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build
dotnet test
```

To produce something you can copy to a server (self-contained, so the server needs no .NET runtime):

```bat
scripts\publish.cmd            :: → publish\Service, publish\Dashboard, install scripts
```

The `.cmd` files run the matching `.ps1` with `-ExecutionPolicy Bypass` for that one run, so
they work even where Windows blocks PowerShell scripts ("running scripts is disabled on this
system") without changing the machine's policy. Equivalent by hand:
`powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1`.

## Install on an IIS server

1. Copy the `publish` folder to the server.
2. Right-click `install-service.cmd` and choose **Run as administrator**
   (or run `install-service.cmd` from an elevated command prompt).
   This copies the app to `C:\Program Files\IISMonitor`, registers and starts the `IISMonitor`
   service (automatic start, restarts on failure) and adds an **IIS Monitor** Start menu shortcut.
3. Open **IIS Monitor** from the Start menu (it asks for administrator rights).
4. If the toolbar shows **Enable response times**, click it once to turn on live response times.

To update, publish again and re-run `install-service.cmd`; it stops the service, replaces the
files and starts it again. To remove it: `uninstall-service.cmd` as administrator (add
`-RemoveData` to also delete history and settings).

You can also run `IISMonitor.exe` without installing the service. It then collects data itself
(a banner says so) and records history only while it is open.

## Using the dashboard

- **Overview** (opens first): four live charts comparing app pools, one line per pool:
  **CPU**, **RAM** (switch between working set, the RAM in use, and private bytes, what IIS's
  private-memory recycling limit watches), **requests/sec** and **network** (website traffic
  plus outbound traffic). Each chart title shows the current total of the ticked pools. Tick
  or untick pools in the list on the left, which is also the legend. Every pool starts ticked,
  including pools created later; after eight pools the colors repeat with dashed, then dotted,
  lines. Ticks, colors and the RAM choice are remembered (per Windows user, in
  `%LocalAppData%\IISMonitor\dashboard.json`). Hover any chart to read every line's value at
  that moment.
- **App pools**: one row per pool. Select a pool to see its processes and live charts for CPU,
  memory, disk, network, database connections, requests, response time, queue, errors and
  process count.
- **Sites**: connections, requests/sec, bandwidth, response time (avg / p95 / max) and 4xx/5xx
  per site, with live charts.
- **Database**: which app pools keep SQL Server busy, what they are running right now and
  their slow queries (see below).
- **Server**: whole-machine CPU and memory, plus the local SQL Server process.
- **History**: pick an app pool, site or the server and a range (last hour to last 7 days).
  Longer ranges are merged into wider points (for example 7 days → about 7-minute points).
  Averages stay averages; maximums and p95 show the worst value in each point.
- **Collectors**: each data source and whether it is working. If a column or chart is empty,
  the reason is here.
- **Update every**: how often data is collected and the screen refreshes. This is a service
  setting, so it also sets the resolution of the live charts.
- **Settings…**: history resolution and retention, SQL Server ports and optional session
  queries, and switches for the two ETW traces.

## Database tab

Shows which app pool is loading SQL Server, and with which queries. It needs one connection
string per SQL Server under *Settings*, with a login that has `VIEW SERVER STATE`; nothing is
installed on the SQL Server and no trace is started. For a SQL Server on the IIS server, with the
service running as LocalSystem:

```sql
IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
GRANT VIEW SERVER STATE TO [NT AUTHORITY\SYSTEM];
```

and the connection string `Server=.;Integrated Security=true;TrustServerCertificate=true`.

Every second (*Settings*, 1–10 s) the service asks SQL Server which queries are running and
which client process sent them (`host_process_id`), and maps that process to its app pool:

- **DB load**: the average number of the pool's queries running at once. 1.00 means one query
  busy all the time; it is split into *on CPU* and *waiting*. Sampling every second makes this
  accurate over a minute or so even for queries of a few milliseconds. SQL Server's own
  per-session totals can't be used for this: they only update when a query finishes and reset
  every time a pooled connection is reused, which is constantly in a web app.
- **Blocked now / Blocking others**: the pool's queries waiting for another session's locks,
  and other sessions waiting for this pool's locks. **Idle in transaction** counts sessions that
  hold a transaction open while doing nothing, a common cause of blocking.
- **Running now**: every query from this server running at that moment, longest first, with
  what it is doing (on CPU, waiting for a lock, reading from disk, waiting for the app to read
  results…) and who blocks it.
- **Slow queries**: every query that runs longer than the threshold (**2 seconds** by default,
  *Settings*) is recorded when it finishes, with its app pool, database, duration, CPU, reads,
  main wait and statement, kept with the rest of the history. The list groups runs of the same
  stored procedure, or of the same batch (runs that differ only in literal values group
  together), and sorts by total time, so the costliest come first. For each it shows the
  statement the longest run spent most of its time in. Durations come from the samples, so a
  query ran at least as long as shown, and at most one sample interval longer.
- Statements are stored and shown with literal values replaced by `?`
  (`WHERE Email = 'x@y.com'` → `WHERE Email = ?`), so customer data isn't copied into the history.
- Charts show database load and blocked queries per app pool for the pools ticked on the
  Overview tab. The App pools grid also has a **DB load** column, and pool history keeps DB
  load, blocking and slow-query counts.
- Waits by design are ignored: Service Broker and `SqlDependency` listeners, `WAITFOR` and
  trace readers would otherwise look like endless slow queries.
- A query is matched to this server only if its client reports this computer's name as host name
  (the default); connection strings that set `Workstation ID` show up as "other servers".

## Data and security

- Settings: `%ProgramData%\IISMonitor\settings.json`. History: `%ProgramData%\IISMonitor\history.db`.
  When the app creates that folder, it restricts access to Administrators and SYSTEM, because the
  settings can contain SQL Server connection strings. Prefer `Integrated Security=true`: the service
  runs as LocalSystem and signs in to a local SQL Server as `NT AUTHORITY\SYSTEM` and to a remote
  one as the computer account (`DOMAIN\SERVER$`). That login needs `VIEW SERVER STATE`.
- Slow-query statements are kept without literal values, in the same history database.
- The dashboard talks to the service over a local named pipe that only Administrators and
  SYSTEM can open. Nothing listens on the network.
- History size: about 60,000 rows per app pool or site per week at 10-second resolution.
  Measured: 10 pools + 10 sites with every metric populated take about 260 MB for 7 days, and
  a 7-day chart query takes about 0.15 s. A 30-second resolution cuts the size to a third.
  Old rows are deleted hourly.
- Service errors go to the Windows **Application** event log, source **IISMonitor**.

## Troubleshooting

| Symptom | Check |
|---|---|
| Response time shows "not traced" | Click **Enable response times**; make sure the *HTTP Logging* role service is installed; the Collectors tab shows how many sites are traced. |
| Pool network looks low, or process network shows "—" | Kernel tracing is off or failed (see Collectors), so outbound traffic is missing and pool network is website traffic only. Windows allows up to 8 such system trace sessions at once; other monitoring tools may be using them. |
| DB connections are 0 but the app uses SQL Server | The SQL Server may listen on a non-default port (add it in Settings), or the app may use shared memory/named pipes to a local SQL Server (add a session connection string). |
| Dashboard says "Standalone mode" | The service isn't running. Use **Start the service**, or check the Application event log. |
| Empty performance counter columns | Rebuild counters with `lodctr /R` from an elevated prompt, then restart the service. |

## Status

The platform-neutral logic (aggregation, statistics, history, protocol) is covered by unit
tests that run on any OS. The Windows collectors and the dashboard build cleanly but still
need a first run on a real Windows Server 2022/2025 IIS machine.
