using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SandboxMcp.FullBroker;

internal static class Native
{
    internal enum Ownership { Other, Owned, Unknown }
    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { public uint Type, State, Accepted, Error, SpecificError, Checkpoint, WaitHint, Pid, Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceEntry { public string? Name; public IntPtr Main; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ServiceMain(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint ServiceHandler(uint control, uint eventType, IntPtr data, IntPtr context);
    [StructLayout(LayoutKind.Sequential)]
    internal struct HeadlessParameters
    {
        public uint Size, Kind, Timeout, IdentityBytes;
        public IntPtr Identity, InitialProgram;
        public uint Parent;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int Id; public IntPtr Name; public int State; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes;
        public IntPtr ReservedData, In, Out, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string display, uint access,
        uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error,
        string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(ServiceHandle service, uint level, out Status status, uint bytes, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(ServiceHandle service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcherW([In] ServiceEntry[] entries);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr RegisterServiceCtrlHandlerExW(string name, ServiceHandler handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint bytes);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint mode, uint instances,
        uint outputBytes, uint inputBytes, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EXPERIMENTAL_WTSCreateHeadlessSession(ref HeadlessParameters parameters, out int sessionId);
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EXPERIMENTAL_WTSStopHeadlessSession(int sessionId);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int information, out IntPtr data, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr data);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out uint value, uint bytes, out uint written);
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string app, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    internal static bool IsSystem
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true; }
    }
    internal static Status Query(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<Status>(), out _)) throw Error("Query service");
        return status;
    }
    internal static NamedPipeServerStream Pipe(string ownerSid, bool authorizationTest = false)
        => PrivatePipe(Paths.PipeName + (authorizationTest ? ".authorization-test" : ""), ownerSid);

    private static NamedPipeServerStream PrivatePipe(string name, string peerSid)
    {
        var sid = new SecurityIdentifier(peerSid);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;SY)(A;;GRGW;;;{sid.Value})", 1, out var descriptor, out _))
            throw Error("Create pipe security");
        try
        {
            var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // First instance + overlapped; byte pipe, local clients only.
            var handle = CreateNamedPipeW($@"\\.\pipe\{name}", 0x40080003, 8, 1, 65536, 16384, 5000, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw Error("Create exclusive local pipe"); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    internal static Dictionary<int, int> Sessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out int count)) throw Error("Enumerate sessions");
        try
        {
            var result = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(buffer + i * Marshal.SizeOf<SessionInfo>());
                result.Add(session.Id, session.State);
            }
            return result;
        }
        finally { WTSFreeMemory(buffer); }
    }
    internal static bool IsOwnedSession(int sessionId, string agentName, string agentSid)
        => ClassifySession(sessionId, agentName, agentSid) == Ownership.Owned;

    internal static Ownership ClassifySession(int sessionId, string agentName, string agentSid)
    {
        if (sessionId == 0 || sessionId == WTSGetActiveConsoleSessionId()) return Ownership.Other;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, 5, out var name, out _)) throw Error("Read candidate session name");
        string? actualName;
        try { actualName = Marshal.PtrToStringUni(name); }
        finally { WTSFreeMemory(name); }
        if (string.IsNullOrEmpty(actualName)) return Ownership.Unknown;
        if (!string.Equals(actualName, agentName, StringComparison.OrdinalIgnoreCase)) return Ownership.Other;
        if (!WTSQueryUserToken(checked((uint)sessionId), out var token)) throw Error("Query candidate agent token");
        using (token)
        using (var user = new WindowsIdentity(token.DangerousGetHandle()))
            return user.User?.Value == agentSid ? Ownership.Owned : Ownership.Other;
    }
    private static SafeAccessTokenHandle OwnedToken(int sessionId, string sid)
    {
        if (!WTSQueryUserToken(checked((uint)sessionId), out var token)) throw Error("Query owned session token");
        try
        {
            using var user = new WindowsIdentity(token.DangerousGetHandle());
            if (user.User?.Value != sid) throw new UnauthorizedAccessException("Session token ownership mismatch");
            if (!GetTokenInformation(token, 12, out uint actualSession, 4, out uint written)) throw Error("Read token session");
            if (written != 4 || actualSession != sessionId) throw new UnauthorizedAccessException("Token session mismatch");
            return token;
        }
        catch { token.Dispose(); throw; }
    }
    internal static uint RunWorker(int sessionId, string sid, string key, string? variant,
        Action ready, ManualResetEventSlim captureStart, CancellationToken cancel)
    {
        Protocol.RequireId(key);
        using var token = OwnedToken(sessionId, sid);
        using var barrier = PrivatePipe("SandboxMcpFullBroker.Worker." + key, sid);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        startup.CancelAfter(CaptureRunLimits.StartupMs);
        var connected = barrier.WaitForConnectionAsync(startup.Token);
        string executable = Path.Combine(Paths.ReleaseRoot, "worker", "HeadlessSpike.exe");
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw Error("Create agent environment");
        try
        {
            var startInfo = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Desktop = @"winsta0\default" };
            if (!VisualWorkloads.IsAllowed(variant)) throw new ArgumentException("Unsupported variant");
            var command = new StringBuilder($"\"{executable}\" installed-worker {sessionId} {key}" + (variant is null ? "" : $" {variant}"));
            if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x08000400, environment, Path.GetDirectoryName(executable)!, ref startInfo, out var info)) throw Error("Launch fixed agent worker");
            using var process = new SafeProcessHandle(info.Process, true);
            using var thread = new SafeWaitHandle(info.Thread, true);
            while (!connected.IsCompleted)
            {
                startup.Token.ThrowIfCancellationRequested();
                uint wait = WaitForSingleObject(process, 250);
                if (wait == 0) throw new IOException("Worker exited during staged initialization");
                if (wait == uint.MaxValue) throw Error("Wait for worker readiness");
            }
            connected.GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(barrier.SafePipeHandle, out uint peer) || peer != info.Pid)
                throw new UnauthorizedAccessException("Unexpected worker readiness peer");
            byte[] signal = new byte[1];
            barrier.ReadExactlyAsync(signal, startup.Token).AsTask().GetAwaiter().GetResult();
            string? actualSid = null;
            barrier.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); actualSid = identity?.User?.Value; });
            if (signal[0] != (byte)'R' || actualSid != sid)
                throw new UnauthorizedAccessException("Invalid worker readiness identity or message");
            ready();
            captureStart.Wait(cancel);
            barrier.Write(new byte[] { (byte)'G' });
            barrier.Flush();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                uint wait = WaitForSingleObject(process, 250);
                if (wait == 0) break;
                if (wait == uint.MaxValue) throw Error("Wait for agent worker");
                if (wait != 258) throw new IOException("Unexpected worker wait result");
            }
            if (!GetExitCodeProcess(process, out uint code)) throw Error("Read worker exit code");
            return code;
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new TimeoutException("Worker staged initialization/readiness deadline expired");
        }
        finally
        {
            if (!DestroyEnvironmentBlock(environment)) throw Error("Destroy agent environment");
        }
    }
}
