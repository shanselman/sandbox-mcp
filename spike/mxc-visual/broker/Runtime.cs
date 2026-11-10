using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class Runtime
{
    public static object Run(Request request, Configuration config, CancellationToken cancel)
    {
        Platform.Check();
        string id = request.Id!;
        if (Directory.Exists(Paths.JobDirectory(id))) return Result(id);
        var outstanding = Paths.Leases();
        if (outstanding.Length >= 128 || outstanding.Any(lease => lease.Phase != "finished"))
            throw new InvalidOperationException("Run client recovery before creating another session");
        var actualSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, request.AgentName!).Translate(typeof(SecurityIdentifier));
        if (actualSid.Value != request.AgentSid || actualSid.Value == config.OwnerSid)
            throw new UnauthorizedAccessException("Agent account does not match the requested disposable SID");
        var before = Native.Sessions();
        foreach (var session in before.Where(session => session.Key != 0 && session.Value != 6))
        {
            var ownership = Native.ClassifySession(session.Key, request.AgentName!, request.AgentSid!);
            if (ownership == Native.Ownership.Owned)
                throw new InvalidOperationException("Agent already has a session; a fresh provisioned account is required");
            if (ownership == Native.Ownership.Unknown)
                throw new InvalidOperationException("A session is initializing without an attributable user; wait before creating a Full session");
        }

        var report = new Dictionary<string, object?> { ["id"] = id, ["created"] = false, ["started"] = DateTimeOffset.UtcNow };
        var lease = new Lease(request, Guid.NewGuid().ToString("N"), before.Keys.ToArray(), null, "creating", DateTimeOffset.UtcNow);
        bool journaled = false;
        try
        {
            using (var credential = LocalAgentCredential.Acquire(request.AgentName!, report, new SecurityIdentifier(config.OwnerSid)))
            {
                LocalAgentCredential.VerifyOwnerBinding(request.AgentName!, new SecurityIdentifier(config.OwnerSid), actualSid);
                cancel.ThrowIfCancellationRequested();
                Paths.SaveLease(lease);
                journaled = true;
                var parameters = new Native.HeadlessParameters
                {
                    Size = checked((uint)(Marshal.OffsetOf<Native.HeadlessParameters>(nameof(Native.HeadlessParameters.Parent)).ToInt32() + 4)),
                    Kind = 1, Timeout = 20000, Identity = credential.Buffer, IdentityBytes = credential.Length,
                    InitialProgram = IntPtr.Zero, Parent = uint.MaxValue - 1
                };
                var timer = Stopwatch.StartNew();
                bool created = Native.EXPERIMENTAL_WTSCreateHeadlessSession(ref parameters, out int sessionId);
                int error = Marshal.GetLastWin32Error();
                report["created"] = created;
                report["createMs"] = timer.ElapsedMilliseconds;
                if (!created) throw new System.ComponentModel.Win32Exception(error, "Full session creation");
                if (sessionId <= 0 || before.ContainsKey(sessionId) || sessionId == Native.WTSGetActiveConsoleSessionId())
                    throw new InvalidOperationException("Refusing an existing/console session ID");
                lease = lease with { SessionId = sessionId, Phase = "running" };
                Paths.SaveLease(lease);
            }
            cancel.ThrowIfCancellationRequested();
            if (!Native.IsOwnedSession(lease.SessionId!.Value, request.AgentName!, request.AgentSid!))
                throw new UnauthorizedAccessException("Created session ownership could not be verified");
            report["sessionId"] = lease.SessionId;
            report["outputKey"] = lease.OutputKey;
            using var authorizationProbe = Native.Pipe(config.OwnerSid, true);
            report["workerExitCode"] = Native.RunWorker(lease.SessionId.Value, request.AgentSid!, lease.OutputKey, cancel);
            report["success"] = (uint)report["workerExitCode"]! == 0;
        }
        catch (Exception error)
        {
            report["success"] = false;
            report["error"] = error.Message;
        }
        finally
        {
            if (journaled)
            {
                try
                {
                    Cleanup(lease);
                    report["sessionCleanupComplete"] = true;
                    lease = lease with { Phase = "finished" };
                }
                catch (Exception error)
                {
                    report["success"] = false;
                    report["sessionCleanupComplete"] = false;
                    report["cleanupError"] = error.Message;
                    lease = lease with { Phase = "recovery-required" };
                }
                lease = lease with { Result = JsonSerializer.SerializeToElement(report, Protocol.Json) };
                Paths.SaveLease(lease);
            }
        }
        if (!journaled) report["sessionCleanupComplete"] = true;
        return new { ok = true, state = journaled ? lease.Phase : "rejected", report };
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

    public static object Recover()
    {
        foreach (var lease in Paths.Leases())
        {
            if (lease.Phase == "finished") continue;
            Cleanup(lease);
            var result = JsonSerializer.SerializeToElement(new
            {
                success = false, sessionCleanupComplete = true,
                error = "Interrupted run recovered; the client must preserve evidence and deprovision its account",
                outputKey = lease.OutputKey
            }, Protocol.Json);
            Paths.SaveLease(lease with { Phase = "finished", Result = result });
        }
        return new { ok = true, pending = Summaries() };
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
