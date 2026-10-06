using System.ComponentModel;
using System.Runtime.InteropServices;
using IISMonitor.Collectors.Native;
using IISMonitor.Core.Collection;
using Microsoft.Win32.SafeHandles;

namespace IISMonitor.Collectors.Processes;

/// <summary>Lists every process with its parent and thread count (one ToolHelp snapshot).</summary>
internal static class ProcessTableReader
{
    public static List<ProcessEntry> Read()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");

        var result = new List<ProcessEntry>(256);
        var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
        if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            return result;

        do
        {
            result.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? "", (int)entry.cntThreads));
        }
        while (NativeMethods.Process32NextW(snapshot, ref entry));

        return result;
    }
}

/// <summary>
/// Reads cumulative CPU time, memory, handle and I/O counters per process. Handles are kept open
/// between ticks so a PID that gets reused by a new process is noticed (its creation time changes).
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private const uint StillActive = 259;
    private readonly Dictionary<int, SafeProcessHandle> _handles = [];

    public Dictionary<int, ProcessSample> Sample(IEnumerable<int> pids)
    {
        var wanted = new HashSet<int>(pids);
        foreach (var stale in _handles.Keys.Where(pid => !wanted.Contains(pid)).ToList())
        {
            _handles[stale].Dispose();
            _handles.Remove(stale);
        }

        var result = new Dictionary<int, ProcessSample>();
        foreach (var pid in wanted)
        {
            if (TrySample(pid, out var sample))
                result[pid] = sample;
        }

        return result;
    }

    private bool TrySample(int pid, out ProcessSample sample)
    {
        sample = default;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var handle = GetHandle(pid);
            if (handle is null)
                return false;

            // A cached handle to an exited process means the PID now belongs to someone else.
            if (NativeMethods.GetExitCodeProcess(handle, out var exitCode) && exitCode != StillActive)
            {
                handle.Dispose();
                _handles.Remove(pid);
                continue;
            }

            if (!NativeMethods.GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
            {
                handle.Dispose();
                _handles.Remove(pid);
                continue;
            }

            NativeMethods.GetProcessMemoryInfo(handle, out var memory, (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX>());
            NativeMethods.GetProcessHandleCount(handle, out var handleCount);
            NativeMethods.GetProcessIoCounters(handle, out var io);

            sample = new ProcessSample(
                pid,
                created > 0 ? DateTime.FromFileTimeUtc(created).Ticks : 0,
                kernel + user,
                (long)memory.WorkingSetSize,
                (long)memory.PrivateUsage,
                (int)handleCount,
                (long)io.ReadTransferCount,
                (long)io.WriteTransferCount);
            return true;
        }

        return false;
    }

    private SafeProcessHandle? GetHandle(int pid)
    {
        if (_handles.TryGetValue(pid, out var cached))
            return cached;

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        _handles[pid] = handle;
        return handle;
    }

    public void Dispose()
    {
        foreach (var handle in _handles.Values)
            handle.Dispose();
        _handles.Clear();
    }
}

/// <summary>Machine-wide CPU times and physical memory.</summary>
internal static class SystemSampler
{
    public static SystemSample Read()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed");

        var memory = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalMemoryStatusEx failed");

        return new SystemSample(idle, kernel, user, (long)memory.ullTotalPhys, (long)memory.ullAvailPhys);
    }
}
