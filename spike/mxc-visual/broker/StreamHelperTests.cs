using System.Diagnostics;
using System.Text.Json;

namespace SandboxMcp.FullBroker;

internal static class StreamHelperTests
{
    public static int Child(string mode)
    {
        if (mode == "startup-error")
        {
            Console.WriteLine("""{"phase":"error","report":{"error":"Synthetic warm-up failure"}}""");
            return 1;
        }
        Console.WriteLine("""{"phase":"ready"}""");
        Console.Out.Flush();
        if (Console.ReadLine() != "GO") return 2;
        for (int i = 0; i < 30; i++)
        {
            if (mode == "missing-frame" && i == 15) continue;
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "frame", sequence = i }));
        }
        Console.WriteLine("""{"phase":"complete","report":{"frames":[],"targetFps":1}}""");
        Console.Out.Flush();
        if (mode == "exit-stall") Thread.Sleep(20000);
        return 0;
    }

    public static void Register(Action<string, Action> check)
    {
        foreach (string mode in new[] { "complete", "startup-error", "missing-frame", "exit-stall" })
            check("stream helper phases: " + mode, () =>
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!);
                start.ArgumentList.Add("selftest-stream-child");
                start.ArgumentList.Add(mode);
                var diagnostics = new Dictionary<string, object?>();
                int ready = 0;
                Exception? failure = null;
                try
                {
                    var result = StreamHelperRunner.Run(start, _ =>
                    {
                        ready++;
                        return Task.CompletedTask;
                    }, diagnostics, CancellationToken.None, new(10000, 10000, 5000, 1000)).GetAwaiter().GetResult();
                    if (result.ExitCode != 0) throw new Exception("Synthetic child failed");
                }
                catch (InvalidOperationException error) { failure = error; }
                if (mode == "complete")
                {
                    if (failure is not null || ready != 1 || diagnostics["phase"]?.ToString() != "finished")
                        throw new Exception("Complete stream did not verify exit", failure);
                }
                else
                {
                    string expected = mode == "startup-error" ? "startup" : mode == "exit-stall" ? "exit" : "capture";
                    if (failure is null || diagnostics["phase"]?.ToString() != expected)
                        throw new Exception("Failure was assigned the wrong stage", failure);
                    if (mode == "exit-stall" && !diagnostics.ContainsKey("captureCompletedAt"))
                        throw new Exception("Completed capture evidence lost on exit stall");
                }
            });
    }
}
