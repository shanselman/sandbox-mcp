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
        foreach (string variant in new[] { "light", "dark", "dark-large" })
            Check("allowlisted variant " + variant, () => Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(
                new Request(1, "run-fixture", "0123456789abcdef0123456789abcdef", "A1-B2", "S-1-5-21-1-2-3-1001", "opaque", variant), Protocol.Json)));
        Check("reject arbitrary variant", () => Reject(() => Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new Request(1, "run-fixture", "0123456789abcdef0123456789abcdef", "A1-B2", "S-1-5-21-1-2-3-1001", "opaque", "& cmd"), Protocol.Json))));
        Check("missing experimental APIs guide updating without promising availability", () =>
        {
            var result = Platform.Classify("Arm64", "Windows test", ["wtsapi32!EXPERIMENTAL_WTSCreateHeadlessSession"], []);
            if (result.Ready || result.Code != "experimental_apis_missing" || !result.Message.Contains("latest available Windows updates")
                || !result.Message.Contains("not a guarantee")) throw new Exception("Missing-API guidance incorrect");
        });
        Check("uninspected private credential ABI still fails closed", () =>
        {
            var result = Platform.Classify("Arm64", "Windows newer", [], ["IsoSessionCore.dll/architecture"]);
            if (result.Ready || result.Code != "experimental_build_not_validated"
                || !result.Message.Contains("credential ABI")) throw new Exception("Unknown credential ABI accepted");
        });
        Check("unsupported architecture remains explicit", () =>
        {
            var result = Platform.Classify("X64", "Windows test", [], []);
            if (result.Ready || result.Code != "architecture_not_validated") throw new Exception("Architecture accepted without validation");
        });
        Check("available APIs permit testing without claiming validated Windows behavior", () =>
        {
            var result = Platform.Classify("Arm64", "Windows newer", [], []);
            if (!result.Ready || result.Code != "experimental_available"
                || !result.Message.Contains("not proof")) throw new Exception("Availability misrepresented as native validation");
        });
        Check("capture scheduling skips overdue slots rather than bursting after a stall", () =>
        {
            long due = CaptureSchedule.NextDue(0, 25, 1000);
            if (due != 1000) throw new Exception("Normal target cadence changed");
            due = CaptureSchedule.NextDue(due, 4200, 1000);
            if (due != 5000) throw new Exception("Overdue slots were replayed");
            if (CaptureSchedule.NextDue(due, 5020, 1000) != 6000)
                throw new Exception("Cadence did not recover without a burst");
            if (CaptureSchedule.NextDue(1000, 2000, 1000) != 2000)
                throw new Exception("An available target slot was unnecessarily skipped");
        });
        var members = new[] { "light", "dark", "dark-large" }.Select((variant, index) =>
            new GridMember(index.ToString("x32"), $"A{index}-B{index}", $"S-1-5-21-1-2-3-{1001 + index}", $"opaque-{index}", variant)).ToArray();
        Check("exactly three coordinated variants accepted", () =>
            Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(new Request(1, "run-grid", Members: members), Protocol.Json)));
        Check("unbounded grid rejected", () => Reject(() =>
            Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(new Request(1, "run-grid", Members: [.. members, members[0]]), Protocol.Json))));
        Check("duplicate agent identity rejected", () => Reject(() =>
            Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(new Request(1, "run-grid", Members: [members[0], members[1], members[2] with { AgentSid = members[0].AgentSid }]), Protocol.Json))));
        Check("grid with repeated theme rejected", () => Reject(() =>
            Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(new Request(1, "run-grid", Members: [members[0], members[1], members[2] with { Variant = "light" }]), Protocol.Json))));
        Check("nested duplicate JSON property rejected", () => Reject(() =>
            Parse("""{"protocol":1,"operation":"run-grid","members":[{"id":"a","id":"b"}]}""")));
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
        Check("failed recovery member does not block independent session cleanup", () =>
        {
            var group = members.Select(member => lease with { Request = member.AsRequest() }).ToArray();
            var attempted = new List<string>();
            var saved = new List<Lease>();
            var errors = Runtime.RecoverLeases(group, item =>
            {
                attempted.Add(item.Request.Id!);
                if (item.Request.Id == group[0].Request.Id) throw new IOException("Synthetic stop failure");
                return item;
            }, saved.Add);
            if (attempted.Count != 3 || saved.Count != 2 || saved.Any(item => item.Phase != "finished") || errors.Length != 1)
                throw new Exception("Independent recovery was not attempted for every member");
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
        FileCase("100 immutable publications succeed while prior snapshots are held without delete sharing", root =>
        {
            var readers = new List<FileStream>();
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    string path = Path.Combine(root, $"snapshot-{i:D3}.json");
                    EvidencePublication.PublishNew(path, stream => JsonSerializer.Serialize(stream, new { sequence = i }));
                    readers.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.GetProperty("sequence").GetInt32() != i) throw new Exception("Torn snapshot");
                }
                string first = Path.Combine(root, "snapshot-000.json");
                File.WriteAllText(first + ".tmp", "{}");
                try { File.Move(first + ".tmp", first, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
                throw new Exception("Negative replacement control did not reproduce a sharing refusal");
            }
            finally { foreach (var reader in readers) reader.Dispose(); }
        });
        FileCase("durable evidence rejects duplicates without changing prior bytes", root =>
        {
            string path = Path.Combine(root, "frame.json");
            EvidencePublication.PublishNew(path, stream => stream.Write([1, 2, 3]));
            try { EvidencePublication.PublishNew(path, stream => stream.Write([4, 5, 6])); }
            catch (IOException)
            {
                if (!File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 })
                    || Directory.GetFiles(root).Length != 1) throw new Exception("Prior evidence changed or temporary leaked");
                return;
            }
            throw new Exception("Duplicate publication succeeded");
        });
        FileCase("failed evidence serialization publishes nothing and leaves no temporary", root =>
        {
            try
            {
                EvidencePublication.PublishNew(Path.Combine(root, "frame.json"), stream =>
                {
                    stream.WriteByte(1);
                    throw new InvalidDataException("Synthetic serializer failure");
                });
            }
            catch (InvalidDataException)
            {
                if (Directory.GetFiles(root).Length != 0) throw new Exception("Partial evidence escaped");
                return;
            }
            throw new Exception("Failure was suppressed");
        });
        LifecycleTests.Register(Check);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, failures, nativeLifecycleInvoked = false }, Protocol.Json));
        return failures.Count == 0 ? 0 : 1;
    }
}
