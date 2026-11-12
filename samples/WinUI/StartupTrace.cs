using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HelloWinUI;

internal static class StartupTrace
{
    public static void Write(string phase, object? detail = null)
    {
        string? output = Environment.GetEnvironmentVariable("SBX_VISUAL_OUTPUT");
        if (output is null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                thread = Environment.CurrentManagedThreadId, phase, detail
            }) + "\n");
            using var file = new FileStream(Path.Combine(output, "app-startup.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            file.Write(bytes);
            file.Flush(true);
        }
        catch (Exception error)
        {
            // Diagnostics must not replace the original app failure.
            Console.Error.WriteLine($"Startup trace failed: {error}");
            Debug.WriteLine(error);
        }
    }
}
