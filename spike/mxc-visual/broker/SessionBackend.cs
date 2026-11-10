using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SandboxMcp.FullBroker;

// The only native lifecycle adapter. Offline tests supply an in-memory backend;
// no environment flag or IPC field can select a different implementation.
internal interface ISessionBackend
{
    DateTimeOffset Now { get; }
    uint ConsoleSessionId { get; }
    void CheckPlatform();
    bool JournalExists(string id);
    Lease[] Leases();
    Dictionary<int, int> Sessions();
    void ValidateAccount(Request request, string ownerSid);
    IDisposable PrepareCredential(Request request, Configuration config, Dictionary<string, object?> report);
    int CreateSession(IDisposable credential);
    Native.Ownership Classify(int sessionId, Request request);
    void Save(Lease lease);
    IDisposable AuthorizationProbe(string ownerSid);
    uint RunWorker(Lease lease, CancellationToken cancel);
    void StopSession(int sessionId);
    void Delay(int milliseconds);
}

internal sealed class WindowsSessionBackend : ISessionBackend
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public uint ConsoleSessionId => Native.WTSGetActiveConsoleSessionId();
    public void CheckPlatform() => Platform.Check();
    public bool JournalExists(string id) => Directory.Exists(Paths.JobDirectory(id));
    public Lease[] Leases() => Paths.Leases();
    public Dictionary<int, int> Sessions() => Native.Sessions();
    public void ValidateAccount(Request request, string ownerSid)
    {
        var actual = (SecurityIdentifier)new NTAccount(Environment.MachineName, request.AgentName!).Translate(typeof(SecurityIdentifier));
        if (actual.Value != request.AgentSid || actual.Value == ownerSid)
            throw new UnauthorizedAccessException("Agent account does not match the requested disposable SID");
    }
    public IDisposable PrepareCredential(Request request, Configuration config, Dictionary<string, object?> report)
    {
        var credential = LocalAgentCredential.Acquire(request.AgentName!, report, new SecurityIdentifier(config.OwnerSid));
        try
        {
            LocalAgentCredential.VerifyOwnerBinding(request.AgentName!, new SecurityIdentifier(config.OwnerSid), new SecurityIdentifier(request.AgentSid!));
            return credential;
        }
        catch { credential.Dispose(); throw; }
    }
    public int CreateSession(IDisposable prepared)
    {
        if (prepared is not LocalAgentCredential credential) throw new ArgumentException("Expected a native agent credential");
        var parameters = new Native.HeadlessParameters
        {
            Size = checked((uint)(Marshal.OffsetOf<Native.HeadlessParameters>(nameof(Native.HeadlessParameters.Parent)).ToInt32() + 4)),
            Kind = 1, Timeout = 20000, Identity = credential.Buffer, IdentityBytes = credential.Length,
            InitialProgram = IntPtr.Zero, Parent = uint.MaxValue - 1
        };
        if (!Native.EXPERIMENTAL_WTSCreateHeadlessSession(ref parameters, out int sessionId))
            throw Native.Error("Full session creation");
        return sessionId;
    }
    public Native.Ownership Classify(int sessionId, Request request) =>
        Native.ClassifySession(sessionId, request.AgentName!, request.AgentSid!);
    public void Save(Lease lease) => Paths.SaveLease(lease);
    public IDisposable AuthorizationProbe(string ownerSid) => Native.Pipe(ownerSid, true);
    public uint RunWorker(Lease lease, CancellationToken cancel) =>
        Native.RunWorker(lease.SessionId!.Value, lease.Request.AgentSid!, lease.OutputKey, lease.Request.Variant, cancel);
    public void StopSession(int sessionId)
    {
        if (!Native.EXPERIMENTAL_WTSStopHeadlessSession(sessionId)) throw Native.Error("Stop owned Full session");
    }
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
}
