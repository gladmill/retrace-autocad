using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AcadStatus.Core;

namespace AcadStatus.AutoCad;

/// <summary>Looks at the process a status file names: does a process with that id run, and is it the one that wrote the file (its start time
/// against <c>pidStart</c>, <see cref="ProcessIdentity"/>). Called through a <see cref="LivenessCache"/>.
/// <para>It opens the process with PROCESS_QUERY_LIMITED_INFORMATION, which Windows grants even for most processes that
/// <c>Process.StartTime</c> cannot open (SYSTEM services, other sessions), and reads the creation and exit times from the handle.
/// If even that is denied the process exists but cannot be identified: <see cref="ProcessState.Unknown"/>, which
/// <see cref="ProcessIdentity.Resolve"/> treats as alive, except for a file that states <c>pidStart</c> and has been stale for over an
/// hour (a writer silent that long is not the protected process that now holds its pid).</para></summary>
static class ProcessProbe
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ERROR_INVALID_PARAMETER = 87;   // what OpenProcess says for a pid that is not a process
    const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; public long Ticks => ((long)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out FILETIME create, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public static ProcessState Probe(int pid, DateTime? pidStart)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER ? ProcessState.Gone : ProcessState.Unknown;
        try
        {
            if (GetExitCodeProcess(h, out var code) && code != STILL_ACTIVE) return ProcessState.Gone;   // exited, a handle somewhere keeps it listed
            if (!GetProcessTimes(h, out var created, out _, out _, out _)) return ProcessState.Unknown;
            var start = DateTime.FromFileTimeUtc(created.Ticks);
            return ProcessIdentity.StateOf(true, start, pidStart);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>When this process started (UTC), or null.</summary>
    public static DateTime? OwnStart()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return null; }
    }
}
