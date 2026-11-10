using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using System.IO.Pipes;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionParams
    {
        public uint Size;
        public uint Kind;
        public uint LogonTimeoutMs;
        public uint AuthIdentityBytes;
        public IntPtr AuthIdentity;
        public IntPtr InitialProgram;
        public uint ParentSessionId;
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EXPERIMENTAL_WTSCreateHeadlessSession(ref SessionParams parameters, out int sessionId);

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EXPERIMENTAL_WTSStopHeadlessSession(int sessionId);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiValidateAuthIdentity(IntPtr identity);

    [StructLayout(LayoutKind.Sequential)]
    internal struct AuthIdentityEx2
    {
        public uint Version;
        public ushort HeaderLength;
        public uint StructureLength;
        public uint UserOffset;
        public ushort UserLength;
        public uint DomainOffset;
        public ushort DomainLength;
        public uint CredentialsOffset;
        public ushort CredentialsLength;
        public uint Flags;
        public uint PackageListOffset;
        public ushort PackageListLength;
    }

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    private static extern int WHvGetCapability(uint code, out int value, uint size, out uint written);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public int Id;
        public IntPtr Name;
        public int State;
    }

    internal sealed record Job(string Output, string App, string Helper, int? ExpectedSessionId = null, string? Variant = null);
    private sealed record HelperResult(int ExitCode, string Stdout, string Stderr);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "metadata") return Metadata();
            if (args.Length == 1 && args[0] == "credential-selftest") return CredentialPacking.SelfTest();
            if (args.Length == 4 && args[0] == "controlled-account")
                return ControlledAccount.Run(args[1], args[2], args[3]).GetAwaiter().GetResult();
            if (args.Length == 4 && args[0] == "account-lease")
                return ControlledAccount.Lease(args[1], uint.Parse(args[2]), args[3]).GetAwaiter().GetResult();
            if (args.Length == 4 && args[0] == "initial-program-control")
                return ControlledAccount.InitialProgramControl(args[1], args[2], args[3]);
            if (args.Length == 2 && args[0] == "system-control") return SystemSessionProbe.Controller(args[1]);
            if (args.Length == 2 && args[0] == "system-install") return SystemSessionProbe.Install(args[1]);
            if (args.Length == 2 && args[0] == "system-service") return SystemSessionProbe.Dispatch(args[1]);
            if (args.Length == 2 && args[0] == "system-status") return SystemSessionProbe.Status(args[1]);
            if (args.Length == 3 && args[0] == "installed-worker")
                return InstalledWorker(int.Parse(args[1]), args[2]);
            if (args.Length == 4 && args[0] == "installed-worker")
                return InstalledWorker(int.Parse(args[1]), args[2], args[3]);
            if (args.Length == 1 && args[0] == "platform")
            {
                int status = WHvGetCapability(0, out int present, sizeof(int), out uint written);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    operation = "WHvGetCapability(HypervisorPresent)", hresult = $"0x{status:X8}",
                    bytesWritten = written, hypervisorPresent = status >= 0 && written == sizeof(int) ? present != 0 : (bool?)null
                }, JsonOptions));
                return status >= 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "worker") return Worker(args[1]);
            if (args.Length is >= 6 and <= 10 && args[0] == "run") return Run(args);
            if (args.Length != 1 || args[0] != "probe")
                throw new ArgumentException("Usage: HeadlessSpike probe | metadata | platform | credential-selftest | controlled-account <new-evidence-dir> <app-dir> <helper-dir> | initial-program-control <new-evidence-dir> <app-dir> <helper-dir> | run <full|utility> <caller|console> <output> <app.exe> <CaptureHelper.exe> [no-program] [padded-size] [agent-user=<MXC-provisioned-name>] [local-credential]");
            var library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "wtsapi32.dll"));
            try
            {
                var exports = new[] { "EXPERIMENTAL_WTSCreateHeadlessSession", "EXPERIMENTAL_WTSStopHeadlessSession" }
                    .ToDictionary(name => name, name => NativeLibrary.TryGetExport(library, name, out _));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow,
                    os = Environment.OSVersion.VersionString,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    callerSessionId = Process.GetCurrentProcess().SessionId,
                    // V1_SIZE is FIELD_OFFSET(ParentSessionId) + sizeof(ULONG), NOT sizeof(struct).
                    v1Size = Marshal.OffsetOf<SessionParams>(nameof(SessionParams.ParentSessionId)).ToInt32() + sizeof(uint),
                    allocatedSize = Marshal.SizeOf<SessionParams>(),
                    exports
                }, new JsonSerializerOptions { WriteIndented = true }));
                return exports.Values.All(value => value) ? 0 : 1;
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int InstalledWorker(int expectedSession, string outputKey, string? variant = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != false
            || expectedSession <= 0 || expectedSession == WTSGetActiveConsoleSessionId()
            || Process.GetCurrentProcess().SessionId != expectedSession)
            throw new UnauthorizedAccessException("Installed worker must run only as the owned non-console agent");
        if (!Guid.TryParseExact(outputKey, "N", out _) || outputKey != outputKey.ToLowerInvariant())
            throw new ArgumentException("Invalid output key");
        if (variant is not (null or "light" or "dark" or "dark-large"))
            throw new ArgumentException("Unsupported fixed fixture variant");
        string release = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Shared", "broker-" + outputKey);
        if (Directory.Exists(output)) throw new IOException("Worker output already exists; refusing to overwrite evidence");
        Directory.CreateDirectory(output);
        bool denied = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", "SandboxMcpFullBroker.v1.authorization-test",
                PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(2000);
        }
        catch (UnauthorizedAccessException) { denied = true; }
        catch (IOException error) when ((error.HResult & 0xFFFF) == 5) { denied = true; }
        File.WriteAllText(Path.Combine(output, "authorization.json"),
            JsonSerializer.Serialize(new { sessionId = expectedSession, nonOwnerPipeAccessDenied = denied }, JsonOptions));
        if (!denied) throw new UnauthorizedAccessException("The agent could reach the owner-only authorization-test pipe");
        var job = new Job(output, Path.Combine(release, "fixture", "HelloWinUI.exe"),
            Path.Combine(release, "helper", "CaptureHelper.exe"), expectedSession, variant);
        string path = Path.Combine(output, "job.json");
        File.WriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
        return Worker(path);
    }

    private static int Metadata()
    {
        string file = Path.Combine(Environment.SystemDirectory, "WinMetadata", "Windows.AI.winmd");
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader(MetadataReaderOptions.None);
        var types = metadata.TypeDefinitions.Select(handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return new
            {
                name = metadata.GetString(type.Name),
                ns = metadata.GetString(type.Namespace),
                methods = type.GetMethods().Select(methodHandle =>
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    return new
                    {
                        name = metadata.GetString(method.Name),
                        parameters = method.GetParameters().Select(parameterHandle =>
                        {
                            var parameter = metadata.GetParameter(parameterHandle);
                            return new { name = metadata.GetString(parameter.Name), sequence = parameter.SequenceNumber };
                        }).ToArray()
                    };
                }).ToArray(),
                fields = type.GetFields().Select(fieldHandle => metadata.GetString(metadata.GetFieldDefinition(fieldHandle).Name)).ToArray()
            };
        }).Where(type => type.ns.Contains("IsolationSession", StringComparison.Ordinal)).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { file, types }, JsonOptions));
        return types.Length == 0 ? 1 : 0;
    }

    internal static HashSet<int> Sessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessionsW");
        try
        {
            return Enumerable.Range(0, count)
                .Select(index => Marshal.PtrToStructure<SessionInfo>(buffer + index * Marshal.SizeOf<SessionInfo>()).Id)
                .ToHashSet();
        }
        finally { WTSFreeMemory(buffer); }
    }

    internal static int Run(string[] args, IntPtr suppliedIdentity = default, uint suppliedIdentityBytes = 0,
        string? workerExecutable = null, string? suppliedIdentityDescription = null)
    {
        uint kind = args[1] switch { "full" => 1, "utility" => 0, _ => throw new ArgumentException("Invalid kind") };
        uint parent = args[2] switch { "caller" => uint.MaxValue, "console" => uint.MaxValue - 1, _ => throw new ArgumentException("Invalid parent") };
        bool noProgram = args.Skip(6).Contains("no-program");
        bool paddedSize = args.Skip(6).Contains("padded-size");
        bool useLocalCredential = args.Skip(6).Contains("local-credential");
        string? agentUser = args.Skip(6).FirstOrDefault(flag => flag.StartsWith("agent-user=", StringComparison.Ordinal))?["agent-user=".Length..];
        if (args.Skip(6).Any(flag => flag is not ("no-program" or "padded-size" or "local-credential") && !flag.StartsWith("agent-user=", StringComparison.Ordinal)))
            throw new ArgumentException("Invalid experiment flag");
        if (useLocalCredential && string.IsNullOrWhiteSpace(agentUser))
            throw new ArgumentException("local-credential requires a fresh MXC-provisioned agent user");
        uint parameterSize = checked((uint)(paddedSize ? Marshal.SizeOf<SessionParams>()
            : Marshal.OffsetOf<SessionParams>(nameof(SessionParams.ParentSessionId)).ToInt32() + sizeof(uint)));
        var job = new Job(Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), Path.GetFullPath(args[5]));
        if (!File.Exists(job.App) || !File.Exists(job.Helper)) throw new FileNotFoundException("Build the app and helper first");
        Directory.CreateDirectory(job.Output);
        string jobPath = Path.Combine(job.Output, "job.json");
        if (File.Exists(jobPath)) throw new IOException("Use a new evidence directory for each attempt");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOptions));
        var before = Sessions();
        int caller = Process.GetCurrentProcess().SessionId;
        uint console = WTSGetActiveConsoleSessionId();
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["kind"] = args[1],
            ["parent"] = args[2],
            ["initialProgram"] = noProgram ? "null" : "owned worker executable",
            ["parameterSize"] = parameterSize,
            ["authentication"] = suppliedIdentity != IntPtr.Zero ? suppliedIdentityDescription ?? "temporary standard-account password; SSPI-normalized local EX2; memory-only"
                : useLocalCredential ? "build-pinned local-agent credential; memory-only"
                : agentUser is null ? "null identity; no credentials supplied" : "MXC-provisioned agent name; SSPI identity without supplied credentials",
            ["agentUserName"] = agentUser,
            ["callerSessionId"] = caller,
            ["consoleSessionId"] = console,
            ["sessionsBefore"] = before,
            ["createAttempted"] = false,
            ["os"] = Environment.OSVersion.VersionString
        };
        int sessionId = -1;
        bool created = false;
        IntPtr serializedIdentity = IntPtr.Zero;
        uint serializedIdentityBytes = 0;
        LocalAgentCredential? localCredential = null;
        string reportPath = Path.Combine(job.Output, "host.json");
        var initialProgram = noProgram ? IntPtr.Zero : Marshal.StringToHGlobalUni($"\"{workerExecutable ?? Environment.ProcessPath}\" worker \"{jobPath}\"");
        try
        {
            uint identityBytes = 0;
            if (suppliedIdentity != IntPtr.Zero)
            {
                identityBytes = suppliedIdentityBytes;
                report["authIdentityVersion"] = unchecked((uint)Marshal.ReadInt32(suppliedIdentity));
                report["authIdentityBytes"] = identityBytes;
                int status = SspiValidateAuthIdentity(suppliedIdentity);
                report["authIdentityValidation"] = $"0x{status:X8}";
                if (status != 0) throw new COMException("Supplied EX2 failed SSPI validation", status);
            }
            else if (useLocalCredential)
            {
                report["phase"] = "retrieving-local-agent-credential";
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
                localCredential = LocalAgentCredential.Acquire(agentUser!, report);
                identityBytes = localCredential.Length;
            }
            else if (agentUser is not null)
            {
                if (string.IsNullOrWhiteSpace(agentUser)) throw new ArgumentException("Agent username cannot be empty");
                byte[] userBytes = Encoding.Unicode.GetBytes(agentUser);
                byte[] domainBytes = Encoding.Unicode.GetBytes(Environment.MachineName);
                int headerBytes = Marshal.SizeOf<AuthIdentityEx2>();
                serializedIdentityBytes = checked((uint)(headerBytes + userBytes.Length + domainBytes.Length));
                serializedIdentity = Marshal.AllocHGlobal(checked((int)serializedIdentityBytes));
                Marshal.Copy(new byte[checked((int)serializedIdentityBytes)], 0, serializedIdentity, checked((int)serializedIdentityBytes));
                var auth = new AuthIdentityEx2
                {
                    Version = 0x201, HeaderLength = checked((ushort)headerBytes),
                    StructureLength = serializedIdentityBytes,
                    UserOffset = checked((uint)headerBytes), UserLength = checked((ushort)userBytes.Length),
                    DomainOffset = checked((uint)(headerBytes + userBytes.Length)), DomainLength = checked((ushort)domainBytes.Length),
                    Flags = 2 // SEC_WINNT_AUTH_IDENTITY_UNICODE; no credential payload supplied.
                };
                Marshal.StructureToPtr(auth, serializedIdentity, false);
                Marshal.Copy(userBytes, 0, serializedIdentity + headerBytes, userBytes.Length);
                Marshal.Copy(domainBytes, 0, serializedIdentity + headerBytes + userBytes.Length, domainBytes.Length);
                int status = SspiValidateAuthIdentity(serializedIdentity);
                report["authIdentityValidation"] = $"0x{status:X8}";
                if (status != 0) throw new InvalidOperationException($"SspiValidateAuthIdentity: 0x{status:X8}");
                identityBytes = serializedIdentityBytes;
                report["authIdentityVersion"] = auth.Version;
                report["authIdentityBytes"] = identityBytes;
            }
            var parameters = new SessionParams
            {
                Size = parameterSize,
                Kind = kind,
                LogonTimeoutMs = 20000,
                AuthIdentity = suppliedIdentity != IntPtr.Zero ? suppliedIdentity : localCredential?.Buffer ?? serializedIdentity,
                AuthIdentityBytes = identityBytes,
                InitialProgram = initialProgram,
                ParentSessionId = parent
            };
            report["phase"] = "creating";
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            var timer = Stopwatch.StartNew();
            report["createAttempted"] = true;
            created = EXPERIMENTAL_WTSCreateHeadlessSession(ref parameters, out sessionId);
            int error = Marshal.GetLastWin32Error();
            report["createMs"] = timer.ElapsedMilliseconds;
            report["created"] = created;
            report["sessionId"] = sessionId;
            if (!created)
            {
                report["createError"] = new { code = error, message = new Win32Exception(error).Message };
                return 1;
            }
            if (sessionId < 0 || before.Contains(sessionId) || sessionId == caller || sessionId == console)
                throw new InvalidOperationException($"Unexpected session ID {sessionId}; refusing to touch an existing session");
            Console.Error.WriteLine($"Created owned {args[1]} session {sessionId}; awaiting worker");
            if (noProgram) return 0;
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromSeconds(90))
            {
                string workerPath = Path.Combine(job.Output, "worker.json");
                if (File.Exists(workerPath))
                {
                    using var worker = JsonDocument.Parse(File.ReadAllText(workerPath));
                    if (worker.RootElement.GetProperty("sessionId").GetInt32() != sessionId)
                        throw new InvalidOperationException("Worker executed in the wrong session");
                    if (worker.RootElement.TryGetProperty("error", out var workerError))
                        throw new InvalidOperationException($"Worker failed: {workerError}");
                    return 0;
                }
                Thread.Sleep(500);
            }
            throw new TimeoutException("Initial program did not produce worker.json within 90 seconds");
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
            return 1;
        }
        finally
        {
            Exception? credentialCleanupError = null;
            try { localCredential?.Dispose(); }
            catch (Exception error)
            {
                credentialCleanupError = error;
                report["credentialCleanupError"] = error.Message;
            }
            if (serializedIdentity != IntPtr.Zero)
            {
                Marshal.Copy(new byte[checked((int)serializedIdentityBytes)], 0, serializedIdentity, checked((int)serializedIdentityBytes));
                Marshal.FreeHGlobal(serializedIdentity);
            }
            Marshal.FreeHGlobal(initialProgram);
            int cleanupError = 0;
            if (created && sessionId >= 0 && !before.Contains(sessionId) && sessionId != caller && sessionId != console)
            {
                bool stopped = EXPERIMENTAL_WTSStopHeadlessSession(sessionId);
                int error = Marshal.GetLastWin32Error();
                report["cleanup"] = new { stopped, error = stopped ? null : new { code = error, message = new Win32Exception(error).Message } };
                if (!stopped) cleanupError = error;
                else
                {
                    var teardown = Stopwatch.StartNew();
                    while (Sessions().Contains(sessionId) && teardown.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);
                    bool gone = !Sessions().Contains(sessionId);
                    report["ownedSessionGone"] = gone;
                    if (!gone) cleanupError = 1460;
                }
            }
            report["phase"] = "finished";
            report["sessionsAfter"] = Sessions();
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            if (cleanupError != 0) throw new Win32Exception(cleanupError, $"Stopping owned session {sessionId} failed");
            if (credentialCleanupError is not null) throw new InvalidOperationException("Credential cleanup failed", credentialCleanupError);
        }
    }

    private static HelperResult Helper(Job job, params string[] arguments)
    {
        var start = new ProcessStartInfo(job.Helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Helper failed to start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        int timeout = arguments.FirstOrDefault() == "stream" ? 45000 : 30000;
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"Owned helper exceeded {timeout} milliseconds");
        }
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static int Worker(string jobPath)
    {
        var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath)) ?? throw new InvalidDataException("Invalid job");
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["sessionId"] = Process.GetCurrentProcess().SessionId,
            ["user"] = Environment.UserName
        };
        Process? app = null;
        try
        {
            if (job.ExpectedSessionId is not null && Process.GetCurrentProcess().SessionId != job.ExpectedSessionId)
                throw new InvalidOperationException("Worker is not in the expected owned session");
            File.WriteAllText(Path.Combine(job.Output, "worker-started.json"), JsonSerializer.Serialize(report, JsonOptions));
            var start = new ProcessStartInfo(job.App) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(job.App)! };
            if (job.Variant is not null)
            {
                if (job.Variant is not ("light" or "dark" or "dark-large")) throw new ArgumentException("Unsupported fixture variant");
                start.Environment["SBX_VISUAL_VARIANT"] = job.Variant;
                start.Environment["SBX_VISUAL_OUTPUT"] = job.Output;
                report["variant"] = job.Variant;
            }
            app = Process.Start(start)
                ?? throw new InvalidOperationException("App failed to start");
            report["appPid"] = app.Id;
            var before = Helper(job, "capture", "--pid", app.Id.ToString(), "--out", job.Output, "--label", "1-before");
            report["before"] = before;
            var uia = Helper(job, "uia", "--pid", app.Id.ToString(), "--set", "NameBox=FullSession",
                "--invoke", "GreetButton,IncrementButton,IncrementButton");
            report["uia"] = uia;
            var after = Helper(job, "capture", "--pid", app.Id.ToString(), "--out", job.Output, "--label", "2-after");
            report["after"] = after;
            if (new[] { before, uia, after }.Any(result => result.ExitCode != 0))
                throw new InvalidOperationException("One or more helper operations failed; see recorded output");
            if (job.Variant is not null)
            {
                var stream = Helper(job, "stream", "--pid", app.Id.ToString(), "--out", job.Output);
                report["stream"] = stream;
                if (stream.ExitCode != 0) throw new InvalidOperationException("Bounded screenshot stream failed");
            }
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        finally
        {
            if (app is not null)
            {
                try
                {
                    if (!app.HasExited) { app.Kill(entireProcessTree: true); app.WaitForExit(5000); }
                    report["appCleanup"] = "terminated owned app";
                }
                catch (Exception error) { report["appCleanupError"] = error.ToString(); }
                app.Dispose();
            }
            var output = Path.Combine(job.Output, "worker.json");
            File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(report, JsonOptions));
            File.Move(output + ".tmp", output);
        }
        return report.ContainsKey("error") ? 1 : 0;
    }
}
