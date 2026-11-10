using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

internal static class SystemSessionProbe
{
    private sealed record Job(string ServiceName, string AgentName, string AgentSid, string? OwnerSid, string Output,
        bool Visual = false, string? Workspace = null);
    private sealed record ProcessObservation(int Pid, string Name);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly ServiceMain MainCallback = ServiceEntry;
    private static readonly ServiceHandler HandlerCallback = HandleControl;
    private static string? serviceJobPath;
    private static IntPtr statusHandle;
    private static bool stopRequested;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMain(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceHandler(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry { public string? Name; public IntPtr Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public ServiceStatus Status;
        public uint ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes;
        public IntPtr ReservedData, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string displayName,
        uint access, uint type, uint start, uint errorControl, string binary, string? group, IntPtr tag,
        string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, uint level, out ServiceStatusProcess status, uint bytes, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, ServiceHandler handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint value, uint bytes, out uint written);
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    private static Job ReadJob(string path)
    {
        var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Missing service job");
        ValidateName(job.ServiceName);
        if (Path.GetFullPath(job.Output) != Path.GetDirectoryName(Path.GetFullPath(path)))
            throw new InvalidDataException("Service output must be beside its job file");
        var actualSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, job.AgentName).Translate(typeof(SecurityIdentifier));
        if (actualSid.Value != job.AgentSid) throw new InvalidDataException("Provisioned agent SID does not match");
        if (job.Visual && (job.Workspace is null || !Directory.Exists(job.Workspace)))
            throw new InvalidDataException("Visual mode requires the provisioned agent workspace");
        return job;
    }

    private static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^SbxFullProbe_[a-f0-9]{12}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Not an owned experimental service name");
    }

    public static int Controller(string path)
    {
        using var caller = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run the controller unelevated as the MXC provisioning caller");
        var job = ReadJob(path) with { OwnerSid = caller.User?.Value ?? throw new InvalidOperationException("Missing caller SID") };
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(job.OwnerSid), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(job.Output).SetAccessControl(security);
        File.WriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("system-install");
        start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Approved service installer did not start");
        if (!process.WaitForExit(270000))
            throw new TimeoutException("Service installer is still running; inspect its named service and cleanup report, do not retry");
        return process.ExitCode;
    }

