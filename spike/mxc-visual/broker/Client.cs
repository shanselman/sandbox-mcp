using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class Client
{
    public static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sandbox-mcp", "full-broker");
    public static FileStream AcquireWorkflowLock()
    {
        if (Native.IsSystem) throw new UnauthorizedAccessException("The client coordinator must not run as System");
        Directory.CreateDirectory(StateRoot);
        return new FileStream(Path.Combine(StateRoot, "client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static void RequireNoPendingAccounts(string root)
    {
        string jobs = Path.Combine(root, "jobs");
        if (Directory.Exists(jobs) && Directory.GetFiles(jobs, "*.json").Length != 0)
            throw new InvalidOperationException("Run the normal client recovery command before uninstalling; provisioned accounts are still recorded");
    }

    public static async Task<int> HoldLock()
    {
        using var handle = AcquireWorkflowLock();
        Console.WriteLine("""{"locked":true}""");
        Console.Out.Flush();
        using var input = Console.OpenStandardInput();
        await input.CopyToAsync(Stream.Null);
        return 0;
    }

    public static async Task<int> Call()
    {
        using var input = Console.OpenStandardInput();
        using var data = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(chunk)) != 0)
        {
            if (data.Length + read > Protocol.RequestLimit) throw new InvalidDataException("Request exceeds limit");
            data.Write(chunk, 0, read);
        }
        var request = Protocol.Parse(data.ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            request.Operation is "run-fixture" or "run-grid" ? CaptureRunLimits.ResponseForMembers(request.Members?.Length ?? 1) : 220000));
        using var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, deadline.Token);
        using var manager = Native.OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw Native.Error("Read service manager");
        using var service = Native.OpenServiceW(manager, Paths.ServiceName, 4);
        if (service.IsInvalid) throw Native.Error("Read installed service");
        var status = Native.Query(service);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw Native.Error("Authenticate pipe server");
        if (status.State != 4 || status.Pid == 0 || status.Pid != pid)
            throw new UnauthorizedAccessException("Pipe server is not the installed running broker");
        await Protocol.Write(pipe, request, Protocol.RequestLimit, deadline.Token);
        byte[] response = await Protocol.Read(pipe, Protocol.ResponseLimit, deadline.Token);
        using var document = JsonDocument.Parse(response);
        Console.WriteLine(document.RootElement.GetRawText());
        return document.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
    }
}
