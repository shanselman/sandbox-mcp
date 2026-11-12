using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class Runtime
{
    public static object Run(Request request, Configuration config, CancellationToken cancel)
    {
        if (request.Operation == "run-grid")
        {
            var results = RunGroup(request.Members!.Select(member => member.AsRequest()).ToArray(), config, cancel,
                new WindowsSessionBackend(), StartupPacing.DelayMs(request.StartupPacing));
            return new { ok = true, results };
        }
        if (Directory.Exists(Paths.JobDirectory(request.Id!))) return Result(request.Id!);
        return RunGroup([request], config, cancel, new WindowsSessionBackend())[0];
    }

    private sealed class Work(Request request, int[] baseline)
    {
        public Request Request { get; } = request;
        public Lease Lease { get; set; } = new(request, Guid.NewGuid().ToString("N"), baseline, null, "creating", DateTimeOffset.UtcNow);
        public bool Journaled { get; set; }
        public Dictionary<string, object?> Report { get; } = new() { ["id"] = request.Id, ["created"] = false, ["started"] = DateTimeOffset.UtcNow };
    }

    internal static object[] RunGroup(Request[] requests, Configuration config, CancellationToken cancel, ISessionBackend backend,
        int creationDelayMs = 0)
    {
        StartupPacing.ValidateDelay(creationDelayMs);
        backend.CheckPlatform();
        if (requests.Length is < 1 or > VisualWorkloads.MaxSessions) throw new InvalidDataException("Unsupported group size");
        if (requests.Any(request => backend.JournalExists(request.Id!)))
            throw new InvalidOperationException("A grid member already has a journal; recover instead of repeating creation");
        var outstanding = backend.Leases();
        if (outstanding.Length + requests.Length > 128 || outstanding.Any(lease => lease.Phase != "finished"))
            throw new InvalidOperationException("Run client recovery before creating another session");
        var before = backend.Sessions();
        foreach (var request in requests)
        {
            backend.ValidateAccount(request, config.OwnerSid);
            foreach (var session in before.Where(session => session.Key != 0 && session.Value != 6))
            {
                var ownership = backend.Classify(session.Key, request);
                if (ownership == Native.Ownership.Owned)
                    throw new InvalidOperationException("Agent already has a session; a fresh provisioned account is required");
                if (ownership == Native.Ownership.Unknown)
                    throw new InvalidOperationException("A session is initializing without an attributable user; wait before creating a Full session");
            }
        }
        var group = requests.Select(request => new Work(request, before.Keys.ToArray())).ToArray();
        try
        {
            foreach (var work in group)
            {
                var request = work.Request;
                cancel.ThrowIfCancellationRequested();
                using var credential = backend.PrepareCredential(request, config, work.Report);
                cancel.ThrowIfCancellationRequested();
                work.Lease = work.Lease with { Created = backend.Now };
                backend.Save(work.Lease);
                work.Journaled = true;
                var timer = Stopwatch.StartNew();
                work.Report["createBeginAt"] = backend.Now;
                int sessionId = backend.CreateSession(credential);
                work.Report["created"] = true;
                work.Report["createMs"] = timer.ElapsedMilliseconds;
                if (sessionId <= 0 || before.ContainsKey(sessionId) || sessionId == backend.ConsoleSessionId
                    || group.Any(other => other.Lease.SessionId == sessionId))
                    throw new InvalidOperationException("Refusing an existing/console session ID");
                work.Lease = work.Lease with { SessionId = sessionId, Phase = "running" };
                backend.Save(work.Lease);
                if (backend.Classify(sessionId, request) != Native.Ownership.Owned)
                    throw new UnauthorizedAccessException("Created session ownership could not be verified");
                work.Report["sessionId"] = sessionId;
                work.Report["outputKey"] = work.Lease.OutputKey;
                work.Report["variant"] = request.Variant ?? "baseline";
                work.Report["createdAt"] = backend.Now;
                work.Report["creationPacingMs"] = creationDelayMs;
                if (creationDelayMs > 0 && work != group[^1])
                {
                    work.Report["creationSettleStartedAt"] = backend.Now;
                    for (int remaining = creationDelayMs; remaining > 0; remaining -= 250)
                    {
                        cancel.ThrowIfCancellationRequested();
                        backend.Delay(Math.Min(250, remaining));
                    }
                    cancel.ThrowIfCancellationRequested();
                    work.Report["creationSettleFinishedAt"] = backend.Now;
                }
            }
            cancel.ThrowIfCancellationRequested();
            var active = backend.Sessions();
            if (group.Any(work => !active.ContainsKey(work.Lease.SessionId!.Value)))
                throw new InvalidOperationException("Not all owned sessions survived until coordinated worker launch");
            foreach (var work in group)
            {
                work.Report["coordinatedSessionIds"] = group.Select(member => member.Lease.SessionId!.Value).ToArray();
                work.Report["allSessionsPresentAtWorkerLaunch"] = true;
            }
            using var authorizationProbe = backend.AuthorizationProbe(config.OwnerSid);
            using var workers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var captureStart = new ManualResetEventSlim();
            var tasks = new List<Task>();
            try
            {
                foreach (var work in group)
                {
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    tasks.Add(Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            work.Report["workerStartedAt"] = DateTimeOffset.UtcNow;
                            uint code = backend.RunWorker(work.Lease, () =>
                            {
                                work.Report["workerReadyAt"] = DateTimeOffset.UtcNow;
                                ready.TrySetResult();
                            }, captureStart, workers.Token);
                            work.Report["workerExitCode"] = code;
                            work.Report["success"] = code == 0;
                            if (code != 0) workers.Cancel();
                        }
                        catch (Exception error)
                        {
                            work.Report["success"] = false;
                            work.Report["error"] = error.Message;
                            ready.TrySetException(error);
                            workers.Cancel();
                        }
                        finally
                        {
                            ready.TrySetException(new IOException("Worker ended before readiness"));
                            work.Report["workerFinishedAt"] = DateTimeOffset.UtcNow;
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                    ready.Task.WaitAsync(TimeSpan.FromMilliseconds(CaptureRunLimits.StartupMs + 5000), workers.Token).GetAwaiter().GetResult();
                }
                workers.Token.ThrowIfCancellationRequested();
                DateTimeOffset released = DateTimeOffset.UtcNow;
                foreach (var work in group) work.Report["captureReleasedAt"] = released;
                captureStart.Set();
            }
            finally
            {
                if (!captureStart.IsSet) workers.Cancel();
                Task.WaitAll(tasks.ToArray());
            }
        }
        catch (Exception error)
        {
            foreach (var work in group)
            {
                work.Report["success"] = false;
                if (!work.Report.ContainsKey("error")) work.Report["error"] = error.Message;
            }
        }
        finally
        {
            foreach (var work in group)
            {
                if (!work.Journaled) { work.Report["sessionCleanupComplete"] = true; continue; }
                try
                {
                    work.Lease = Cleanup(work.Lease, backend, checkpoint => work.Lease = checkpoint);
                    work.Report["sessionCleanupComplete"] = true;
                    work.Lease = work.Lease with { Phase = "finished" };
                }
                catch (Exception error)
                {
                    work.Report["success"] = false;
                    work.Report["sessionCleanupComplete"] = false;
                    work.Report["cleanupError"] = error.Message;
                    work.Lease = work.Lease with { Phase = "recovery-required" };
                }
                try
                {
                    work.Lease = work.Lease with { Result = JsonSerializer.SerializeToElement(work.Report, Protocol.Json) };
                    backend.Save(work.Lease);
                }
                catch (Exception error)
                {
                    work.Report["success"] = false;
                    work.Report["journalError"] = error.Message;
                    // Continue cleaning the remaining owned sessions.
                }
            }
        }
        return group.Select(work => (object)new { ok = true, state = work.Journaled ? work.Lease.Phase : "rejected", report = work.Report }).ToArray();
    }

    internal static Lease Cleanup(Lease lease, ISessionBackend backend, Action<Lease>? checkpoint = null)
    {
        if (lease.SessionId is null && lease.CleanupSessionId is null)
        {
            // A crash can precede the create result. Allow the requested native
            // logon timeout plus a grace period before the final ownership scan.
            int remaining = (int)Math.Clamp((lease.Created.AddSeconds(30) - backend.Now).TotalMilliseconds, 0, 30000);
            backend.Delay(remaining);
        }
        var observed = new Dictionary<int, Native.Ownership>();
        foreach (var session in backend.Sessions().Where(session => session.Key != 0 && session.Value != 6))
        {
            try { observed[session.Key] = backend.Classify(session.Key, lease.Request); }
            catch (System.ComponentModel.Win32Exception)
            {
                if (backend.Sessions().ContainsKey(session.Key)) throw;
            }
        }
        var plan = PlanCleanup(lease, observed);
        foreach (int sessionId in plan.Owned)
        {
            if (backend.Classify(sessionId, lease.Request) != Native.Ownership.Owned)
                throw new UnauthorizedAccessException("Session ownership changed before teardown");
            // Commit the exact impending stop before entering the experimental
            // call; a kernel crash must not erase the only lifecycle evidence.
            lease = lease with { Phase = "stopping", CleanupSessionId = sessionId, CleanupStartedAt = backend.Now };
            backend.Save(lease);
            checkpoint?.Invoke(lease);
            backend.StopSession(sessionId);
            var started = backend.Now;
            while (backend.Sessions().ContainsKey(sessionId))
            {
                if (backend.Now - started > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned session has not disappeared");
                backend.Delay(200);
            }
        }
        if (plan.Uncertain.Length != 0) throw new InvalidOperationException("Unattributed sessions remain after interrupted creation; refusing to claim cleanup");
        return lease;
    }

    internal static (int[] Owned, int[] Uncertain) PlanCleanup(Lease lease, IReadOnlyDictionary<int, Native.Ownership> observed)
    {
        var baseline = lease.Baseline.ToHashSet();
        if (lease.SessionId is not null && lease.CleanupSessionId is not null && lease.SessionId != lease.CleanupSessionId)
            throw new InvalidOperationException("Creation and cleanup session identities disagree");
        int? target = lease.SessionId ?? lease.CleanupSessionId;
        if (observed.Any(pair => pair.Value == Native.Ownership.Owned && baseline.Contains(pair.Key)))
            throw new InvalidOperationException("Owned identity collided with a baseline session ID; reconciliation required");
        if (target is null && observed.Count(pair => pair.Value == Native.Ownership.Owned) > 1)
            return ([], observed.Where(pair => pair.Value is Native.Ownership.Owned or Native.Ownership.Unknown).Select(pair => pair.Key).ToArray());
        return (
            observed.Where(pair => pair.Value == Native.Ownership.Owned && !baseline.Contains(pair.Key)
                && (target is null || target == pair.Key)).Select(pair => pair.Key).ToArray(),
            observed.Where(pair => pair.Value == Native.Ownership.Unknown
                && (pair.Key == target || target is null && !baseline.Contains(pair.Key))
                || pair.Value == Native.Ownership.Owned && target is not null && pair.Key != target).Select(pair => pair.Key).ToArray());
    }

    internal sealed record RecoveryResult(bool Ok, bool Complete, string[] Errors, object[] Pending);

    public static RecoveryResult Recover()
    {
        var backend = new WindowsSessionBackend();
        string[] failures = RecoverLeases(Paths.Leases(), lease => Cleanup(lease, backend), Paths.SaveLease);
        return new(true, failures.Length == 0, failures, Summaries());
    }

    internal static string[] RecoverLeases(IEnumerable<Lease> leases, Func<Lease, Lease> cleanup, Action<Lease> save)
    {
        var failures = new List<string>();
        foreach (var lease in leases)
        {
            if (lease.Phase == "finished") continue;
            try
            {
                var cleaned = cleanup(lease);
                var result = JsonSerializer.SerializeToElement(new
                {
                    success = false, sessionCleanupComplete = true,
                    error = "Owned sessions are absent after recovery; this does not prove pre-interruption teardown succeeded",
                    preInterruptionTeardownConfirmed = false,
                    previousResult = lease.Result,
                    outputKey = lease.OutputKey
                }, Protocol.Json);
                save(cleaned with { Phase = "finished", Result = result });
            }
            catch (Exception error) { failures.Add($"{lease.Request.Id}: {error.Message}"); }
        }
        return failures.ToArray();
    }

    public static object[] Summaries() => Paths.Leases().Select(lease => (object)new
    {
        id = lease.Request.Id, lease.Request.AgentName, lease.Request.AgentSid,
        lease.OutputKey, lease.Phase
    }).ToArray();
    public static object Result(string id)
    {
        var lease = Paths.ReadLease(id);
        return new { ok = true, state = lease.Phase, request = lease.Request, outputKey = lease.OutputKey, report = lease.Result };
    }
    public static object Ack(string id)
    {
        var lease = Paths.ReadLease(id);
        if (lease.Phase != "finished") throw new InvalidOperationException("Session recovery is not complete");
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, lease.Request.AgentName!).Translate(typeof(SecurityIdentifier));
            if (sid.Value == lease.Request.AgentSid) throw new InvalidOperationException("Client account cleanup is still pending");
        }
        catch (IdentityNotMappedException) { /* The owned account no longer exists. */ }
        Paths.AcknowledgeJournal(id);
        return new { ok = true, acknowledged = id };
    }
}