    public static int Status(string name)
    {
        ValidateName(name);
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager");
        using var service = OpenServiceW(manager, name, 4);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1060) throw new Win32Exception(error, "OpenService");
            Console.WriteLine(JsonSerializer.Serialize(new { exists = false, name }));
            return 0;
        }
        var status = Query(service);
        Console.WriteLine(JsonSerializer.Serialize(new { exists = true, name, state = status.Status.State, processId = status.ProcessId }));
        return 0;
    }

    public static int Install(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("The explicitly approved service installer must be elevated");
        using var jobLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var job = ReadJob(path);
        using var exeLock = File.OpenRead(Environment.ProcessPath!);
        using var dllLock = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "HeadlessSpike.dll"));
        using var configLock = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "HeadlessSpike.runtimeconfig.json"));
        using var depsLock = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "HeadlessSpike.deps.json"));
        using var manager = OpenSCManagerW(null, null, 3);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager");
        ServiceHandle? service = null;
        var report = new Dictionary<string, object?> { ["at"] = DateTimeOffset.UtcNow, ["service"] = job.ServiceName };
        int result = 1;
        try
        {
            service = CreateServiceW(manager, job.ServiceName, "sandbox-mcp bounded Full-session experiment",
                0x10034, 0x10, 3, 1, $"\"{Environment.ProcessPath}\" system-service \"{Path.GetFullPath(path)}\"",
                null, IntPtr.Zero, null, null, null);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateService");
            report["created"] = true;
            if (!StartServiceW(service, 0, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartService");
            report["started"] = true;
            WaitStopped(service, TimeSpan.FromSeconds(180));
            report["serviceExitCode"] = Query(service).Status.Win32ExitCode;
            result = 0;
        }
        catch (Exception error) { report["error"] = error.Message; }
        finally
        {
            if (service is not null && !service.IsInvalid)
            {
                try
                {
                    if (Query(service).Status.State != 1)
                    {
                        if (!ControlService(service, 1, out _))
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 1062) throw new Win32Exception(error, "Stop owned service");
                        }
                        WaitStopped(service, TimeSpan.FromSeconds(30));
                    }
                    report["stopped"] = true;
                }
                catch (Exception error) { report["stopError"] = error.Message; result = 1; }
                if (!DeleteService(service))
                {
                    report["deleteError"] = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    result = 1;
                }
                else report["deleteRequested"] = true;
            }
            service?.Dispose();
            File.WriteAllText(Path.Combine(job.Output, "service-install.json"), JsonSerializer.Serialize(report, JsonOptions));
        }
        return result;
    }

    private static ServiceStatusProcess Query(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Query owned service");
        return status;
    }

    private static void WaitStopped(ServiceHandle service, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (Query(service).Status.State != 1)
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException("Owned service has not stopped; cleanup is incomplete");
            Thread.Sleep(250);
        }
    }

    public static int Dispatch(string path)
    {
        var job = ReadJob(path);
        serviceJobPath = path;
        var table = new[]
        {
            new ServiceTableEntry { Name = job.ServiceName, Main = Marshal.GetFunctionPointerForDelegate(MainCallback) },
            new ServiceTableEntry()
        };
        if (!StartServiceCtrlDispatcherW(table))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "StartServiceCtrlDispatcher");
        return 0;
    }

    private static void ServiceEntry(uint argc, IntPtr argv)
    {
        Job? job = null;
        int result = 1;
        try
        {
            job = ReadJob(serviceJobPath!);
            statusHandle = RegisterServiceCtrlHandlerExW(job.ServiceName, HandlerCallback, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterServiceCtrlHandler");
            ReportStatus(4, 0);
            result = Probe(job);
        }
        catch (Exception error)
        {
            if (job is not null)
                File.WriteAllText(Path.Combine(job.Output, "service-failure.json"), JsonSerializer.Serialize(new { error = error.Message }, JsonOptions));
        }
        finally
        {
            if (statusHandle != IntPtr.Zero) ReportStatus(1, result == 0 ? 0u : 1066u);
        }
    }

    private static uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control == 1) Volatile.Write(ref stopRequested, true);
        return 0;
    }

    private static void ReportStatus(uint state, uint error)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, ControlsAccepted = state == 4 ? 1u : 0u,
            Win32ExitCode = error, ServiceSpecificExitCode = error == 1066 ? 1u : 0u };
        if (!SetServiceStatus(statusHandle, ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetServiceStatus");
    }

    private static int Probe(Job job)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new InvalidOperationException("Probe is not running as LocalSystem");
        if (job.OwnerSid is null) throw new InvalidDataException("Missing provisioning caller SID");
        var before = Program.Sessions();
        uint console = Program.WTSGetActiveConsoleSessionId();
        int caller = Process.GetCurrentProcess().SessionId;
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow, ["isLocalSystem"] = true, ["kind"] = "full", ["initialProgram"] = "null",
            ["callerSessionId"] = caller, ["consoleSessionId"] = console, ["sessionsBefore"] = before, ["createAttempted"] = false
        };
        string path = Path.Combine(job.Output, "system-probe.json");
        int sessionId = -1;
        bool created = false;
        int result = 1;
        try
        {
            report["phase"] = "credential";
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
            using var credential = LocalAgentCredential.Acquire(job.AgentName, report, new SecurityIdentifier(job.OwnerSid));
            if (Volatile.Read(ref stopRequested)) throw new OperationCanceledException("Owned service stop requested");
            var parameters = new Program.SessionParams
            {
                Size = checked((uint)(Marshal.OffsetOf<Program.SessionParams>(nameof(Program.SessionParams.ParentSessionId)).ToInt32() + 4)),
                Kind = 1, LogonTimeoutMs = 20000, AuthIdentity = credential.Buffer, AuthIdentityBytes = credential.Length,
                InitialProgram = IntPtr.Zero, ParentSessionId = uint.MaxValue - 1
            };
            report["phase"] = "creating";
            report["createAttempted"] = true;
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
            var timer = Stopwatch.StartNew();
            created = Program.EXPERIMENTAL_WTSCreateHeadlessSession(ref parameters, out sessionId);
            int error = Marshal.GetLastWin32Error();
            report["created"] = created;
            report["sessionId"] = sessionId;
            report["createMs"] = timer.ElapsedMilliseconds;
            if (!created) throw new Win32Exception(error, "System-context WTS Full creation");
            if (sessionId < 0 || before.Contains(sessionId) || sessionId == caller || sessionId == console)
                throw new InvalidOperationException("Unexpected session ID; refusing to inspect or stop a pre-existing session");
            report["phase"] = "observing";
            var observations = new List<object>();
            var warnings = new List<string>();
            bool dwmSeen = false;
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromSeconds(20) && !Volatile.Read(ref stopRequested))
            {
                var processes = new List<ProcessObservation>();
                foreach (string name in new[] { "dwm", "winlogon", "csrss", "userinit", "explorer", "rdpinit" })
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.SessionId == sessionId) processes.Add(new(process.Id, name));
                        }
                        catch (Exception observationError) when (observationError is InvalidOperationException or Win32Exception)
                        {
                            warnings.Add($"Process observation unavailable: {observationError.GetType().Name}");
                        }
                    }
                }
                dwmSeen |= processes.Any(process => process.Name == "dwm");
                observations.Add(new { milliseconds = timer.ElapsedMilliseconds, processes });
                if (dwmSeen && timer.Elapsed >= TimeSpan.FromSeconds(3)) break;
                Thread.Sleep(500);
            }
            report["dwmSeen"] = dwmSeen;
            report["observations"] = observations;
            report["observationWarnings"] = warnings;
            if (job.Visual)
            {
                report["phase"] = "visual-worker";
                File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
                RunVisualWorker(job, sessionId, report);
            }
            result = 0;
        }
        catch (Exception error)
        {
            report["error"] = error.Message;
            report["hresult"] = $"0x{error.HResult:X8}";
        }
        finally
        {
            if (created && sessionId >= 0 && !before.Contains(sessionId) && sessionId != caller && sessionId != console)
            {
                bool stopped = Program.EXPERIMENTAL_WTSStopHeadlessSession(sessionId);
                int error = Marshal.GetLastWin32Error();
                report["sessionStop"] = new { stopped, error = stopped ? 0 : error };
                if (!stopped) result = 1;
                else
                {
                    var timer = Stopwatch.StartNew();
                    while (Program.Sessions().Contains(sessionId) && timer.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(250);
                    bool gone = !Program.Sessions().Contains(sessionId);
                    report["ownedSessionGone"] = gone;
                    if (!gone) result = 1;
                }
            }
            report["sessionsAfter"] = Program.Sessions();
            report["phase"] = "finished";
            report["exitCode"] = result;
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        }
        return result;
    }

    private static void RunVisualWorker(Job job, int sessionId, Dictionary<string, object?> report)
    {
        string workspace = Path.GetFullPath(job.Workspace ?? throw new InvalidDataException("Missing visual workspace"));
        string executable = Path.Combine(workspace, "headless", "HeadlessSpike.exe");
        string output = Path.Combine(workspace, "visual");
        string app = Path.Combine(workspace, "app", "HelloWinUI.exe");
        string helper = Path.Combine(workspace, "helper", "CaptureHelper.exe");
        if (!File.Exists(executable) || !File.Exists(app) || !File.Exists(helper))
            throw new FileNotFoundException("The fixed visual-test payload was not staged");
        Directory.CreateDirectory(output);
        string workerJob = Path.Combine(workspace, "visual-job.json");
        if (File.Exists(workerJob)) throw new IOException("Visual worker job already exists");
        File.WriteAllText(workerJob, JsonSerializer.Serialize(new Program.Job(output, app, helper, sessionId), JsonOptions));
        using var jobLock = File.OpenRead(workerJob);
        if (!WTSQueryUserToken(checked((uint)sessionId), out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken for owned session");
        using (token)
        {
            using var user = new WindowsIdentity(token.DangerousGetHandle());
            if (user.User?.Value != job.AgentSid) throw new InvalidOperationException("Owned session token is not the provisioned agent");
            if (!GetTokenInformation(token, 12, out uint tokenSession, sizeof(uint), out uint written))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read owned token session");
            if (written != sizeof(uint) || tokenSession != sessionId)
                throw new InvalidOperationException("Owned user token belongs to a different session");
            report["workerTokenVerified"] = true;
            report["workerTokenSessionId"] = tokenSession;
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, token, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Create agent environment");
                var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                var command = new StringBuilder($"\"{executable}\" worker \"{workerJob}\"");
                if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000400, environment, Path.GetDirectoryName(executable)!, ref startup, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Launch the fixed worker as the agent");
                using var process = new SafeProcessHandle(information.Process, true);
                using var thread = new SafeWaitHandle(information.Thread, true);
                report["workerPid"] = information.ProcessId;
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    uint wait = WaitForSingleObject(process, 250);
                    if (wait == 0) break;
                    if (wait == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for owned visual worker");
                    if (wait != 258) throw new InvalidOperationException($"Unexpected worker wait status {wait}");
                    if (Volatile.Read(ref stopRequested)) throw new OperationCanceledException("Owned service stop requested");
                    if (timer.Elapsed > TimeSpan.FromSeconds(100))
                        throw new TimeoutException("Owned visual worker exceeded 100 seconds; session teardown will stop it");
                }
                if (!GetExitCodeProcess(process, out uint exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Get owned worker exit code");
                report["workerExitCode"] = exitCode;
                string result = Path.Combine(output, "worker.json");
                if (!File.Exists(result)) throw new IOException("Owned visual worker did not write its report");
                using var worker = JsonDocument.Parse(File.ReadAllText(result));
                report["worker"] = worker.RootElement.Clone();
                if (worker.RootElement.GetProperty("sessionId").GetInt32() != sessionId
                    || !string.Equals(worker.RootElement.GetProperty("user").GetString(), job.AgentName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Visual worker report has the wrong user or session");
                if (exitCode != 0 || worker.RootElement.TryGetProperty("error", out _)
                    || worker.RootElement.TryGetProperty("appCleanupError", out _))
                    throw new InvalidOperationException("Visual worker failed; inspect its report");
            }
            finally
            {
                if (environment != IntPtr.Zero && !DestroyEnvironmentBlock(environment))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Destroy agent environment");
            }
        }
    }
}
