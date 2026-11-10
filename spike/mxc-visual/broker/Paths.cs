using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SandboxMcp.FullBroker;

internal static class Paths
{
    public const string ServiceName = "SandboxMcpFullBroker", PipeName = "SandboxMcpFullBroker.v1";
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);
    public static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServiceName);
    public static string ConfigurationPath => Path.Combine(StateRoot, "config.json");
    public static string Jobs => Path.Combine(StateRoot, "jobs");
    public static string ReleaseRoot => Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private const FileSystemRights WriteRights = FileSystemRights.Write | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static string Release(string hash)
    {
        if (!Regex.IsMatch(hash, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid release identity");
        return Path.Combine(InstallRoot, "versions", hash);
    }

    public static Configuration ReadConfiguration()
    {
        RequireProtected(StateRoot);
        RequireProtected(ConfigurationPath);
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigurationPath), Protocol.Json)
            ?? throw new InvalidDataException("Missing broker configuration");
        if (config.Schema != 1) throw new InvalidDataException("Unsupported broker configuration");
        ValidateOwnerSid(config.OwnerSid);
        RequireProtected(InstallRoot);
        RequireProtected(Release(config.Release));
        return config;
    }

    internal static void ValidateOwnerSid(string value)
    {
        string sid = new SecurityIdentifier(value).Value;
        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            throw new InvalidDataException("Installation must be scoped to an interactive local/domain/Entra account");
    }

    public static void RejectReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not permitted in protected broker paths");
            current = Directory.GetParent(current)?.FullName;
        }
    }

    public static void RequireProtected(string path)
    {
        RejectReparse(path);
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateSecurity(security);
    }

    internal static void ValidateSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("Broker path has no owner");
        if (owner != SystemSid && owner != AdminSid) throw new UnauthorizedAccessException("Broker path has an untrusted owner");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & WriteRights) != 0
                && sid != SystemSid && sid != AdminSid)
                throw new UnauthorizedAccessException("Broker path permits non-administrator writes");
        }
    }

    public static void CreateProtectedDirectory(string path, bool readable)
    {
        if (Directory.Exists(path))
        {
            RequireProtected(path);
            return;
        }
        RejectReparse(Directory.GetParent(path)!.FullName);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdminSid);
        foreach (var sid in new[] { SystemSid, AdminSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (readable)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        RequireProtected(path);
    }

    public static void ProtectFile(string path, bool readable)
    {
        RejectReparse(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdminSid);
        foreach (var sid in new[] { SystemSid, AdminSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        if (readable)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        RequireProtected(path);
    }

    public static string JobDirectory(string id)
    {
        Protocol.RequireId(id);
        return Path.Combine(Jobs, id);
    }
    public static void SaveLease(Lease lease)
    {
        string directory = JobDirectory(lease.Request.Id!);
        Directory.CreateDirectory(directory);
        RequireProtected(directory);
        AtomicJson(Path.Combine(directory, "state.json"), lease);
    }
    public static Lease ReadLease(string id)
    {
        string path = Path.Combine(JobDirectory(id), "state.json");
        return ReadLeaseAt(path, id, RequireProtected);
    }
    private static Lease ReadLeaseAt(string path, string id, Action<string> inspect)
    {
        inspect(path);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Lease exceeded its bounded size");
        var lease = JsonSerializer.Deserialize<Lease>(File.ReadAllText(path), Protocol.Json) ?? throw new InvalidDataException("Missing lease");
        if (lease.Request.Id != id) throw new InvalidDataException("Lease identity mismatch");
        Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(lease.Request, Protocol.Json));
        Protocol.RequireId(lease.OutputKey);
        if (lease.Request.Operation != "run-fixture" || lease.Phase is not ("creating" or "running" or "finished" or "recovery-required")
            || lease.Baseline.Length > 4096 || lease.Baseline.Any(value => value < 0))
            throw new InvalidDataException("Invalid lease state");
        return lease;
    }
    public static Lease[] Leases()
    {
        RequireProtected(Jobs);
        ReconcileJournals(Jobs, RequireProtected);
        var directories = Directory.GetDirectories(Jobs);
        if (directories.Length > 128) throw new IOException("Broker cleanup history exceeded its bounded capacity");
        return directories.Select(path => ReadLease(Path.GetFileName(path))).ToArray();
    }

    internal static void ReconcileJournals(string root, Action<string> inspect)
    {
        inspect(root);
        foreach (string directory in Directory.GetDirectories(root))
        {
            inspect(directory);
            string name = Path.GetFileName(directory);
            bool tombstone = name.StartsWith(".cleanup-", StringComparison.Ordinal);
            string id = tombstone ? name[".cleanup-".Length..] : name;
            Protocol.RequireId(id);
            string[] files = Directory.GetFiles(directory);
            if (Directory.GetDirectories(directory).Length != 0
                || files.Any(file => Path.GetFileName(file) != "state.json"
                    && !Regex.IsMatch(Path.GetFileName(file), "^state\\.json\\.[a-f0-9]{32}\\.tmp$")))
                throw new InvalidDataException("Unexpected journal contents; refusing to discard recovery evidence");
            foreach (string file in files) inspect(file);
            string state = Path.Combine(directory, "state.json");
            if (!tombstone && File.Exists(state))
            {
                _ = ReadLeaseAt(state, id, inspect);
                foreach (string temporary in files.Where(file => file != state)) File.Delete(temporary);
                continue;
            }
            // Without the initial committed state, Run has not reached WTS.
            // A cleanup marker instead proves acknowledgement already committed.
            if (!tombstone)
            {
                string destination = Path.Combine(root, ".cleanup-" + id);
                Directory.Move(directory, destination);
                DeleteJournalContents(destination, inspect);
            }
            else DeleteJournalContents(directory, inspect);
        }
    }

    public static void AcknowledgeJournal(string id)
    {
        string directory = JobDirectory(id);
        RequireProtected(directory);
        string tombstone = Path.Combine(Jobs, ".cleanup-" + id);
        Directory.Move(directory, tombstone);
        DeleteJournalContents(tombstone, RequireProtected);
    }

    private static void DeleteJournalContents(string directory, Action<string> inspect)
    {
        inspect(directory);
        if (Directory.GetDirectories(directory).Length != 0) throw new InvalidDataException("Unexpected nested journal directory");
        foreach (string file in Directory.GetFiles(directory))
        {
            inspect(file);
            if (Path.GetFileName(file) != "state.json"
                && !Regex.IsMatch(Path.GetFileName(file), "^state\\.json\\.[a-f0-9]{32}\\.tmp$"))
                throw new InvalidDataException("Unexpected cleanup file");
            File.Delete(file);
        }
        Directory.Delete(directory, false);
    }
    public static void AtomicJson<T>(string path, T value, bool readable = false)
    {
        RequireProtected(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) RequireProtected(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Protocol.Json);
                stream.Flush(true);
            }
            ProtectFile(temporary, readable);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
