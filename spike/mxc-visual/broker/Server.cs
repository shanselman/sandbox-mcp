using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class Server
{
    private static readonly Native.ServiceMain MainCallback = Entry;
    private static readonly Native.ServiceHandler HandlerCallback = Control;
    private static readonly CancellationTokenSource Stop = new();
    private static IntPtr statusHandle;
    private static string? recoveryError;

    public static void Dispatch()
    {
        var table = new[]
        {
            new Native.ServiceEntry { Name = Paths.ServiceName, Main = Marshal.GetFunctionPointerForDelegate(MainCallback) },
            new Native.ServiceEntry()
        };
        if (!Native.StartServiceCtrlDispatcherW(table)) throw Native.Error("Start service dispatcher");
    }

    private static void Entry(uint argc, IntPtr argv)
    {
        uint error = 0;
        try
        {
            statusHandle = Native.RegisterServiceCtrlHandlerExW(Paths.ServiceName, HandlerCallback, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) throw Native.Error("Register service handler");
            Report(2, 0);
            if (!Native.IsSystem) throw new UnauthorizedAccessException("Broker must run as LocalSystem");
            var config = Paths.ReadConfiguration();
            if (!string.Equals(Paths.ReleaseRoot, Paths.Release(config.Release), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Service is not running from its protected installed release");
            Paths.RequireProtected(Environment.ProcessPath!);
            Paths.RequireProtected(Path.Combine(AppContext.BaseDirectory, "SandboxMcp.FullBroker.dll"));
            Paths.RequireProtected(Paths.Jobs);
            try
            {
                Platform.Check();
                Runtime.Recover();
            }
            catch (Exception failure) { recoveryError = failure.Message; }
            Report(4, 0);
            Serve(config, Stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch (Exception failure)
        {
            error = 1066;
            Log("Service failure: " + failure.GetType().Name + ": " + failure.Message);
        }
        finally
        {
            if (statusHandle != IntPtr.Zero) Report(1, error);
        }
    }

    private static uint Control(uint control, uint eventType, IntPtr data, IntPtr context)
    {
        if (control == 1)
        {
            Report(3, 0);
            Stop.Cancel();
        }
        return 0;
    }
    private static void Report(uint state, uint error)
    {
        var status = new Native.Status
        {
            Type = 0x10, State = state, Accepted = state == 4 ? 1u : 0u,
            Error = error, SpecificError = error == 1066 ? 1u : 0u,
            Checkpoint = state is 2 or 3 ? 1u : 0u, WaitHint = state is 2 or 3 ? 180000u : 0u
        };
        if (!Native.SetServiceStatus(statusHandle, ref status)) throw Native.Error("Report service status");
    }

    private static async Task Serve(Configuration config, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var pipe = Native.Pipe(config.OwnerSid);
            await pipe.WaitForConnectionAsync(stopping);
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            object response;
            try
            {
                byte[] bytes = await Protocol.Read(pipe, Protocol.RequestLimit, readDeadline.Token);
                string? callerSid = null;
                pipe.RunAsClient(() =>
                {
                    using var caller = WindowsIdentity.GetCurrent(true);
                    callerSid = caller?.User?.Value;
                });
                if (!string.Equals(callerSid, config.OwnerSid, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Caller is not the installed owner");
                var request = Protocol.Parse(bytes);
                if (request.Operation == "run-fixture")
                {
                    if (recoveryError is not null) throw new InvalidOperationException("Broker recovery/build check is required: " + recoveryError);
                    using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    run.CancelAfter(TimeSpan.FromSeconds(150));
                    using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    var monitor = MonitorDisconnect(pipe, run, monitorStop.Token);
                    try { response = await Task.Run(() => Runtime.Run(request, config, run.Token), CancellationToken.None); }
                    finally
                    {
                        monitorStop.Cancel();
                        await monitor;
                    }
                }
                else response = request.Operation switch
                {
                    "status" => new { ok = true, protocol = 1, ready = recoveryError is null, recoveryError, pending = Runtime.Summaries() },
                    "result" => Runtime.Result(request.Id!),
                    "ack" => Runtime.Ack(request.Id!),
                    "recover" => Recover(),
                    _ => throw new InvalidDataException("Unsupported operation")
                };
            }
            catch (Exception failure) when (failure is JsonException or InvalidDataException or ArgumentException)
            {
                response = new { ok = false, code = "invalid_request", error = "Malformed or unsupported request" };
            }
            catch (UnauthorizedAccessException)
            {
                response = new { ok = false, code = "access_denied", error = "Caller or resource ownership was rejected" };
            }
            catch (Exception failure)
            {
                if (stopping.IsCancellationRequested) break;
                Log("Request failed: " + failure.GetType().Name + ": " + failure.Message);
                response = new { ok = false, code = "request_failed", error = failure.Message };
            }
            try
            {
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                await Protocol.Write(pipe, response, Protocol.ResponseLimit, writeDeadline.Token);
            }
            catch (Exception failure) when (failure is IOException or OperationCanceledException)
            {
                Log("Client disconnected or response deadline elapsed; durable state retained");
            }
        }
    }

    private static object Recover()
    {
        try
        {
            Platform.Check();
            var response = Runtime.Recover();
            recoveryError = null;
            return response;
        }
        catch (Exception failure) { recoveryError = failure.Message; throw; }
    }
    private static async Task MonitorDisconnect(Stream pipe, CancellationTokenSource run, CancellationToken stop)
    {
        try
        {
            byte[] extra = new byte[1];
            int count = await pipe.ReadAsync(extra, stop);
            if (count != 0) Log("Additional bytes rejected after the single request");
            run.Cancel(); // EOF or extra bytes: one request is allowed per connection.
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { run.Cancel(); }
    }
    private static void Log(string message)
    {
        Paths.RequireProtected(Paths.StateRoot);
        string path = Path.Combine(Paths.StateRoot, "events.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 524288)
            File.Move(path, Path.Combine(Paths.StateRoot, "events.previous.jsonl"), true);
        File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, message }) + Environment.NewLine);
    }
}
