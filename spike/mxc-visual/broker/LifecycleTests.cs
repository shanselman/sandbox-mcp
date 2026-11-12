using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class LifecycleTests
{
    private static readonly Configuration Config = new(1, "S-1-5-21-1-2-3-1000", new string('A', 64));
    private static Request[] Requests => new[] { "light", "dark", "dark-large" }.Select((variant, i) =>
        new Request(1, "run-fixture", i.ToString("x32"), $"A{i}-B{i}", $"S-1-5-21-1-2-3-{1001 + i}", $"opaque{i}", variant)).ToArray();
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException) { return; }
        throw new Exception("Expected fail-closed refusal");
    }
    private static JsonElement[] Run(Fake backend, CancellationToken token = default) =>
        Runtime.RunGroup(Requests, Config, token, backend).Select(value => JsonSerializer.SerializeToElement(value, Protocol.Json)).ToArray();

    public static void Register(Action<string, Action> check)
    {
        foreach (bool workerFailure in new[] { false, true })
        foreach (Exception failure in new Exception[] { new Win32Exception(1450, "Full session creation"), new IOException("Synthetic failure") })
            check($"offline failure diagnostics: {(workerFailure ? "worker" : "group")} {failure.GetType().Name}", () =>
            {
                failure.Data["private-context"] = "synthetic-private-value";
                var backend = workerFailure
                    ? new Fake { PreparationFailure = 11, PreparationError = failure }
                    : new Fake { AfterCreate = _ => throw failure };
                var results = Run(backend);
                var reports = workerFailure ? new[] { results[1].GetProperty("report") }
                    : results.Select(result => result.GetProperty("report")).ToArray();
                foreach (var report in reports)
                {
                    Assert(report.GetProperty("error").GetString() == failure.Message, "Original failure message was replaced");
                    Assert(report.GetProperty("errorType").GetString() == failure.GetType().FullName, "Exception type lost");
                    Assert(report.GetProperty("errorHResult").GetInt32() == failure.HResult, "Exception HRESULT lost");
                    if (failure is Win32Exception native)
                        Assert(report.GetProperty("nativeErrorCode").GetInt32() == native.NativeErrorCode, "Native error code lost");
                    else Assert(!report.TryGetProperty("nativeErrorCode", out _), "Non-native failure invented a native code");
                    Assert(!report.GetRawText().Contains("synthetic-private-value"), "Private exception data leaked");
                    Assert(!report.GetProperty("success").GetBoolean() && report.GetProperty("sessionCleanupComplete").GetBoolean(),
                        "Failure or cleanup verdict changed");
                }
                var persisted = backend.Journals[Requests[workerFailure ? 1 : 0].Id!].Result!.Value;
                Assert(persisted.GetProperty("errorType").GetString() == failure.GetType().FullName
                    && persisted.GetProperty("errorHResult").GetInt32() == failure.HResult, "Failure metadata was not persisted");
                if (failure is Win32Exception)
                    Assert(persisted.GetProperty("nativeErrorCode").GetInt32() == 1450, "Persisted native code lost");
                Assert(backend.Owners.Count == 0 && backend.Stops.Count == (workerFailure ? 3 : 1)
                    && backend.CredentialsCreated == backend.CredentialsDisposed, "Failure diagnostics changed resource cleanup");
            });
        foreach (var (mode, delay) in new[] { ("immediate", 0), ("settle-2s", 2000), ("settle-5s", 5000), ("settle-6s", 6000), ("settle-15s", 15000) })
        check($"fixed creation pacing {mode} occurs only between sessions before any worker", () =>
        {
            var backend = new Fake();
            var results = Runtime.RunGroup(Requests, Config, CancellationToken.None, backend, StartupPacing.DelayMs(mode))
                .Select(value => JsonSerializer.SerializeToElement(value, Protocol.Json)).ToArray();
            Assert(backend.DelayedMs == delay * 2, "Expected exactly two fixed gaps, not a trailing delay");
            Assert(results.All(result => result.GetProperty("report").GetProperty("success").GetBoolean()), "Paced workers failed");
            Assert(results[1].GetProperty("report").GetProperty("createdAt").GetDateTimeOffset()
                - results[0].GetProperty("report").GetProperty("createdAt").GetDateTimeOffset() == TimeSpan.FromMilliseconds(delay), "Wrong pacing interval");
            Assert(results.All(result => result.GetProperty("report").GetProperty("createBeginAt").GetDateTimeOffset()
                <= result.GetProperty("report").GetProperty("createdAt").GetDateTimeOffset()), "Missing native create start");
            Assert(backend.Prepared.SequenceEqual([10, 11, 12]) && backend.Owners.Count == 0, "Warm-up/cleanup changed");
        });
        check("unsupported creation delay is refused before platform or identity work", () =>
        {
            var backend = new Fake { PlatformFailure = true };
            try { Runtime.RunGroup(Requests, Config, CancellationToken.None, backend, 3000); }
            catch (ArgumentOutOfRangeException) { return; }
            throw new Exception("Arbitrary creation delay accepted");
        });
        foreach (int delay in new[] { 2000, 5000, 6000, 15000 })
        check($"cancellation during {delay}ms creation settling stops further creation and cleans the owned session", () =>
        {
            using var cancel = new CancellationTokenSource();
            var backend = new Fake { AfterDelay = () => cancel.Cancel() };
            Runtime.RunGroup(Requests, Config, cancel.Token, backend, delay);
            Assert(backend.Creates == 1 && backend.Workers.IsEmpty && backend.Owners.Count == 0, "Pacing cancellation leaked or continued");
        });
        check("offline lifecycle: all sessions exist before workers; credentials disposed; exact cleanup", () =>
        {
            var backend = new Fake();
            var results = Run(backend);
            Assert(results.All(result => result.GetProperty("report").GetProperty("success").GetBoolean()), "Workers failed");
            Assert(backend.Workers.Count == 3 && backend.Stops.SequenceEqual([10, 11, 12]), "Wrong workers/stop order");
            Assert(backend.CredentialsCreated == backend.CredentialsDisposed && backend.Owners.Count == 0, "Resource leak");
            Assert(backend.Journals.Values.All(lease => lease.Phase == "finished"), "Unfinished journals");
        });
        check("offline lifecycle: the single-session path preserves its existing behavior", () =>
        {
            var backend = new Fake(1);
            var result = Runtime.RunGroup([Requests[0]], Config, CancellationToken.None, backend);
            var report = JsonSerializer.SerializeToElement(result[0], Protocol.Json);
            Assert(report.GetProperty("report").GetProperty("success").GetBoolean(), "Single worker failed");
            Assert(backend.Creates == 1 && backend.Stops.SequenceEqual([10]) && backend.Owners.Count == 0, "Single cleanup changed");
        });
        foreach (int count in new[] { 4, 8, 16 })
            check($"offline lifecycle: {count} staged Calculator workers and exact cleanup", () =>
            {
                var backend = new Fake(count);
                var requests = Enumerable.Range(0, count).Select(index => Requests[0] with
                {
                    Id = index.ToString("x32"), AgentName = $"A{index}-B{index}",
                    AgentSid = $"S-1-5-21-1-2-3-{1001 + index}", ContainerId = $"opaque-{index}",
                    Variant = $"calculator-{index + 1:D2}"
                }).ToArray();
                var results = Runtime.RunGroup(requests, Config, CancellationToken.None, backend)
                    .Select(value => JsonSerializer.SerializeToElement(value, Protocol.Json)).ToArray();
                Assert(results.All(result => result.GetProperty("report").GetProperty("success").GetBoolean()), "Larger grid failed");
                Assert(backend.Prepared.SequenceEqual(Enumerable.Range(10, count)), "Preparation was not serial");
                Assert(backend.Stops.Count == count && backend.Owners.Count == 0, "Larger grid cleanup incomplete");
                Assert(results.Select(result => result.GetProperty("report").GetProperty("captureReleasedAt").GetDateTimeOffset())
                    .Distinct().Count() == 1, "Larger grid release was not shared");
            });
        foreach (int index in Enumerable.Range(0, 3))
        {
            check($"offline lifecycle: credential failure member {index}", () =>
            {
                var backend = new Fake { CredentialFailure = index };
                var results = Run(backend);
                Assert(backend.Creates == index && backend.Workers.IsEmpty, "Created/ran beyond credential failure");
                Assert(backend.Owners.Count == 0 && backend.Stops.Count == index, "Earlier members not cleaned");
                Assert(backend.CredentialsCreated == backend.CredentialsDisposed, "Credential leaked");
                Assert(results.All(result => !result.GetProperty("report").GetProperty("success").GetBoolean()), "False success");
            });
            check($"offline lifecycle: initial journal failure member {index} prevents native create", () =>
            {
                var backend = new Fake { SaveFailure = (lease, _) => lease.Request.Id == index.ToString("x32") && lease.Phase == "creating" };
                Run(backend);
                Assert(backend.Creates == index && backend.Workers.IsEmpty && backend.Owners.Count == 0, "Create preceded durable intent");
            });
            foreach (bool afterCreation in new[] { false, true })
                check($"offline lifecycle: native create failure member {index}, side effect {afterCreation}", () =>
                {
                    var backend = new Fake { CreateFailure = index, CreateBeforeThrow = afterCreation };
                    Run(backend);
                    Assert(backend.Owners.Count == 0 && backend.Workers.IsEmpty, "Failed create leaked/ran worker");
                    Assert(backend.CredentialsCreated == backend.CredentialsDisposed, "Credential leaked on create error");
                });
            check($"offline lifecycle: running journal failure member {index} still cleans returned session", () =>
            {
                var backend = new Fake { SaveFailure = (lease, _) => lease.Phase == "running" && lease.Request.Id == index.ToString("x32") };
                Run(backend);
                Assert(backend.Creates == index + 1 && backend.Stops.Count == index + 1 && backend.Owners.Count == 0, "Returned session was lost");
            });
            check($"offline lifecycle: stop failure member {index} does not strand others", () =>
            {
                var backend = new Fake { StopFailure = 10 + index };
                var results = Run(backend);
                Assert(backend.Stops.Count == 3 && backend.Owners.Count == 1, "Other members were not attempted");
                Assert(results[index].GetProperty("state").GetString() == "recovery-required", "Failed stop claimed clean");
                Assert(!results[index].GetProperty("report").GetProperty("sessionCleanupComplete").GetBoolean(), "False cleanup");
            });
            check($"offline lifecycle: final journal failure member {index} still cleans others", () =>
            {
                var backend = new Fake { SaveFailure = (lease, _) => lease.Phase == "finished" && lease.Request.Id == index.ToString("x32") };
                var results = Run(backend);
                Assert(backend.Owners.Count == 0 && backend.Stops.Count == 3, "Journal failure skipped sibling teardown");
                Assert(results[index].GetProperty("report").TryGetProperty("journalError", out _), "Journal failure hidden");
            });
        }
        check("offline lifecycle: canceled before any credential or session side effect", () =>
        {
            var backend = new Fake();
            Run(backend, new CancellationToken(true));
            Assert(backend.Creates == 0 && backend.CredentialsCreated == 0 && backend.Journals.Count == 0, "Canceled run had effects");
        });
        check("offline lifecycle: cancellation after first creation cleans only that session", () =>
        {
            using var cancel = new CancellationTokenSource();
            var backend = new Fake { AfterCreate = _ => cancel.Cancel() };
            Run(backend, cancel.Token);
            Assert(backend.Creates == 1 && backend.Owners.Count == 0 && backend.Workers.IsEmpty, "Canceled creation was not bounded");
        });
        check("offline lifecycle: worker failure cleans every member", () =>
        {
            var backend = new Fake { WorkerFailure = 11 };
            var results = Run(backend);
            Assert(backend.Owners.Count == 0 && backend.Stops.Count == 3, "Worker failure leaked a session");
            Assert(!results[1].GetProperty("report").GetProperty("success").GetBoolean(), "Failed worker marked successful");
        });
        check("offline lifecycle: capture waits for every serialized worker preparation", () =>
        {
            var backend = new Fake();
            var results = Run(backend);
            Assert(backend.Prepared.SequenceEqual([10, 11, 12]), "Preparation order changed");
            var release = results.Select(result => result.GetProperty("report").GetProperty("captureReleasedAt").GetDateTimeOffset()).Distinct().ToArray();
            Assert(release.Length == 1, "Workers did not share one release");
            Assert(results.All(result => result.GetProperty("report").GetProperty("workerReadyAt").GetDateTimeOffset() <= release[0]), "Capture preceded readiness");
        });
        check("offline lifecycle: failed preparation never releases capture or starts later workers", () =>
        {
            var backend = new Fake { PreparationFailure = 11 };
            var results = Run(backend);
            Assert(backend.Workers.Count == 2 && backend.Prepared.SequenceEqual([10]), "Work continued beyond failed preparation");
            Assert(backend.Owners.Count == 0 && backend.Stops.Count == 3, "Preparation failure leaked sessions");
            Assert(results.All(result => !result.GetProperty("report").TryGetProperty("captureReleasedAt", out _)), "Failure released capture");
        });
        check("offline lifecycle: no worker under mismatched session identity", () =>
        {
            var backend = new Fake { Classifier = (id, _, owned) => id == 10 ? Native.Ownership.Other : owned };
            Run(backend);
            Assert(backend.Workers.IsEmpty && backend.Stops.Count == 0, "Mismatched user launched/stopped");
        });
        foreach (int returned in new[] { 0, 1, -1 })
            check($"offline lifecycle: returned protected/invalid ID {returned}", () =>
            {
                var backend = new Fake { ReturnId = returned };
                Run(backend);
                Assert(backend.Workers.IsEmpty && backend.Stops.Count == 0, "Protected/invalid session touched");
            });
        check("offline cleanup: unknown identity never stopped or claimed absent", () =>
        {
            var backend = new Fake();
            backend.Owners[10] = Requests[0].AgentSid!;
            backend.Classifier = (_, _, _) => Native.Ownership.Unknown;
            Throws(() => Runtime.Cleanup(MakeLease(10), backend));
            Assert(backend.Stops.Count == 0, "Unknown identity stopped");
        });
        check("offline cleanup: identity changes between discovery and stop", () =>
        {
            var backend = new Fake(); int calls = 0;
            backend.Owners[10] = Requests[0].AgentSid!;
            backend.Classifier = (id, _, ownership) => id == 10 && ++calls > 1 ? Native.Ownership.Other : ownership;
            Throws(() => Runtime.Cleanup(MakeLease(10), backend));
            Assert(backend.Stops.Count == 0, "Recycled identity stopped");
        });
        check("offline cleanup: unexpected second session with same user is not implicitly owned", () =>
        {
            var backend = new Fake();
            backend.Owners[10] = backend.Owners[11] = Requests[0].AgentSid!;
            Throws(() => Runtime.Cleanup(MakeLease(10), backend));
            Assert(backend.Stops.SequenceEqual([10]) && backend.Owners.ContainsKey(11), "Stopped an unrecorded session");
        });
        check("offline cleanup: stop intent must persist before native call", () =>
        {
            var backend = new Fake { SaveFailure = (lease, _) => lease.Phase == "stopping" };
            backend.Owners[10] = Requests[0].AgentSid!;
            Throws(() => Runtime.Cleanup(MakeLease(10), backend));
            Assert(backend.Stops.Count == 0, "Stop occurred without durable intent");
        });
        check("offline recovery: a recorded stop target cannot adopt another same-account session", () =>
        {
            var backend = new Fake(); backend.Owners[11] = Requests[0].AgentSid!;
            var stopping = MakeLease(null) with { Phase = "stopping", CleanupSessionId = 10, CleanupStartedAt = backend.Now };
            Throws(() => Runtime.Cleanup(stopping, backend));
            Assert(backend.Stops.Count == 0 && backend.Owners.ContainsKey(11), "Recovery adopted an unrecorded session");
        });
        check("offline cleanup: ambiguous creation cannot adopt several same-account sessions", () =>
        {
            var backend = new Fake(); backend.Owners[10] = backend.Owners[11] = Requests[0].AgentSid!;
            Throws(() => Runtime.Cleanup(MakeLease(null), backend));
            Assert(backend.Stops.Count == 0, "Ambiguous resources were stopped");
        });
        check("offline recovery: successful unknown-create cleanup retains authoritative stop identity", () =>
        {
            var backend = new Fake(); backend.Owners[10] = Requests[0].AgentSid!;
            var lease = MakeLease(null);
            var errors = Runtime.RecoverLeases([lease], item => Runtime.Cleanup(item, backend), backend.Save);
            Assert(errors.Length == 0 && backend.Journals[lease.Request.Id!].CleanupSessionId == 10, "Stop identity lost after recovery");
        });
        check("offline lifecycle: failed unknown-create cleanup retains stop identity", () =>
        {
            var backend = new Fake { CreateFailure = 0, CreateBeforeThrow = true, StopFailure = 10 };
            Run(backend);
            var journal = backend.Journals[Requests[0].Id!];
            Assert(journal.SessionId is null && journal.CleanupSessionId == 10 && journal.Phase == "recovery-required", "Error update erased stop identity");
        });
        check("offline cleanup: stop success without disappearance is not success", () =>
        {
            var backend = new Fake { RetainStopped = true };
            backend.Owners[10] = Requests[0].AgentSid!;
            Throws(() => Runtime.Cleanup(MakeLease(10), backend));
            Assert(backend.Stops.Count == 1 && backend.DelayedMs >= 20000, "Wait/stop was unbounded or falsely successful");
        });
        check("offline recovery: reboot absence preserves uncertainty and previous error", () =>
        {
            var backend = new Fake();
            var old = MakeLease(10) with { Phase = "stopping", Result = JsonSerializer.SerializeToElement(new { error = "original failure" }) };
            var errors = Runtime.RecoverLeases([old], lease => Runtime.Cleanup(lease, backend), backend.Save);
            var recovered = backend.Journals[old.Request.Id!];
            Assert(errors.Length == 0 && backend.Stops.Count == 0, "Recovery stopped an absent session");
            Assert(recovered.Result!.Value.GetProperty("preInterruptionTeardownConfirmed").GetBoolean() == false, "Crash erased uncertainty");
            Assert(recovered.Result.Value.GetProperty("previousResult").GetProperty("error").GetString() == "original failure", "Original failure lost");
        });
        check("offline lifecycle: blocked platform never reaches account/native operations", () =>
        {
            var backend = new Fake { PlatformFailure = true };
            Throws(() => Run(backend));
            Assert(backend.Creates == 0 && backend.Validations == 0, "Platform failure permitted effects");
        });
        check("offline lifecycle: existing unfinished journal blocks new creation", () =>
        {
            var backend = new Fake();
            backend.Journals["existing"] = MakeLease(10);
            Throws(() => Run(backend));
            Assert(backend.Creates == 0, "Created with recovery pending");
        });
        check("offline cleanup: 1000 ownership permutations protect baseline and foreign sessions", () =>
        {
            var random = new Random(4187);
            for (int i = 0; i < 1000; i++)
            {
                var observed = Enumerable.Range(1, 25).ToDictionary(id => id, _ => (Native.Ownership)random.Next(3));
                observed[1] = Native.Ownership.Other;
                var lease = MakeLease(i % 2 == 0 ? 10 : null);
                var plan = Runtime.PlanCleanup(lease, observed);
                Assert(plan.Owned.All(id => !lease.Baseline.Contains(id) && observed[id] == Native.Ownership.Owned
                    && (lease.SessionId is null || lease.SessionId == id)), "Unsafe plan");
            }
        });
    }

    private static Lease MakeLease(int? session) => new(Requests[0], new string('a', 32), [0, 1], session, "running", DateTimeOffset.UnixEpoch);

    // No calls to Native, Paths, credentials, pipes, or real process APIs.
    private sealed class Fake(int expectedWorkers = 3) : ISessionBackend
    {
        public Dictionary<int, string> Owners { get; } = [];
        public Dictionary<string, Lease> Journals { get; } = [];
        public List<int> Stops { get; } = [];
        public ConcurrentBag<int> Workers { get; } = [];
        public int Creates, CredentialsCreated, CredentialsDisposed, Validations, DelayedMs;
        public int CredentialFailure = -1, CreateFailure = -1, StopFailure = -1, WorkerFailure = -1;
        public int PreparationFailure = -1;
        public Exception? PreparationError;
        public List<int> Prepared { get; } = [];
        public int? ReturnId;
        public bool CreateBeforeThrow, RetainStopped, PlatformFailure;
        public Action<int>? AfterCreate;
        public Action? AfterDelay;
        public Func<Lease, int, bool>? SaveFailure;
        public Func<int, Request, Native.Ownership, Native.Ownership>? Classifier;
        private int saves;
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        public uint ConsoleSessionId => 1;
        public void CheckPlatform() { if (PlatformFailure) throw new InvalidOperationException("Synthetic incompatible platform"); }
        public bool JournalExists(string id) => Journals.ContainsKey(id);
        public Lease[] Leases() => Journals.Values.ToArray();
        public Dictionary<int, int> Sessions() => new Dictionary<int, int>(Owners.Keys.Select(id => new KeyValuePair<int, int>(id, 0))) { [0] = 4, [1] = 0 };
        public void ValidateAccount(Request request, string ownerSid) { Validations++; }
        private sealed class Credential(Request request, Action dispose) : IDisposable
        {
            public Request Request { get; } = request;
            public void Dispose() => dispose();
        }
        public IDisposable PrepareCredential(Request request, Configuration config, Dictionary<string, object?> report)
        {
            if (request.Id == CredentialFailure.ToString("x32")) throw new UnauthorizedAccessException("Synthetic credential failure");
            CredentialsCreated++;
            return new Credential(request, () => CredentialsDisposed++);
        }
        public int CreateSession(IDisposable prepared)
        {
            var request = ((Credential)prepared).Request;
            Assert(Journals.TryGetValue(request.Id!, out var lease) && lease.Phase == "creating", "Creation preceded durable journal");
            int index = Creates++;
            int id = ReturnId ?? (10 + index);
            if (index == CreateFailure && !CreateBeforeThrow) throw new IOException("Synthetic create failure");
            if (ReturnId is null) Owners[id] = request.AgentSid!;
            AfterCreate?.Invoke(index);
            if (index == CreateFailure) throw new IOException("Synthetic failure after native side effect");
            return id;
        }
        public Native.Ownership Classify(int id, Request request)
        {
            if (id is 0 or 1) return Native.Ownership.Other;
            var ownership = Owners.TryGetValue(id, out string? sid) && sid == request.AgentSid ? Native.Ownership.Owned : Native.Ownership.Other;
            return Classifier?.Invoke(id, request, ownership) ?? ownership;
        }
        public void Save(Lease lease)
        {
            if (SaveFailure?.Invoke(lease, ++saves) == true) throw new IOException("Synthetic journal failure");
            // Snapshot, as on disk; do not share mutable result dictionaries.
            Journals[lease.Request.Id!] = JsonSerializer.Deserialize<Lease>(JsonSerializer.Serialize(lease, Protocol.Json), Protocol.Json)!;
        }
        private sealed class Resource : IDisposable { public void Dispose() { } }
        public IDisposable AuthorizationProbe(string ownerSid) => new Resource();
        public uint RunWorker(Lease lease, Action ready, ManualResetEventSlim captureStart, CancellationToken cancel)
        {
            Assert(Creates == expectedWorkers && Owners.Count == expectedWorkers, "Workers launched before coordinated creation");
            Assert(CredentialsCreated == CredentialsDisposed, "Credentials held during worker execution");
            Workers.Add(lease.SessionId!.Value);
            int index = lease.SessionId.Value - 10;
            Assert(Prepared.Count == index && !captureStart.IsSet, "Worker preparation was not serialized");
            if (lease.SessionId == PreparationFailure) throw PreparationError ?? new IOException("Synthetic worker preparation failure");
            Prepared.Add(lease.SessionId.Value);
            ready();
            captureStart.Wait(cancel);
            Assert(Prepared.Count == expectedWorkers, "Capture began before all workers were ready");
            cancel.ThrowIfCancellationRequested();
            return lease.SessionId == WorkerFailure ? 1u : 0u;
        }
        public void StopSession(int id)
        {
            Assert(Journals.Values.Any(lease => lease.Phase == "stopping" && lease.CleanupSessionId == id), "Stop intent missing");
            Stops.Add(id);
            if (id == StopFailure) throw new IOException("Synthetic stop failure");
            if (!RetainStopped) Owners.Remove(id);
        }
        public void Delay(int milliseconds) { DelayedMs += milliseconds; Now = Now.AddMilliseconds(milliseconds); AfterDelay?.Invoke(); }
    }
}
