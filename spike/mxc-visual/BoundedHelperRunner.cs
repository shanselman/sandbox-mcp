using System.Diagnostics;

internal static class BoundedHelperRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> Run(
        ProcessStartInfo start, int timeoutMs, int exitMs, Dictionary<string, object?> diagnostics)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        diagnostics["startedAt"] = DateTimeOffset.UtcNow;
        diagnostics["phase"] = "launch";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Helper failed to start");
        diagnostics["helperPid"] = process.Id;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            diagnostics["phase"] = "execution";
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            diagnostics["exitCode"] = process.ExitCode;
            diagnostics["exitedAt"] = DateTimeOffset.UtcNow;
            diagnostics["phase"] = "output-drain";
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromMilliseconds(exitMs));
            diagnostics["stderr"] = stderr.Result;
            diagnostics["phase"] = "finished";
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception error)
        {
            diagnostics["error"] = error.Message;
            diagnostics["failedAt"] = DateTimeOffset.UtcNow;
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception killError) { diagnostics["terminationError"] = killError.Message; }
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(exitMs));
                diagnostics["terminationConfirmed"] = true;
                diagnostics["exitCode"] = process.ExitCode;
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromMilliseconds(exitMs));
            }
            catch (Exception drainError) { diagnostics["terminationOrDrainError"] = drainError.Message; }
            diagnostics["stdout"] = stdout.IsCompletedSuccessfully ? stdout.Result : "<output drain incomplete>";
            diagnostics["stderr"] = stderr.IsCompletedSuccessfully ? stderr.Result : "<output drain incomplete>";
            throw new InvalidOperationException($"Owned helper failed during {diagnostics["phase"]} after a {timeoutMs}ms execution budget: {error.Message}", error);
        }
    }
}
