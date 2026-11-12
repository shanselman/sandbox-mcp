using System.Text.Json;
using System.Windows.Automation;

internal static class CalculatorReadinessTests
{
    public static int Run()
    {
        var failures = new List<string>();
        int passed = 0;
        void Check(string name, Action test)
        {
            try { test(); passed++; }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        Check("transient UIA startup errors are recorded and retried before success", () =>
        {
            int calls = 0;
            var diagnostics = new Dictionary<string, object?>();
            Program.WaitForCalculator(() =>
            {
                if (++calls < 3) throw new ElementNotAvailableException("Synthetic startup window replacement");
                return true;
            }, diagnostics, 1000, 1);
            if (calls != 3 || (int)diagnostics["readinessTransientErrors"]! != 2)
                throw new Exception("Startup retry or diagnostics missing");
        });
        Check("absent Calculator readiness fails within the deadline", () =>
        {
            try { Program.WaitForCalculator(() => false, new(), 10, 1); }
            catch (TimeoutException) { return; }
            throw new Exception("Missing UI was accepted");
        });
        Check("nontransient errors fail immediately without retry", () =>
        {
            int calls = 0;
            try { Program.WaitForCalculator(() => { calls++; throw new UnauthorizedAccessException("Synthetic denial"); }, new(), 1000, 1); }
            catch (UnauthorizedAccessException) { if (calls == 1) return; }
            throw new Exception("Authorization error was retried or suppressed");
        });
        Check("dump policy refuses other users, sessions, System and arbitrary executables", () =>
        {
            const string sid = "S-1-5-21-1-2-3-1001", image = @"C:\fixed\fixture\HelloWinUI.exe";
            OwnedAppDump.RequireTarget(sid, sid, 10, 10, 1, image, image);
            foreach (var target in new[] {
                (Sid: "S-1-5-18", Caller: sid, Session: 10, Helper: 10, Console: 1u, Image: image),
                (Sid: sid, Caller: "S-1-5-18", Session: 10, Helper: 10, Console: 1u, Image: image),
                (Sid: sid, Caller: sid, Session: 11, Helper: 10, Console: 1u, Image: image),
                (Sid: sid, Caller: sid, Session: 10, Helper: 10, Console: 10u, Image: image),
                (Sid: sid, Caller: sid, Session: 10, Helper: 10, Console: 1u, Image: @"C:\Windows\explorer.exe") })
            {
                bool refused = false;
                try { OwnedAppDump.RequireTarget(target.Sid, target.Caller, target.Session, target.Helper, target.Console, target.Image, image); }
                catch (UnauthorizedAccessException) { refused = true; }
                if (!refused) throw new Exception("Unsafe dump target accepted");
            }
        });
        Console.WriteLine(JsonSerializer.Serialize(new { passed, failures, nativeLifecycleInvoked = false }));
        return failures.Count == 0 ? 0 : 1;
    }
}
