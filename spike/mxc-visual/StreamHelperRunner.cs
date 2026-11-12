using System.Diagnostics;
using System.Text.Json;

internal sealed record StreamHelperBudgets(int StartupMs, int CaptureMs, int ProgressMs, int ExitMs)
{
    public static StreamHelperBudgets Default => new(CaptureRunLimits.StartupMs, CaptureRunLimits.CaptureMs,
        CaptureRunLimits.ProgressMs, CaptureRunLimits.ExitMs);
}

internal static class StreamHelperRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> Run(
        ProcessStartInfo start, Func<CancellationToken, Task> ready,
        Dictionary<string, object?> diagnostics, CancellationToken cancel,
        StreamHelperBudgets? budgets = null)
    {
        budgets ??= StreamHelperBudgets.Default;
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Stream helper did not start");
        diagnostics["helperPid"] = process.Id;
        diagnostics["startedAt"] = DateTimeOffset.UtcNow;
        diagnostics["budgets"] = budgets;
        diagnostics["phase"] = "startup";
        var stderr = process.StandardError.ReadToEndAsync();
        using var input = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, input.Token);
        async Task<JsonElement> ReadMessage(int timeout)
        {
            string? line = await process.StandardOutput.ReadLineAsync(linked.Token).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(timeout), cancel);
            if (line is null) throw new IOException("Stream helper exited before its completion message");
            if (line.Length > 262144) throw new InvalidDataException("Stream helper message exceeded its limit");
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }
        try
        {
            var message = await ReadMessage(budgets.StartupMs);
            if (message.GetProperty("phase").GetString() != "ready")
                throw new InvalidDataException("Expected stream helper readiness: " + message.GetRawText());
            diagnostics["readyAt"] = DateTimeOffset.UtcNow;
            diagnostics["phase"] = "barrier";
            await ready(cancel);
            diagnostics["captureReleasedAt"] = DateTimeOffset.UtcNow;
            await process.StandardInput.WriteLineAsync("GO");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            diagnostics["phase"] = "capture";
            var timer = Stopwatch.StartNew();
            int nextSequence = 0;
            JsonElement report;
            while (true)
            {
                int remaining = budgets.CaptureMs - checked((int)timer.ElapsedMilliseconds);
                if (remaining <= 0) throw new TimeoutException("Stream capture total budget expired");
                message = await ReadMessage(Math.Min(remaining, budgets.ProgressMs));
                string? phase = message.GetProperty("phase").GetString();
                if (phase == "frame")
                {
                    if (message.GetProperty("sequence").GetInt32() != nextSequence++)
                        throw new InvalidDataException("Stream helper progress sequence changed");
                    diagnostics["receivedFrames"] = nextSequence;
                    diagnostics["lastProgressAt"] = DateTimeOffset.UtcNow;
                }
                else if (phase == "complete")
                {
                    report = message.GetProperty("report");
                    if (nextSequence != 30 || report.TryGetProperty("error", out _))
                        throw new InvalidDataException("Stream helper completed with missing frames or an error");
                    break;
                }
                else throw new InvalidDataException("Unexpected stream helper phase: " + message.GetRawText());
            }
            diagnostics["captureCompletedAt"] = DateTimeOffset.UtcNow;
            diagnostics["phase"] = "exit";
            await process.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromMilliseconds(budgets.ExitMs), cancel);
            string errorText = await stderr.WaitAsync(TimeSpan.FromMilliseconds(budgets.ExitMs), cancel);
            diagnostics["exitedAt"] = DateTimeOffset.UtcNow;
            diagnostics["exitCode"] = process.ExitCode;
            diagnostics["phase"] = "finished";
            return (process.ExitCode, report.GetRawText(), errorText);
        }
        catch (Exception error)
        {
            diagnostics["error"] = error.Message;
            diagnostics["failedAt"] = DateTimeOffset.UtcNow;
            input.Cancel();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(budgets.ExitMs)); }
                catch (TimeoutException) { diagnostics["terminationPending"] = true; }
            }
            if (stderr.IsCompletedSuccessfully) diagnostics["stderr"] = stderr.Result;
            throw new InvalidOperationException($"Stream helper failed during {diagnostics["phase"]}: {error.Message}", error);
        }
    }
}
