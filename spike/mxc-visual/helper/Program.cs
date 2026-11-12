// In-session capture helper. Runs as the IsolationSession agent user, in the same
// session/desktop as the app. Writes PNGs + a JSON report to --out. Never touches
// other sessions. Usage:
//   CaptureHelper capture --pid <pid> --out <dir> --label <name>
//   CaptureHelper uia --pid <pid> --invoke <automationId> [--set <automationId>=<text>]
//   CaptureHelper windows
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

static class Program
{
    static int Main(string[] args)
    {
        if (args is ["calculator-readiness-selftest"]) return CalculatorReadinessTests.Run();
        var opts = ParseArgs(args.Skip(1).ToArray());
        Trace("helper.enter", new { operation = args.FirstOrDefault() });
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTime.UtcNow.ToString("o"),
            ["user"] = Environment.UserName,
            ["sessionId"] = Process.GetCurrentProcess().SessionId,
            ["interactive"] = Environment.UserInteractive
        };
        try
        {
            if (args.FirstOrDefault() == "dump-app")
            {
                Trace("dump.owned-app");
                OwnedAppDump.Capture(int.Parse(opts["pid"]), opts["out"], report);
                Console.WriteLine(JsonSerializer.Serialize(report));
                return 0;
            }
            Trace("diagnostics.display");
            report["screen"] = $"{GetSystemMetrics(0)}x{GetSystemMetrics(1)}";
            report["virtualScreen"] = $"{GetSystemMetrics(78)}x{GetSystemMetrics(79)}";
            Trace("diagnostics.input-desktop");
            report["inputDesktop"] = InputDesktopName();
            Trace("diagnostics.dwm");
            report["dwmCompositionEnabled"] = DwmIsCompositionEnabled(out var dwmOn) == 0 ? dwmOn : null;
            Trace("diagnostics.process-enumeration");
            var processes = Process.GetProcessesByName("dwm");
            try { report["dwmInSession"] = processes.Any(p => p.SessionId == Process.GetCurrentProcess().SessionId); }
            finally { foreach (var process in processes) process.Dispose(); }
            Trace("diagnostics.wgc-support");
            report["wgcSupported"] = TryOr(() => Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported());
            Trace("operation.begin", new { operation = args.FirstOrDefault() });
            switch (args.FirstOrDefault())
            {
                case "windows": report["windows"] = ListWindows(null); break;
                case "capture": Capture(opts, report); break;
                case "preview": Preview(opts, report); break;
                case "uia": Uia(opts, report); break;
                case "stream": Stream(opts, report); break;
                case "calculator-ready": CalculatorReady(opts, report); break;
                case "calculator-probe": CalculatorProbe(opts, report); break;
                default: report["error"] = "unknown verb"; break;
            }
        }
        catch (Exception e) { report["error"] = e.ToString(); }
        Trace("operation.end", new { failed = report.ContainsKey("error") });
        if (opts.GetValueOrDefault("coordinated") == "true")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { phase = report.ContainsKey("error") ? "error" : "complete", report }));
            Console.Out.Flush();
        }
        else Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.ContainsKey("error") ? 1 : 0;
    }

    static void Trace(string phase, object? detail = null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, phase, detail }));
        Console.Error.Flush();
    }

    static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
        return d;
    }

    static List<Dictionary<string, object?>> ListWindows(int? pid)
    {
        var list = new List<Dictionary<string, object?>>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wp);
            if (pid is null || wp == pid)
            {
                GetWindowRect(h, out var r);
                var title = new StringBuilder(256); GetWindowText(h, title, 256);
                var cls = new StringBuilder(256); GetClassName(h, cls, 256);
                list.Add(new()
                {
                    ["hwnd"] = $"0x{h:X}", ["pid"] = wp, ["title"] = title.ToString(), ["class"] = cls.ToString(),
                    ["visible"] = IsWindowVisible(h), ["iconic"] = IsIconic(h),
                    ["rect"] = new[] { r.L, r.T, r.R - r.L, r.B - r.T },
                });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr MainWindow(int pid, int waitMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var wp);
                if (wp == pid && IsWindowVisible(h)) { GetWindowRect(h, out var r); if (r.R - r.L > 50) { found = h; return false; } }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            Thread.Sleep(250);
        }
        return IntPtr.Zero;
    }

    static int ResolvePid(Dictionary<string, string> o)
    {
        if (o.TryGetValue("pid", out var p)) return int.Parse(p);
        var me = Process.GetCurrentProcess().SessionId;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30000)
        {
            var hit = Process.GetProcessesByName(o["name"]).FirstOrDefault(x => x.SessionId == me);
            if (hit != null) return hit.Id;
            Thread.Sleep(250);
        }
        throw new Exception($"process {o["name"]} not found in session {me}");
    }

    static void Capture(Dictionary<string, string> o, Dictionary<string, object?> report)
    {
        int pid = ResolvePid(o);
        report["pid"] = pid;
        string outDir = o["out"], label = o.GetValueOrDefault("label", "cap");
        Directory.CreateDirectory(outDir);
        var hwnd = MainWindow(pid, int.Parse(o.GetValueOrDefault("wait", "20000")));
        report["windows"] = ListWindows(pid);
        if (hwnd == IntPtr.Zero) { report["error"] = "no visible window for pid"; return; }
        report["hwnd"] = $"0x{hwnd:X}";
        report["actualWindowDpi"] = GetDpiForWindow(hwnd);
        SetForegroundWindow(hwnd);
        Thread.Sleep(int.Parse(o.GetValueOrDefault("settle", "500")));
        GetWindowRect(hwnd, out var r);
        int w = r.R - r.L, h = r.B - r.T;
        var results = new List<Dictionary<string, object?>>();

        results.Add(Try("screen", label, outDir, () =>
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            var bmp = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh));
            return bmp;
        }));
        results.Add(Try("window-bitblt", label, outDir, () =>
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h));
            return bmp;
        }));
        foreach (var (name, flag) in new[] { ("printwindow-0", 0u), ("printwindow-2", 2u) })
        {
            results.Add(Try(name, label, outDir, () =>
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                var hdc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(hwnd, hdc, flag); } finally { g.ReleaseHdc(hdc); }
                if (!ok) throw new Exception($"PrintWindow returned false, lastError={Marshal.GetLastWin32Error()}");
                return bmp;
            }));
        }
        report["captures"] = results;
    }

    static void Stream(Dictionary<string, string> options, Dictionary<string, object?> report)
    {
        int pid = ResolvePid(options);
        using var target = Process.GetProcessById(pid);
        if (target.SessionId != Process.GetCurrentProcess().SessionId)
            throw new InvalidOperationException("Capture target is outside the helper's session");
        string output = options["out"];
        var hwnd = MainWindow(pid, 10000);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No visible target window");
        bool coordinated = options.GetValueOrDefault("coordinated") == "true";
        int calculator = VisualWorkloads.CalculatorNumber(options.GetValueOrDefault("variant"));
        var steps = calculator > 0 ? VisualWorkloads.CalculatorSteps(calculator) : [];
        var calculatorRoot = calculator > 0 ? CalculatorRoot(pid, report) : null;
        var automationChecks = new List<object>();
        if (coordinated)
        {
            if (!GetWindowRect(hwnd, out var warmRect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            int width = warmRect.R - warmRect.L, height = warmRect.B - warmRect.T;
            if (width <= 0 || height <= 0 || (long)width * height > 16000000) throw new InvalidOperationException("Invalid warm-up capture dimensions");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var dc = graphics.GetHdc();
                try { if (!PrintWindow(hwnd, dc, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Warm-up PrintWindow"); }
                finally { graphics.ReleaseHdc(dc); }
            }
            Console.WriteLine("""{"phase":"ready"}""");
            Console.Out.Flush();
            string? command = Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(CaptureRunLimits.BarrierMs)).GetAwaiter().GetResult();
            if (command != "GO") throw new InvalidDataException("Expected coordinated capture start");
        }
        var frames = new List<object>();
        var timer = Stopwatch.StartNew();
        long scheduledMs = 0;
        const int intervalMs = 1000;
        int failures = 0;
        for (int sequence = 0; sequence < 30; sequence++)
        {
            int delay = checked((int)(scheduledMs - timer.ElapsedMilliseconds));
            if (delay > 0) Thread.Sleep(delay);
            if (target.HasExited) throw new InvalidOperationException("Target exited during the bounded stream");
            long start = timer.ElapsedMilliseconds;
            string name = $"frame-{sequence:D3}.png";
            try
            {
                object? automation = null;
                if (calculatorRoot is not null && sequence < steps.Length)
                {
                    automation = CalculatorStep(calculatorRoot, steps[sequence]);
                    if (steps[sequence].Expected is not null) automationChecks.Add(automation);
                }
                GetWindowRect(hwnd, out var rect);
                int width = rect.R - rect.L, height = rect.B - rect.T;
                if (width <= 0 || height <= 0 || (long)width * height > 16000000)
                    throw new InvalidOperationException("Invalid capture dimensions");
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                long renderStarted = timer.ElapsedMilliseconds;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { if (!PrintWindow(hwnd, dc, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                long renderMs = timer.ElapsedMilliseconds - renderStarted;
                string path = Path.Combine(output, name);
                long publicationStarted = timer.ElapsedMilliseconds;
                EvidencePublication.PublishNew(path, stream => bitmap.Save(stream, ImageFormat.Png));
                long completed = timer.ElapsedMilliseconds;
                var frame = new { sequence, at = DateTimeOffset.UtcNow, elapsedMs = completed,
                    captureMs = completed - start, renderMs, publicationMs = completed - publicationStarted,
                    scheduleDelayMs = Math.Max(0, start - scheduledMs), scheduledMs, file = name,
                    sessionId = target.SessionId, pid, width, height, actualWindowDpi = GetDpiForWindow(hwnd), automation, error = (string?)null };
                frames.Add(frame);
                WriteLive(output, sequence, frame);
                if (coordinated)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { phase = "frame", sequence }));
                    Console.Out.Flush();
                }
            }
            catch (Exception error)
            {
                failures++;
                var frame = new { sequence, at = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds,
                    sessionId = target.SessionId, pid, error = error.Message };
                frames.Add(frame);
                WriteLive(output, sequence, frame);
            }
            scheduledMs = CaptureSchedule.NextDue(scheduledMs, timer.ElapsedMilliseconds, intervalMs);
        }
        report["frames"] = frames;
        report["targetFps"] = 1;
        report["schedule"] = "skip-overdue-slots";
        report["failedFrames"] = failures;
        if (calculator > 0)
        {
            report["workload"] = VisualWorkloads.IsSampleCalculator(options.GetValueOrDefault("variant")) ? "calculator-sample" : "calculator";
            report["automationChecks"] = automationChecks;
            if (automationChecks.Count != 3) failures++;
        }
        EvidencePublication.PublishNew(Path.Combine(output, "stream.json"),
            stream => JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true }));
        if (failures != 0) throw new InvalidOperationException($"{failures} capture frames failed");
    }

    static AutomationElement CalculatorRoot(int pid, Dictionary<string, object?> report)
    {
        using var target = Process.GetProcessById(pid);
        if (target.SessionId != Process.GetCurrentProcess().SessionId)
            throw new UnauthorizedAccessException("Calculator is outside the helper session");
        AutomationElement? found = null;
        var diagnostics = new Dictionary<string, object?>();
        report["calculatorReadiness"] = diagnostics;
        WaitForCalculator(() =>
        {
            if (target.HasExited) throw new InvalidOperationException("Calculator exited before UI Automation became ready");
            var hwnd = MainWindow(pid, 1000);
            diagnostics["lastWindow"] = $"0x{hwnd:X}";
            if (hwnd != IntPtr.Zero)
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CalculatorResults")) is not null)
                { found = root; return true; }
            }
            return false;
        }, diagnostics);
        return found ?? throw new InvalidOperationException("Calculator readiness did not return an element");
    }

    internal static void WaitForCalculator(Func<bool> probe, Dictionary<string, object?> diagnostics, int timeoutMs = 20000, int pollMs = 200)
    {
        var timer = Stopwatch.StartNew();
        int attempts = 0, transientErrors = 0;
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            diagnostics["readinessAttempts"] = ++attempts;
            try { if (probe()) return; }
            catch (ElementNotAvailableException error)
            {
                diagnostics["readinessTransientErrors"] = ++transientErrors;
                diagnostics["lastReadinessError"] = error.ToString();
            }
            Thread.Sleep(pollMs);
        }
        throw new TimeoutException("Calculator UI Automation results element did not become ready: " + JsonSerializer.Serialize(diagnostics));
    }

    static object CalculatorStep(AutomationElement root, VisualWorkloads.Step step)
    {
        AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id))
            ?? throw new InvalidOperationException($"Calculator element {id} is missing");
        ((InvokePattern)Find(step.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(150);
        string display = Find("CalculatorResults").Current.Name;
        int? actual = null;
        if (step.Expected is not null)
        {
            if (!int.TryParse(Regex.Replace(display, "[^0-9-]", ""), out int value) || value != step.Expected)
                throw new InvalidOperationException($"Calculator expected {step.Expected}, observed '{display}'");
            actual = value;
        }
        return new { button = step.Button, expected = step.Expected, actual, display, passed = true };
    }

    static void CalculatorReady(Dictionary<string, string> options, Dictionary<string, object?> report)
    {
        int pid = ResolvePid(options);
        string variant = options["variant"];
        if (VisualWorkloads.CalculatorNumber(variant) == 0) throw new ArgumentException("Invalid Calculator variant");
        var root = CalculatorRoot(pid, report);
        report["clear"] = CalculatorStep(root, new("clearButton", 0));
        report["pid"] = pid;
        report["calculatorReady"] = true;
        if (VisualWorkloads.IsSampleCalculator(variant))
        {
            if (!File.Exists(Path.Combine(options["out"], "fixture-settings.json")))
                throw new InvalidOperationException("Sample calculator has not published its measured settings");
            return;
        }
        var settings = new { variant, workload = "calculator", application = "Microsoft.WindowsCalculator",
            theme = "System (unmeasured)", actualWindowDpi = GetDpiForWindow(MainWindow(pid, 1000)),
            rasterizationScale = (double?)null, fixtureLayoutScale = 1, dpiWasChanged = false, scaleIsSimulation = false };
        EvidencePublication.PublishNew(Path.Combine(options["out"], "fixture-settings.json"),
            stream => JsonSerializer.Serialize(stream, settings));
    }

    static void CalculatorProbe(Dictionary<string, string> options, Dictionary<string, object?> report)
    {
        int pid = ResolvePid(options);
        var root = CalculatorRoot(pid, report);
        foreach (string button in new[] { "clearButton", "num7Button", "plusButton", "num8Button" })
            CalculatorStep(root, new(button));
        report["assertion"] = CalculatorStep(root, new("equalButton", 15));
        report["pid"] = pid;
        report["calculatorVerified"] = true;
    }

    static void Preview(Dictionary<string, string> options, Dictionary<string, object?> report)
    {
        Trace("preview.resolve-target");
        int pid = ResolvePid(options);
        using var target = Process.GetProcessById(pid);
        if (target.SessionId != Process.GetCurrentProcess().SessionId)
            throw new UnauthorizedAccessException("Preview target is outside the helper session");
        var timer = Stopwatch.StartNew();
        Trace("preview.find-window", new { pid });
        var hwnd = MainWindow(pid, 20000);
        Trace("preview.enumerate-windows", new { pid, hwnd = $"0x{hwnd:X}" });
        report["windows"] = ListWindows(pid);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No visible app window for startup picture");
        if (!GetWindowRect(hwnd, out var rect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        int width = rect.R - rect.L, height = rect.B - rect.T;
        if (width <= 0 || height <= 0 || (long)width * height > 16000000) throw new InvalidOperationException("Invalid startup picture dimensions");
        Trace("preview.allocate-bitmap", new { width, height });
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            Trace("preview.printwindow", new { pid, hwnd = $"0x{hwnd:X}", width, height });
            try { if (!PrintWindow(hwnd, dc, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Startup PrintWindow"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Trace("preview.publish-image");
        EvidencePublication.PublishNew(Path.Combine(options["out"], "startup.png"), stream => bitmap.Save(stream, ImageFormat.Png));
        long elapsed = timer.ElapsedMilliseconds;
        var preview = new { kind = "startup", at = DateTimeOffset.UtcNow, file = "startup.png",
            sessionId = target.SessionId, pid, width, height, actualWindowDpi = GetDpiForWindow(hwnd),
            captureMs = elapsed, elapsedMs = elapsed, hwnd = $"0x{hwnd:X}" };
        report["preview"] = preview;
        Trace("preview.publish-metadata");
        EvidencePublication.PublishNew(Path.Combine(options["out"], "startup.json"), stream => JsonSerializer.Serialize(stream, preview));
        Trace("preview.complete");
    }

    static void WriteLive(string output, int sequence, object frame)
    {
        // Immutable metadata avoids replacing a cross-account file while a
        // collector has it open; publication is a rename to a new name.
        string path = Path.Combine(output, $"frame-meta-{sequence:D3}.json");
        EvidencePublication.PublishNew(path, stream => JsonSerializer.Serialize(stream, frame));
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    static Dictionary<string, object?> Try(string method, string label, string outDir, Func<Bitmap> grab)
    {
        var d = new Dictionary<string, object?> { ["method"] = method };
        var sw = Stopwatch.StartNew();
        try
        {
            using var bmp = grab();
            d["ms"] = sw.ElapsedMilliseconds;
            d["size"] = $"{bmp.Width}x{bmp.Height}";
            d["stats"] = Stats(bmp);
            var path = Path.Combine(outDir, $"{label}-{method}.png");
            EvidencePublication.PublishNew(path, stream => bmp.Save(stream, ImageFormat.Png));
            d["png"] = path;
        }
        catch (Exception e) { d["ms"] = sw.ElapsedMilliseconds; d["error"] = e.Message; }
        return d;
    }

    // Cheap content signal only; humans must still look at the PNGs.
    static object Stats(Bitmap b)
    {
        var colors = new HashSet<int>(); long black = 0, n = 0;
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2)
            {
                var c = b.GetPixel(x, y); n++;
                if (c.R < 8 && c.G < 8 && c.B < 8) black++;
                if (colors.Count < 5000) colors.Add(c.ToArgb());
            }
        return new { sampled = n, blackPct = n == 0 ? 0 : Math.Round(100.0 * black / n, 1), distinctColors = colors.Count };
    }

    static void Uia(Dictionary<string, string> o, Dictionary<string, object?> report)
    {
        int pid = ResolvePid(o);
        report["pid"] = pid;
        var hwnd = MainWindow(pid, 20000);
        if (hwnd == IntPtr.Zero) { report["error"] = "no visible window for pid"; return; }
        var root = AutomationElement.FromHandle(hwnd);
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        report["uiaDescendants"] = all.Count;
        report["uiaIds"] = all.Cast<AutomationElement>()
            .Select(e => $"{e.Current.ControlType.ProgrammaticName}:{e.Current.AutomationId}:{e.Current.Name}")
            .Take(60).ToList();
        AutomationElement? Find(string id) =>
            root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (o.TryGetValue("set", out var set))
        {
            var (id, text) = (set[..set.IndexOf('=')], set[(set.IndexOf('=') + 1)..]);
            var el = Find(id) ?? throw new Exception($"element {id} not found");
            ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
            report["set"] = $"{id}={text}";
        }
        if (o.TryGetValue("invoke", out var inv))
        {
            foreach (var id in inv.Split(','))
            {
                var el = Find(id) ?? throw new Exception($"element {id} not found");
                ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Thread.Sleep(150);
            }
            report["invoked"] = inv;
        }
        Thread.Sleep(300);
        report["after"] = new[] { "Output", "Counter" }.Select(id => $"{id}={Find(id)?.Current.Name}").ToList();
    }

    static string InputDesktopName()
    {
        var d = OpenInputDesktop(0, false, 0x0001);
        if (d == IntPtr.Zero) return $"<OpenInputDesktop failed {Marshal.GetLastWin32Error()}>";
        try
        {
            var sb = new byte[512];
            return GetUserObjectInformationW(d, 2, sb, sb.Length, out var len)
                ? Encoding.Unicode.GetString(sb, 0, Math.Max(0, len - 2)) : "<unknown>";
        }
        finally { CloseDesktop(d); }
    }

    static object? TryOr(Func<object?> f) { try { return f(); } catch (Exception e) { return $"error: {e.Message}"; } }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled(out bool enabled);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint f, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr d);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetUserObjectInformationW(IntPtr h, int i, byte[] b, int n, out int len);
}
