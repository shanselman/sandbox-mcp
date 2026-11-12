using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class OwnedAppDump
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(SafeProcessHandle process, uint pid, SafeFileHandle file,
        uint flags, IntPtr exception, IntPtr userStream, IntPtr callback);
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    internal static void RequireTarget(string? actualSid, string? callerSid, int targetSession, int helperSession,
        uint consoleSession, string actualImage, string expectedImage)
    {
        if (callerSid is null || callerSid == "S-1-5-18" || actualSid != callerSid
            || helperSession <= 1 || helperSession == consoleSession || targetSession != helperSession
            || !string.Equals(actualImage, expectedImage, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Dump target must be the fixed sample app in this owned non-console agent session");
    }

    public static void Capture(int pid, string output, Dictionary<string, object?> report)
    {
        using var target = Process.GetProcessById(pid);
        using var me = Process.GetCurrentProcess();
        using var caller = WindowsIdentity.GetCurrent();
        if (!OpenProcessToken(target.SafeHandle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            string release = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            RequireTarget(identity.User?.Value, caller.User?.Value, target.SessionId, me.SessionId, WTSGetActiveConsoleSessionId(),
                target.MainModule?.FileName ?? "", Path.Combine(release, "fixture", "HelloWinUI.exe"));
        }
        report["target"] = new { pid, sessionId = target.SessionId, startedAt = target.StartTime.ToUniversalTime(),
            cpuMs = target.TotalProcessorTime.TotalMilliseconds, workingSetBytes = target.WorkingSet64,
            privateBytes = target.PrivateMemorySize64, threads = target.Threads.Count,
            hwnd = $"0x{target.MainWindowHandle:X}", windowTitle = target.MainWindowTitle };
        string file = Path.Combine(output, "startup-hang.dmp");
        var timer = Stopwatch.StartNew();
        // Full memory plus thread info/unloaded modules supports native and managed stack diagnosis.
        EvidencePublication.PublishNew(file, stream =>
        {
            if (stream is not FileStream targetFile) throw new InvalidOperationException("Expected a file-backed dump stream");
            if (!MiniDumpWriteDump(target.SafeHandle, checked((uint)pid), targetFile.SafeFileHandle,
                0x1022, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture owned app minidump");
        });
        report["dump"] = new { file = "startup-hang.dmp", elapsedMs = timer.ElapsedMilliseconds,
            bytes = new FileInfo(file).Length, localOnly = true };
    }
}
