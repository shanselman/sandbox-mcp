using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class Runtime
{
    public static object Run(Request request, Configuration config, CancellationToken cancel)
    {
        if (request.Operation == "run-grid")
        {
            var results = RunGroup(request.Members!.Select(member => member.AsRequest()).ToArray(), config, cancel);
            return new { ok = true, results };
        }
        if (Directory.Exists(Paths.JobDirectory(request.Id!))) return Result(request.Id!);
        return RunGroup([request], config, cancel)[0];
    }

    private sealed class Work(Request request, int[] baseline)
    {
        public Request Request { get; } = request;
        public Lease Lease { get; set; } = new(request, Guid.NewGuid().ToString("N"), baseline, null, "creating", DateTimeOffset.UtcNow);
        public bool Journaled { get; set; }
        public Dictionary<string, object?> Report { get; } = new() { ["id"] = request.Id, ["created"] = false, ["started"] = DateTimeOffset.UtcNow };
    }

    private static object[] RunGroup(Request[] requests, Configuration config, CancellationToken cancel)
    {
        Platform.Check();
        if (requests.Length is not (1 or 3)) throw new InvalidDataException("Unsupported group size");
        if (requests.Any(request => Directory.Exists(Paths.JobDirectory(request.Id!))))
            throw new InvalidOperationException("A grid member already has a journal; recover instead of repeating creation");
        var outstanding = Paths.Leases();
        if (outstanding.Length + requests.Length > 128 || outstanding.Any(lease => lease.Phase != "finished"))
            throw new InvalidOperationException("Run client recovery before creating another session");
        var before = Native.Sessions();
        foreach (var request in requests)
        {
            var actualSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, request.AgentName!).Translate(typeof(SecurityIdentifier));
            if (actualSid.Value != request.AgentSid || actualSid.Value == config.OwnerSid)
                throw new UnauthorizedAccessException("Agent account does not match the requested disposable SID");
            foreach (var session in before.Where(session => session.Key != 0 && session.Value != 6))
            {
                var ownership = Native.ClassifySession(session.Key, request.AgentName!, request.AgentSid!);
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
                using var credential = LocalAgentCredential.Acquire(request.AgentName!, work.Report, new SecurityIdentifier(config.OwnerSid));
                LocalAgentCredential.VerifyOwnerBinding(request.AgentName!, new SecurityIdentifier(config.OwnerSid), new SecurityIdentifier(request.AgentSid!));
                cancel.ThrowIfCancellationRequested();
                work.Lease = work.Lease with { Created = DateTimeOffset.UtcNow };
                Paths.SaveLease(work.Lease);
                work.Journaled = true;
                var parameters = new Native.HeadlessParameters
                {
                    Size = checked((uint)(Marshal.OffsetOf<Native.HeadlessParameters>(nameof(Native.HeadlessParameters.Parent)).ToInt32() + 4)),
                    Kind = 1, Timeout = 20000, Identity = credential.Buffer, IdentityBytes = credential.Length,
                    InitialProgram = IntPtr.Zero, Parent = uint.MaxValue - 1
                };
                var timer = Stopwatch.StartNew();
                bool created = Native.EXPERIMENTAL_WTSCreateHeadlessSession(ref parameters, out int sessionId);
                int error = Marshal.GetLastWin32Error();
                work.Report["created"] = created;
                work.Report["createMs"] = timer.ElapsedMilliseconds;
                if (!created) throw new System.ComponentModel.Win32Exception(error, "Full session creation");
                if (sessionId <= 0 || before.ContainsKey(sessionId) || sessionId == Native.WTSGetActiveConsoleSessionId()
                    || group.Any(other => other.Lease.SessionId == sessionId))
                    throw new InvalidOperationException("Refusing an existing/console session ID");
                work.Lease = work.Lease with { SessionId = sessionId, Phase = "running" };
                Paths.SaveLease(work.Lease);
                if (!Native.IsOwnedSession(sessionId, request.AgentName!, request.AgentSid!))
                    throw new UnauthorizedAccessException("Created session ownership could not be verified");
                work.Report["sessionId"] = sessionId;
                work.Report["outputKey"] = work.Lease.OutputKey;
                work.Report["variant"] = request.Variant ?? "baseline";
            }
            cancel.ThrowIfCancellationRequested();
            var active = Native.Sessions();
            if (group.Any(work => !active.ContainsKey(work.Lease.SessionId!.Value)))
                throw new InvalidOperationException("Not all owned sessions survived until coordinated worker launch");
            foreach (var work in group)
            {
                work.Report["coordinatedSessionIds"] = group.Select(member => member.Lease.SessionId!.Value).ToArray();
                work.Report["allSessionsPresentAtWorkerLaunch"] = true;
            }
            using var authorizationProbe = Native.Pipe(config.OwnerSid, true);
            using var workers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Task.WaitAll(group.Select(work => Task.Run(() =>
            {
                try
                {
                    work.Report["workerStartedAt"] = DateTimeOffset.UtcNow;
                    uint code = Native.RunWorker(work.Lease.SessionId!.Value, work.Request.AgentSid!, work.Lease.OutputKey, work.Request.Variant, workers.Token);
                    work.Report["workerExitCode"] = code;
                    work.Report["success"] = code == 0;
                    if (code != 0) workers.Cancel();
                }
                catch (Exception error) { work.Report["success"] = false; work.Report["error"] = error.Message; workers.Cancel(); }
                finally { work.Report["workerFinishedAt"] = DateTimeOffset.UtcNow; }
            }, CancellationToken.None)).ToArray());
        }
        catch (Exception error)
        {
            foreach (var work in group) { work.Report["success"] = false; work.Report["error"] = error.Message; }
        }
        finally
        {
            foreach (var work in group)
            {
                if (!work.Journaled) { work.Report["sessionCleanupComplete"] = true; continue; }
                try
                {
                    Cleanup(work.Lease);
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
                    Paths.SaveLease(work.Lease);
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

    private static void Cleanup(Lease lease)
    {
        if (lease.SessionId is null)
        {
            // A crash can precede the create result. Allow the requested native
            // logon timeout plus a grace period before the final ownership scan.
            int remaining = (int)Math.Clamp((lease.Created.AddSeconds(30) - DateTimeOffset.UtcNow).TotalMilliseconds, 0, 30000);
            Thread.Sleep(remaining);
        }
        var observed = new Dictionary<int, Native.Ownership>();
        foreach (var session in Native.Sessions().Where(session => session.Key != 0 && session.Value != 6))
        {
            try { observed[session.Key] = Native.ClassifySession(session.Key, lease.Request.AgentName!, lease.Request.AgentSid!); }
            catch (System.ComponentModel.Win32Exception)
            {
                if (Native.Sessions().ContainsKey(session.Key)) throw;
            }
        }
        var plan = PlanCleanup(lease, observed);
        foreach (int sessionId in plan.Owned)
        {
            if (!Native.IsOwnedSession(sessionId, lease.Request.AgentName!, lease.Request.AgentSid!))
                throw new UnauthorizedAccessException("Session ownership changed before teardown");
            if (!Native.EXPERIMENTAL_WTSStopHeadlessSession(sessionId)) throw Native.Error("Stop owned Full session");
            var timer = Stopwatch.StartNew();
            while (Native.Sessions().ContainsKey(sessionId))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned session has not disappeared");
                Thread.Sleep(200);
            }
        }
        if (plan.Uncertain.Length != 0) throw new InvalidOperationException("Unattributed sessions remain after interrupted creation; refusing to claim cleanup");
    }

    internal static (int[] Owned, int[] Uncertain) PlanCleanup(Lease lease, IReadOnlyDictionary<int, Native.Ownership> observed)
    {
        var baseline = lease.Baseline.ToHashSet();
        if (observed.Any(pair => pair.Value == Native.Ownership.Owned && baseline.Contains(pair.Key)))
            throw new InvalidOperationException("Owned identity collided with a baseline session ID; reconciliation required");
        return (
            observed.Where(pair => pair.Value == Native.Ownership.Owned && !baseline.Contains(pair.Key)).Select(pair => pair.Key).ToArray(),
            observed.Where(pair => pair.Value == Native.Ownership.Unknown
                && (pair.Key == lease.SessionId || lease.SessionId is null && !baseline.Contains(pair.Key))).Select(pair => pair.Key).ToArray());
    }

    internal sealed record RecoveryResult(bool Ok, bool Complete, string[] Errors, object[] Pending);

    public static RecoveryResult Recover()
    {
        string[] failures = RecoverLeases(Paths.Leases(), Cleanup, Paths.SaveLease);
        return new(true, failures.Length == 0, failures, Summaries());
    }

    internal static string[] RecoverLeases(IEnumerable<Lease> leases, Action<Lease> cleanup, Action<Lease> save)
    {
        var failures = new List<string>();
        foreach (var lease in leases)
        {
            if (lease.Phase == "finished") continue;
            try
            {
                cleanup(lease);
                var result = JsonSerializer.SerializeToElement(new
                {
                    success = false, sessionCleanupComplete = true,
                    error = "Interrupted run recovered; the client must preserve evidence and deprovision its account",
                    outputKey = lease.OutputKey
                }, Protocol.Json);
                save(lease with { Phase = "finished", Result = result });
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
