using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal static class ControlledAccount
{
    private const int SecretBytes = 74;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Account(string Name, string Sid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo
    {
        public string Name;
        public IntPtr Password;
        public uint PasswordAge;
        public uint Privilege;
        public string? Home;
        public string Comment;
        public uint Flags;
        public string? Script;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct GroupMember { public string Name; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, uint level, ref UserInfo info, out uint parameterError);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? server, string user);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, uint level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string group, uint level, ref GroupMember member, uint count);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteProfileW(string sid, string? profile, string? computer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(string user, string domain, IntPtr password, int type, int provider, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int InitialProgramControl(string output, string appDirectory, string helperDirectory)
    {
        if (IsElevated()) throw new InvalidOperationException("The initial-program control must run unelevated");
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new control evidence directory");
        Directory.CreateDirectory(output);
        string name = $"sbxn-{Guid.NewGuid():N}"[..17];
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow, ["testCallerElevated"] = false,
            ["operation"] = "nonexistent-account initial-program negative control",
            ["accountName"] = name, ["accountCreated"] = false
        };
        byte[] secret = CreateSecret();
        IntPtr password = Marshal.AllocHGlobal(SecretBytes);
        int result = 1;
        try
        {
            EnsureAccountAbsent(name);
            report["accountAbsentBefore"] = true;
            Marshal.Copy(secret, 0, password, SecretBytes);
            CryptographicOperations.ZeroMemory(secret);
            var encoding = new Dictionary<string, object?>();
            report["encoding"] = encoding;
            result = CredentialPacking.WithEx2($@"{Environment.MachineName}\{name}", password, (buffer, bytes) =>
            {
                foreach (string kind in new[] { "full", "utility" })
                foreach (bool noProgram in new[] { false, true })
                {
                    EnsureAccountAbsent(name);
                    string label = $"{kind}-{(noProgram ? "no-program" : "worker")}";
                    string evidence = Path.Combine(output, label);
                    var args = new List<string>
                    {
                        "run", kind, "caller", evidence,
                        Path.Combine(Path.GetFullPath(appDirectory), "HelloWinUI.exe"),
                        Path.Combine(Path.GetFullPath(helperDirectory), "CaptureHelper.exe")
                    };
                    if (noProgram) args.Add("no-program");
                    Program.Run(args.ToArray(), buffer, bytes,
                        suppliedIdentityDescription: "nonexistent local account; credential-bearing EX2 negative control");
                    using var observation = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "host.json")));
                    var root = observation.RootElement;
                    if (!root.GetProperty("createAttempted").GetBoolean()
                        || !root.TryGetProperty("created", out var created) || created.GetBoolean()
                        || root.TryGetProperty("error", out _))
                        throw new InvalidOperationException("Negative control did not end in a clean creation refusal; inspect evidence");
                    var before = root.GetProperty("sessionsBefore").EnumerateArray().Select(value => value.GetInt32()).ToHashSet();
                    var after = root.GetProperty("sessionsAfter").EnumerateArray().Select(value => value.GetInt32()).ToHashSet();
                    if (!before.SetEquals(after))
                        throw new InvalidOperationException("Session inventory changed during the negative control; stop and inspect before continuing");
                    report[label] = new
                    {
                        error = root.GetProperty("createError").GetProperty("code").GetInt32(),
                        milliseconds = root.GetProperty("createMs").GetInt64()
                    };
                }
                return 0;
            }, encoding);
            EnsureAccountAbsent(name);
            report["accountAbsentAfter"] = true;
        }
        catch (Exception error) { report["error"] = error.Message; result = 1; }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            Marshal.Copy(new byte[SecretBytes], 0, password, SecretBytes);
            Marshal.FreeHGlobal(password);
            report["measurementCompleted"] = result == 0;
            File.WriteAllText(Path.Combine(output, "initial-program-control.json"), JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        return result;
    }

    private static void EnsureAccountAbsent(string name)
    {
        int status = NetUserGetInfo(null, name, 0, out var buffer);
        try
        {
            if (status != 2221) throw new InvalidOperationException($"Expected NERR_UserNotFound for the synthetic account; got {status}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                int freeStatus = NetApiBufferFree(buffer);
                if (freeStatus != 0) throw new Win32Exception(freeStatus, "NetApiBufferFree");
            }
        }
    }

    public static async Task<int> Run(string output, string appDirectory, string helperDirectory)
    {
        if (IsElevated()) throw new InvalidOperationException("The WTS control must run unelevated");
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new control evidence directory");
        Directory.CreateDirectory(output);
        string stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), $"sbxw-{Guid.NewGuid():N}");
        using var identity = WindowsIdentity.GetCurrent();
        var callerSid = identity.User ?? throw new InvalidOperationException("Missing caller SID");
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(true, false);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(callerSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(stage).Create(directorySecurity);
        var report = new Dictionary<string, object?> { ["at"] = DateTimeOffset.UtcNow, ["testCallerElevated"] = false, ["stage"] = stage };
        Process? setup = null;
        Account? account = null;
        int exitCode = 1;
        string pipeName = $"sbxw-{Guid.NewGuid():N}";
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(true, false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(callerSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
        try
        {
            CopyTree(AppContext.BaseDirectory, Path.Combine(stage, "headless"));
            CopyTree(Path.GetFullPath(appDirectory), Path.Combine(stage, "app"));
            CopyTree(Path.GetFullPath(helperDirectory), Path.Combine(stage, "helper"));
            var start = new ProcessStartInfo(Path.Combine(stage, "headless", "HeadlessSpike.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "account-lease", pipeName, Environment.ProcessId.ToString(), output })
                start.ArgumentList.Add(argument);
            setup = Process.Start(start) ?? throw new InvalidOperationException("Account helper failed to launch");
            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await pipe.WaitForConnectionAsync(handshake.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != setup.Id)
                throw new InvalidOperationException("Unexpected account-helper pipe client");
            account = await Read<Account>(pipe, handshake.Token);
            if (!account.Name.StartsWith("sbxw-", StringComparison.Ordinal) || account.Name.Length != 17)
                throw new InvalidDataException("Unexpected disposable account name");
            report["account"] = account;
            byte[] secret = new byte[SecretBytes];
            IntPtr password = Marshal.AllocHGlobal(SecretBytes);
            try
            {
                await pipe.ReadExactlyAsync(secret, handshake.Token);
                Marshal.Copy(secret, 0, password, SecretBytes);
                CryptographicOperations.ZeroMemory(secret);
                var testSid = new SecurityIdentifier(account.Sid);
                directorySecurity.AddAccessRule(new FileSystemAccessRule(testSid, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(stage).SetAccessControl(directorySecurity);

                bool loggedOn = LogonUserW(account.Name, Environment.MachineName, password, 2, 0, out var token);
                int logonError = Marshal.GetLastWin32Error();
                using (token)
                {
                    report["logon"] = new { succeeded = loggedOn, error = loggedOn ? 0 : logonError };
                    if (!loggedOn) throw new Win32Exception(logonError, "Disposable-account LogonUserW control");
                }
                var encoding = new Dictionary<string, object?>();
                report["encoding"] = encoding;
                exitCode = CredentialPacking.WithEx2($@"{Environment.MachineName}\{account.Name}", password, (buffer, bytes) =>
                {
                    int failures = 0;
                    foreach (string kind in new[] { "full", "utility" })
                    {
                        string evidence = Path.Combine(stage, $"out-{kind}");
                        Directory.CreateDirectory(evidence);
                        var security = new DirectoryInfo(evidence).GetAccessControl();
                        security.AddAccessRule(new FileSystemAccessRule(testSid, FileSystemRights.Modify,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                        new DirectoryInfo(evidence).SetAccessControl(security);
                        int result = Program.Run(new[]
                        {
                            "run", kind, "caller", evidence, Path.Combine(stage, "app", "HelloWinUI.exe"),
                            Path.Combine(stage, "helper", "CaptureHelper.exe")
                        }, buffer, bytes, Path.Combine(stage, "headless", "HeadlessSpike.exe"));
                        report[kind] = result;
                        failures += result == 0 ? 0 : 1;
                        CopyTree(evidence, Path.Combine(output, kind));
                    }
                    return failures == 0 ? 0 : 1;
                }, encoding);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                Marshal.Copy(new byte[SecretBytes], 0, password, SecretBytes);
                Marshal.FreeHGlobal(password);
            }
        }
        catch (Exception error) { report["error"] = error.Message; exitCode = 1; }
        finally
        {
            // Disconnecting releases the lease even when the experiment failed.
            pipe.Dispose();
            if (setup is not null)
            {
                try
                {
                    await setup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
                    report["accountHelperExitCode"] = setup.ExitCode;
                    if (setup.ExitCode != 0) exitCode = 1;
                }
                catch (Exception error) { report["accountHelperWaitError"] = error.Message; exitCode = 1; }
                setup.Dispose();
            }
            string cleanupPath = Path.Combine(output, "account-cleanup.json");
            if (File.Exists(cleanupPath))
                report["accountCleanup"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cleanupPath));
            else if (account is not null)
            {
                report["accountCleanupError"] = "Helper cleanup confirmation missing";
                exitCode = 1;
            }
            // Keep the staging area on failure for diagnostics and explicit cleanup.
            report["retainedStage"] = stage;
            File.WriteAllText(Path.Combine(output, "control.json"), JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        return exitCode;
    }

    public static async Task<int> Lease(string pipeName, uint parentPid, string output)
    {
        if (!IsElevated()) throw new InvalidOperationException("Account setup requires its explicitly approved elevated helper");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await pipe.ConnectAsync(60000, lifetime.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentPid)
            throw new InvalidOperationException("Unexpected parent pipe server");
        string name = $"sbxw-{Guid.NewGuid():N}"[..17];
        string? sid = null;
        bool created = false;
        byte[] secret = CreateSecret();
        IntPtr password = Marshal.AllocHGlobal(SecretBytes);
        var cleanup = new Dictionary<string, object?> { ["account"] = name, ["setupElevated"] = true };
        int result = 0;
        try
        {
            Marshal.Copy(secret, 0, password, SecretBytes);
            var user = new UserInfo { Name = name, Password = password, Privilege = 1, Flags = 0x201,
                Comment = "Temporary sandbox-mcp WTS experiment; owned by account-lease helper" };
            int status = NetUserAdd(null, 1, ref user, out uint parameter);
            if (status != 0) throw new Win32Exception(status, $"NetUserAdd parameter {parameter}");
            created = true;
            sid = ((SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier))).Value;
            string group = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Translate(typeof(NTAccount))).Value.Split('\\')[^1];
            var member = new GroupMember { Name = $@"{Environment.MachineName}\{name}" };
            status = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
            if (status != 0 && status != 1378) throw new Win32Exception(status, "Adding the standard user to Users");
            await Send(pipe, new Account(name, sid), lifetime.Token);
            await pipe.WriteAsync(secret, lifetime.Token);
            await pipe.FlushAsync(lifetime.Token);
            CryptographicOperations.ZeroMemory(secret);
            Marshal.Copy(new byte[SecretBytes], 0, password, SecretBytes);
            // Only account administration happens here; the unelevated parent
            // owns logon checks, WTS calls and app execution.
            var end = new byte[1];
            if (await pipe.ReadAsync(end, lifetime.Token) != 0)
                throw new InvalidDataException("Unexpected lease message");
        }
        catch (Exception error) { cleanup["error"] = error.Message; result = 1; }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            Marshal.Copy(new byte[SecretBytes], 0, password, SecretBytes);
            Marshal.FreeHGlobal(password);
            if (created)
            {
                if (sid is not null)
                {
                    bool deleted = DeleteProfileW(sid, null, null);
                    int error = Marshal.GetLastWin32Error();
                    cleanup["profile"] = new { removed = deleted, notPresent = !deleted && error == 2, error = deleted ? 0 : error };
                    if (!deleted && error != 2) result = 1;
                }
                int status = NetUserDel(null, name);
                cleanup["deleteAccountStatus"] = status;
                if (status != 0) result = 1;
            }
            cleanup["created"] = created;
            File.WriteAllText(Path.Combine(output, "account-cleanup.json"), JsonSerializer.Serialize(cleanup, JsonOptions));
        }
        return result;
    }

    private static byte[] CreateSecret()
    {
        byte[] secret = new byte[SecretBytes];
        byte[] random = RandomNumberGenerator.GetBytes(32);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        try
        {
            for (int i = 0; i < 36; i++)
            {
                char character = i < 4 ? "Aa1!"[i] : alphabet[random[i - 4] & 63];
                BinaryPrimitives.WriteUInt16LittleEndian(secret.AsSpan(i * 2, 2), character);
            }
            return secret;
        }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    private static async Task Send<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        await stream.WriteAsync(size, token);
        await stream.WriteAsync(data, token);
    }

    private static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4];
        await stream.ReadExactlyAsync(size, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > 16384) throw new InvalidDataException("Invalid control-message size");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Missing control message");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        foreach (string directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
