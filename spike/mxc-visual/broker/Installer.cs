using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Pipes;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace SandboxMcp.FullBroker;

internal sealed record Manifest(int Schema, Dictionary<string, string> Files);
internal static class Installer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureAction { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions { public uint Reset; public IntPtr Reboot, Command; public uint Count; public IntPtr Actions; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(Native.ServiceHandle service, uint level, ref FailureActions actions);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string? destination, uint flags);

    public static int Elevate(string operation, string? source)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.User?.Value ?? throw new InvalidOperationException("Missing installation owner");
        if (identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid)) throw new InvalidOperationException("Install as the owning interactive user, not System");
        using var clientWorkflow = operation == "uninstall-elevated" ? Client.AcquireWorkflowLock() : null;
        if (clientWorkflow is not null) Client.RequireNoPendingAccounts(Client.StateRoot);
        string? package = source is null ? null : Path.GetFullPath(source);
        if (package is not null) RequireLocalPackage(package);
        using var manifest = package is null ? null : File.OpenRead(Path.Combine(package, "manifest.json"));
        string? hash = manifest is null ? null : Convert.ToHexString(SHA256.HashData(manifest));
        using var exeLock = File.OpenRead(Environment.ProcessPath!);
        using var dllLock = File.OpenRead(typeof(Program).Assembly.Location);
        string pipeName = "SandboxMcpFullBroker.Install." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 8192, 8192, security);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var connection = pipe.WaitForConnectionAsync(deadline.Token);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(operation);
        if (package is not null) start.ArgumentList.Add(package);
        start.ArgumentList.Add(owner);
        if (hash is not null) start.ArgumentList.Add(hash);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Installer did not start");
        connection.GetAwaiter().GetResult();
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.Id)
            throw new UnauthorizedAccessException("Unexpected installer result-pipe client");
        byte[] response = Protocol.Read(pipe, 8192, deadline.Token).GetAwaiter().GetResult();
        if (!process.WaitForExit(30000)) throw new TimeoutException("Installer cleanup is still running; do not retry");
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(response));
        return process.ExitCode;
    }

    public static int ReportResult(string pipeName, uint parentPid, Func<object> operation)
    {
        if (!Regex.IsMatch(pipeName, "^SandboxMcpFullBroker\\.Install\\.[a-f0-9]{32}$"))
            throw new ArgumentException("Invalid installer result pipe");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        pipe.ConnectAsync(30000, timeout.Token).GetAwaiter().GetResult();
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != parentPid)
            throw new UnauthorizedAccessException("Unexpected installer parent");
        object result;
        int exitCode;
        try { result = operation(); exitCode = 0; }
        catch (Exception error)
        {
            result = new { ok = false, error = error.Message.Length > 4096 ? error.Message[..4096] : error.Message, type = error.GetType().Name };
            exitCode = 1;
        }
        Protocol.Write(pipe, result, 8192, timeout.Token).GetAwaiter().GetResult();
        return exitCode;
    }

    private static void RequireAdministrator(string owner)
    {
        Paths.ValidateOwnerSid(owner);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != new SecurityIdentifier(owner).Value
            || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Installer must be elevated as the selected Windows owner");
    }
    public static Manifest ReadManifest(string source, string expectedHash)
    {
        RequireLocalPackage(source);
        Paths.RejectReparse(source);
        string path = Path.Combine(source, "manifest.json");
        Paths.RejectReparse(path);
        using var file = File.OpenRead(path);
        if (file.Length > 1048576) throw new InvalidDataException("Package manifest exceeds limit");
        if (Convert.ToHexString(SHA256.HashData(file)) != expectedHash) throw new InvalidDataException("Package manifest changed after approval");
        file.Position = 0;
        var manifest = JsonSerializer.Deserialize<Manifest>(file, Protocol.Json) ?? throw new InvalidDataException("Missing package manifest");
        if (manifest.Schema != 1 || manifest.Files.Count is 0 or > 3000) throw new InvalidDataException("Unsupported package manifest");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in manifest.Files)
        {
            ValidateRelativePath(relative);
            if (!names.Add(relative) || !Regex.IsMatch(hash, "^[A-F0-9]{64}$")) throw new InvalidDataException("Invalid package file identity");
        }
        foreach (string required in new[]
        {
            @"broker\SandboxMcp.FullBroker.exe", @"broker\SandboxMcp.FullBroker.dll",
            @"broker\SandboxMcp.FullBroker.runtimeconfig.json", @"broker\SandboxMcp.FullBroker.deps.json",
            @"worker\HeadlessSpike.exe", @"worker\HeadlessSpike.dll", @"helper\CaptureHelper.exe", @"fixture\HelloWinUI.exe"
        })
            if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException("Package is missing a required component");
        return manifest;
    }

    public static object VerifyPackage(string source)
    {
        RequireLocalPackage(source);
        using var file = File.OpenRead(Path.Combine(source, "manifest.json"));
        string hash = Convert.ToHexString(SHA256.HashData(file));
        var manifest = ReadManifest(source, hash);
        long bytes = 0;
        foreach (var (relative, expected) in manifest.Files)
        {
            string path = Path.Combine(source, relative);
            Paths.RejectReparse(path);
            using var input = File.OpenRead(path);
            bytes = checked(bytes + input.Length);
            if (input.Length > 536870912 || bytes > 1073741824) throw new InvalidDataException("Package exceeds size limits");
            if (Convert.ToHexString(SHA256.HashData(input)) != expected) throw new InvalidDataException("Package hash mismatch");
        }
        Platform.Check();
        return new { ok = true, release = hash, files = manifest.Files.Count, bytes, supportedCore = LocalAgentCredential.CheckSupportedBuild() };
    }

    private static void RequireLocalPackage(string source)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(source));
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Experimental broker packages must be on a local drive");
    }

    internal static void ValidateRelativePath(string relative)
    {
        if (relative.Length is 0 or > 240 || Path.IsPathRooted(relative) || relative.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new InvalidDataException("Invalid package-relative path");
        string[] parts = relative.Split('\\');
        if (parts.Length < 2 || parts[0] is not ("broker" or "worker" or "helper" or "fixture"))
            throw new InvalidDataException("Unexpected package component");
        foreach (string part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(char.IsControl)
                || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe package path segment");
        }
    }

    public static object Install(string source, string owner, string hash)
    {
        RequireAdministrator(owner);
        using var installation = new InstallationGate();
        Platform.Check();
        var manifest = ReadManifest(source, hash);
        Paths.CreateProtectedDirectory(Paths.InstallRoot, true);
        Paths.CreateProtectedDirectory(Paths.StateRoot, false);
        if (File.Exists(Paths.ConfigurationPath) && Paths.ReadConfiguration().OwnerSid != owner)
            throw new UnauthorizedAccessException("Existing installation belongs to a different owner; scope changes require uninstall");
        Paths.CreateProtectedDirectory(Paths.Jobs, false);
        Paths.CreateProtectedDirectory(Path.Combine(Paths.InstallRoot, "versions"), true);
        string release = Paths.Release(hash);
        bool newRelease = !Directory.Exists(release);
        string destinationRoot = newRelease ? Path.Combine(Paths.InstallRoot, "versions", ".staging-" + Guid.NewGuid().ToString("N")) : release;
        Paths.CreateProtectedDirectory(destinationRoot, true);
        try
        {
            long total = 0;
            foreach (var (relative, expected) in manifest.Files)
            {
                string input = Path.Combine(source, relative), target = Path.Combine(destinationRoot, relative);
                Paths.RejectReparse(input);
                using var file = File.OpenRead(input);
                total = checked(total + file.Length);
                if (total > 1073741824 || file.Length > 536870912) throw new InvalidDataException("Package exceeds size limits");
                if (Convert.ToHexString(SHA256.HashData(file)) != expected) throw new InvalidDataException("Package file changed or is corrupt");
                if (newRelease)
                {
                    string parent = Path.GetDirectoryName(target)!;
                    string currentDirectory = destinationRoot;
                    foreach (string part in relative.Split('\\')[..^1])
                    {
                        currentDirectory = Path.Combine(currentDirectory, part);
                        Paths.CreateProtectedDirectory(currentDirectory, true);
                    }
                    Paths.RequireProtected(parent);
                    file.Position = 0;
                    using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.CopyTo(destination);
                    Paths.ProtectFile(target, true);
                    using var written = File.OpenRead(target);
                    if (Convert.ToHexString(SHA256.HashData(written)) != expected) throw new InvalidDataException("Installed copy failed verification");
                }
                else
                {
                    Paths.RequireProtected(target);
                    using var installed = File.OpenRead(target);
                    if (Convert.ToHexString(SHA256.HashData(installed)) != expected) throw new InvalidDataException("Existing installed release failed verification");
                }
            }
            if (newRelease) Directory.Move(destinationRoot, release);
        }
        catch
        {
            if (newRelease && Directory.Exists(destinationRoot)) DeleteProtectedTree(destinationRoot, false);
            throw;
        }
        using var manager = Native.OpenSCManagerW(null, null, 3);
        if (manager.IsInvalid) throw Native.Error("Open service manager for installation");
        string command = $"\"{Path.Combine(release, "broker", "SandboxMcp.FullBroker.exe")}\" service";
        using var existing = Native.OpenServiceW(manager, Paths.ServiceName, 0xF01FF);
        int openError = Marshal.GetLastWin32Error();
        Native.ServiceHandle? created = null;
        bool verifiedExisting = false, stoppedExisting = false, modifiedExisting = false;
        Configuration? oldConfig = File.Exists(Paths.ConfigurationPath) ? Paths.ReadConfiguration() : null;
        try
        {
            Native.ServiceHandle service;
            if (!existing.IsInvalid)
            {
                if (oldConfig is null) throw new InvalidOperationException("Service name is occupied by an unrecognized installation");
                VerifyRegisteredImage(oldConfig);
                verifiedExisting = true;
                StopService(existing);
                stoppedExisting = true;
                if (Paths.Leases().Length != 0) throw new InvalidOperationException("Recover and acknowledge pending client jobs before updating");
                if (!Native.ChangeServiceConfigW(existing, 0x10, 2, 1, command, null, IntPtr.Zero, null, "LocalSystem", null, null))
                    throw Native.Error("Update owned service");
                modifiedExisting = true;
                service = existing;
            }
            else
            {
                if (openError != 1060) throw new Win32Exception(openError, "Read installed service");
                created = Native.CreateServiceW(manager, Paths.ServiceName, "Sandbox MCP experimental Full-session broker",
                    0xF01FF, 0x10, 2, 1, command, null, IntPtr.Zero, null, null, null);
                if (created.IsInvalid) throw Native.Error("Create installed broker service");
                service = created;
            }
            Paths.AtomicJson(Paths.ConfigurationPath, new Configuration(1, owner, hash));
            Paths.ProtectFile(Paths.ConfigurationPath, false);
            SetServiceSecurity(service, owner);
            SetRecovery(service);
            if (!Native.StartServiceW(service, 0, IntPtr.Zero)) throw Native.Error("Start installed broker");
            var timer = Stopwatch.StartNew();
            while (Native.Query(service).State != 4)
            {
                if (Native.Query(service).State == 1 || timer.Elapsed > TimeSpan.FromSeconds(60))
                    throw new InvalidOperationException("Installed broker did not reach running state");
                Thread.Sleep(250);
            }
            Paths.AtomicJson(Path.Combine(Paths.InstallRoot, "current.json"), new { schema = 1, release = hash }, true);
            Paths.ProtectFile(Path.Combine(Paths.InstallRoot, "current.json"), true);
            return new { ok = true, installed = true, service = Paths.ServiceName, release, owner };
        }
        catch
        {
            if (created is not null && !created.IsInvalid)
            {
                StopService(created);
                if (!Native.DeleteService(created)) throw Native.Error("Remove failed new broker installation");
            }
            else if (!existing.IsInvalid && oldConfig is not null && verifiedExisting && stoppedExisting)
            {
                if (modifiedExisting)
                {
                    string oldCommand = $"\"{Path.Combine(Paths.Release(oldConfig.Release), "broker", "SandboxMcp.FullBroker.exe")}\" service";
                    if (!Native.ChangeServiceConfigW(existing, 0x10, 2, 1, oldCommand, null, IntPtr.Zero, null, "LocalSystem", null, null))
                        throw Native.Error("Restore previous broker configuration");
                    Paths.AtomicJson(Paths.ConfigurationPath, oldConfig);
                }
                if (Native.Query(existing).State == 1 && !Native.StartServiceW(existing, 0, IntPtr.Zero))
                    throw Native.Error("Restart previous broker after failed update");
            }
            throw;
        }
        finally { created?.Dispose(); }
    }

    private static void VerifyRegisteredImage(Configuration config)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Paths.ServiceName}")
            ?? throw new InvalidOperationException("Service configuration disappeared");
        string expected = $"\"{Path.Combine(Paths.Release(config.Release), "broker", "SandboxMcp.FullBroker.exe")}\" service";
        if (!string.Equals(key.GetValue("ImagePath") as string, expected, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Refusing to modify an unexpected service configuration");
    }
    private static void SetServiceSecurity(Native.ServiceHandle service, string owner)
    {
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LC;;;{owner})", 1, out var descriptor, out _))
            throw Native.Error("Build service security");
        try { if (!Native.SetServiceObjectSecurity(service, 4, descriptor)) throw Native.Error("Protect service configuration"); }
        finally { Native.LocalFree(descriptor); }
    }
    private static void SetRecovery(Native.ServiceHandle service)
    {
        var actions = new[] { new FailureAction { Type = 1, Delay = 1000 }, new FailureAction { Type = 1, Delay = 5000 }, new FailureAction { Type = 0 } };
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<FailureAction>() * actions.Length);
        try
        {
            for (int i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], buffer + i * Marshal.SizeOf<FailureAction>(), false);
            var recovery = new FailureActions { Reset = 3600, Count = (uint)actions.Length, Actions = buffer };
            if (!ChangeServiceConfig2W(service, 2, ref recovery)) throw Native.Error("Configure bounded service crash recovery");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void StopService(Native.ServiceHandle service)
    {
        var initial = Native.Query(service);
        if (initial.State == 1) return;
        Process? process = null;
        try
        {
            if (initial.Pid != 0)
            {
                try { process = Process.GetProcessById(checked((int)initial.Pid)); }
                catch (ArgumentException) when (Native.Query(service).State == 1) { return; }
            }
            if (!Native.ControlService(service, 1, out _))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1062) throw new Win32Exception(error, "Stop installed broker");
            }
            var timer = Stopwatch.StartNew();
            while (Native.Query(service).State != 1)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(180)) throw new TimeoutException("Broker still cleaning up; no forced termination performed");
                Thread.Sleep(250);
            }
            process?.WaitForExit(5000);
        }
        finally { process?.Dispose(); }
    }
    public static object Uninstall(string owner)
    {
        RequireAdministrator(owner);
        using var installation = new InstallationGate();
        var config = Paths.ReadConfiguration();
        if (config.OwnerSid != owner) throw new UnauthorizedAccessException("Installation owner mismatch");
        VerifyRegisteredImage(config);
        using var manager = Native.OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw Native.Error("Open service manager");
        using var service = Native.OpenServiceW(manager, Paths.ServiceName, 0x10034);
        if (service.IsInvalid) throw Native.Error("Read installed service");
        StopService(service);
        if (Paths.Leases().Length != 0)
        {
            if (!Native.StartServiceW(service, 0, IntPtr.Zero)) throw Native.Error("Restart broker for pending cleanup");
            throw new InvalidOperationException("Pending client cleanup exists; run recovery before uninstalling");
        }
        if (!Native.DeleteService(service)) throw Native.Error("Delete installed broker");
        service.Dispose();
        bool reboot = DeleteProtectedTree(Paths.InstallRoot, true);
        reboot |= DeleteProtectedTree(Paths.StateRoot, true);
        return new { ok = true, serviceStopped = true, serviceDeletionRequested = true, rebootRequiredForLockedFiles = reboot };
    }

    private static bool DeleteProtectedTree(string root, bool allowReboot)
    {
        Paths.RequireProtected(root);
        var files = new List<string>();
        var directories = new List<string>();
        void Inspect(string directory)
        {
            Paths.RequireProtected(directory);
            foreach (string file in Directory.GetFiles(directory)) { Paths.RequireProtected(file); files.Add(file); }
            foreach (string child in Directory.GetDirectories(directory)) Inspect(child);
            directories.Add(directory);
        }
        Inspect(root);
        bool pending = false;
        foreach (string file in files)
        {
            try { File.Delete(file); }
            catch (Exception error) when (allowReboot && error is IOException or UnauthorizedAccessException)
            {
                if (!MoveFileExW(file, null, 4)) throw Native.Error("Schedule removal of an owned locked broker file");
                pending = true;
            }
        }
        foreach (string directory in directories)
        {
            if (pending)
            {
                if (!MoveFileExW(directory, null, 4)) throw Native.Error("Schedule removal of an owned broker directory");
            }
            else Directory.Delete(directory, false);
        }
        return pending;
    }

    private sealed class InstallationGate : IDisposable
    {
        private readonly Mutex mutex = new(false, @"Global\SandboxMcpFullBroker.Install");
        public InstallationGate()
        {
            try
            {
                if (!mutex.WaitOne(0)) throw new InvalidOperationException("Another broker installation is already in progress");
            }
            catch (AbandonedMutexException) { Console.Error.WriteLine("Reconciling an interrupted installer operation."); }
            catch { mutex.Dispose(); throw; }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
