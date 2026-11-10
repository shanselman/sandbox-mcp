using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class SelfTest
{
    public static int Run()
    {
        int passed = 0;
        var failures = new List<string>();
        void Check(string name, Action test)
        {
            try { test(); passed++; }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        static void Reject(Action operation)
        {
            try { operation(); }
            catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException) { return; }
            throw new Exception("Input was unexpectedly accepted");
        }
        static Request Parse(string text) => Protocol.Parse(Encoding.UTF8.GetBytes(text));
        Check("status", () => { if (Parse("""{"protocol":1,"operation":"status"}""").Operation != "status") throw new Exception("Wrong operation"); });
        foreach (string input in new[]
        {
            """{"protocol":2,"operation":"status"}""",
            """{"protocol":1,"operation":"exec","command":"whoami"}""",
            """{"protocol":1,"operation":"status","ownerSid":"S-1-5-18"}""",
            """{"protocol":1,"operation":"status","output":"C:\\Windows\\test"}""",
            """{"protocol":1,"operation":"status","operation":"recover"}""",
            """{"protocol":1,"operation":"result","id":".."}""",
            """{"protocol":1,"operation":"run-fixture","id":"00000000000000000000000000000000","agentName":"../x","agentSid":"S-1-5-18","containerId":"opaque"}""",
            "[]", "null", "{"
        })
            Check("reject protocol " + passed, () => Reject(() => Parse(input)));
        Check("valid fixture", () => Parse("""{"protocol":1,"operation":"run-fixture","id":"0123456789abcdef0123456789abcdef","agentName":"A1-B2","agentSid":"S-1-5-21-1-2-3-1001","containerId":"opaque-do-not-parse"}"""));
        Check("client serialization omits irrelevant fields", () => Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(new Request(1, "status"), Protocol.Json)));
        foreach (string input in new[] { @"..\broker\evil.exe", @"broker\..\evil.exe", @"C:\broker\evil.exe", @"broker\file:stream", @"broker\CON", @"broker\file.", @"broker\file ", @"broker\\file", "broker/file", @"unknown\file" })
            Check("reject package path " + input, () => Reject(() => Installer.ValidateRelativePath(input)));
        Check("valid package path", () => Installer.ValidateRelativePath(@"fixture\Assets\Logo.png"));
        Check("local owner SID", () => Paths.ValidateOwnerSid("S-1-5-21-1-2-3-1001"));
        Check("Entra owner SID", () => Paths.ValidateOwnerSid("S-1-12-1-1-2-3-4"));
        Check("reject System owner scope", () => Reject(() => Paths.ValidateOwnerSid("S-1-5-18")));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        DirectorySecurity Descriptor(SecurityIdentifier owner, FileSystemRights rights)
        {
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, rights, AccessControlType.Allow));
            return security;
        }
        Check("protected read-only payload ACL", () => Paths.ValidateSecurity(Descriptor(admin, FileSystemRights.ReadAndExecute)));
        Check("reject writable payload ACL", () => Reject(() => Paths.ValidateSecurity(Descriptor(admin, FileSystemRights.Modify))));
        Check("reject caller-owned payload", () => Reject(() => Paths.ValidateSecurity(Descriptor(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), FileSystemRights.ReadAndExecute))));
        Check("bounded frame", () =>
        {
            using var memory = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0x7f });
            Reject(() => Protocol.Read(memory, 16384, CancellationToken.None).GetAwaiter().GetResult());
        });
        Check("framed round-trip", () =>
        {
            using var memory = new MemoryStream();
            Protocol.Write(memory, new Request(1, "status"), 16384, CancellationToken.None).GetAwaiter().GetResult();
            memory.Position = 0;
            Protocol.Parse(Protocol.Read(memory, 16384, CancellationToken.None).GetAwaiter().GetResult());
        });
        var lease = new Lease(new Request(1, "run-fixture", "0123456789abcdef0123456789abcdef",
            "A1-B2", "S-1-5-21-1-2-3-1001", "opaque"), "0123456789abcdef0123456789abcdef",
            [0, 1, 8], null, "creating", DateTimeOffset.UtcNow);
        Check("recovery selects owned sessions only", () =>
        {
            var plan = Runtime.PlanCleanup(lease, new Dictionary<int, Native.Ownership>
            {
                [1] = Native.Ownership.Other, [14] = Native.Ownership.Owned, [15] = Native.Ownership.Other
            });
            if (!plan.Owned.SequenceEqual([14]) || plan.Uncertain.Length != 0) throw new Exception("Wrong recovery targets");
        });
        Check("interrupted creation preserves uncertainty", () =>
        {
            var plan = Runtime.PlanCleanup(lease, new Dictionary<int, Native.Ownership> { [14] = Native.Ownership.Unknown });
            if (plan.Owned.Length != 0 || !plan.Uncertain.SequenceEqual([14])) throw new Exception("Unknown session was misclassified");
        });
        Check("known session ID cannot authorize a different user", () =>
        {
            var plan = Runtime.PlanCleanup(lease with { SessionId = 14 }, new Dictionary<int, Native.Ownership> { [14] = Native.Ownership.Other });
            if (plan.Owned.Length != 0) throw new Exception("Recycled session selected");
        });
        Check("baseline collision refuses teardown", () =>
        {
            try { Runtime.PlanCleanup(lease, new Dictionary<int, Native.Ownership> { [8] = Native.Ownership.Owned }); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Baseline collision accepted");
        });
        void FileCase(string name, Action<string> test)
        {
            Check(name, () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "FullBrokerJournalTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try { test(root); }
                finally { Directory.Delete(root, true); }
            });
        }
        FileCase("discard uncommitted journal creation", root =>
        {
            string directory = Path.Combine(root, lease.Request.Id!);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json." + Guid.NewGuid().ToString("N") + ".tmp"), "{partial");
            Paths.ReconcileJournals(root, _ => { });
            if (Directory.GetDirectories(root).Length != 0) throw new Exception("Uncommitted journal survived");
        });
        FileCase("retain committed state and remove abandoned update", root =>
        {
            string directory = Path.Combine(root, lease.Request.Id!);
            Directory.CreateDirectory(directory);
            string state = Path.Combine(directory, "state.json");
            File.WriteAllText(state, JsonSerializer.Serialize(lease, Protocol.Json));
            File.WriteAllText(state + "." + Guid.NewGuid().ToString("N") + ".tmp", "{partial");
            Paths.ReconcileJournals(root, _ => { });
            if (!File.Exists(state) || Directory.GetFiles(directory).Length != 1) throw new Exception("Committed state was not preserved");
        });
        FileCase("finish interrupted acknowledgement", root =>
        {
            string directory = Path.Combine(root, ".cleanup-" + lease.Request.Id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json." + Guid.NewGuid().ToString("N") + ".tmp"), "leftover");
            Paths.ReconcileJournals(root, _ => { });
            if (Directory.GetDirectories(root).Length != 0) throw new Exception("Acknowledgement tombstone survived");
        });
        FileCase("corrupt committed journal stays available for reconciliation", root =>
        {
            string directory = Path.Combine(root, lease.Request.Id!);
            Directory.CreateDirectory(directory);
            string state = Path.Combine(directory, "state.json");
            File.WriteAllText(state, "{}");
            Reject(() => Paths.ReconcileJournals(root, _ => { }));
            if (!File.Exists(state)) throw new Exception("Corrupt committed evidence was discarded");
        });
        FileCase("uninstall refuses client-only provisioned state", root =>
        {
            string jobs = Path.Combine(root, "jobs");
            Directory.CreateDirectory(jobs);
            File.WriteAllText(Path.Combine(jobs, lease.Request.Id + ".json"), """{"phase":"provisioning"}""");
            try { Client.RequireNoPendingAccounts(root); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Uninstall ignored client-only state");
        });
        Console.WriteLine(JsonSerializer.Serialize(new { passed, failures }, Protocol.Json));
        return failures.Count == 0 ? 0 : 1;
    }
}
