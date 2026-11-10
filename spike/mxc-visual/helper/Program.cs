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
using System.Windows.Automation;

static class Program
{
    static int Main(string[] args)
    {
        var opts = ParseArgs(args.Skip(1).ToArray());
        var report = new Dictionary<string, object?>
        {
            ["at"] = DateTime.UtcNow.ToString("o"),
            ["user"] = Environment.UserName,
            ["sessionId"] = Process.GetCurrentProcess().SessionId,
            ["interactive"] = Environment.UserInteractive,
            ["screen"] = $"{GetSystemMetrics(0)}x{GetSystemMetrics(1)}",
            ["virtualScreen"] = $"{GetSystemMetrics(78)}x{GetSystemMetrics(79)}",
            ["inputDesktop"] = InputDesktopName(),
            ["dwmCompositionEnabled"] = DwmIsCompositionEnabled(out var dwmOn) == 0 ? dwmOn : null,
            ["dwmInSession"] = Process.GetProcessesByName("dwm").Any(p => p.SessionId == Process.GetCurrentProcess().SessionId),
            ["wgcSupported"] = TryOr(() => Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported()),
        };
        try
        {
            switch (args.FirstOrDefault())
            {
                case "windows": report["windows"] = ListWindows(null); break;
                case "capture": Capture(opts, report); break;
                case "uia": Uia(opts, report); break;
                case "stream": Stream(opts, report); break;
                default: report["error"] = "unknown verb"; break;
            }
        }
        catch (Exception e) { report["error"] = e.ToString(); }
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.ContainsKey("error") ? 1 : 0;
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
        var frames = new List<object>();
        var timer = Stopwatch.StartNew();
        int failures = 0;
        for (int sequence = 0; sequence < 30; sequence++)
        {
            int delay = checked((int)(sequence * 1000 - timer.ElapsedMilliseconds));
            if (delay > 0) Thread.Sleep(delay);
            if (target.HasExited) throw new InvalidOperationException("Target exited during the bounded stream");
            if (sequence > 0 && sequence % 5 == 0)
            {
                var automation = new Dictionary<string, object?>();
                Uia(new Dictionary<string, string> { ["pid"] = pid.ToString(), ["invoke"] = "IncrementButton" }, automation);
                if (automation.ContainsKey("error")) throw new InvalidOperationException("Streaming UIA update failed");
            }
            long start = timer.ElapsedMilliseconds;
            string name = $"frame-{sequence:D3}.png";
            try
            {
                GetWindowRect(hwnd, out var rect);
                int width = rect.R - rect.L, height = rect.B - rect.T;
                if (width <= 0 || height <= 0 || (long)width * height > 16000000)
                    throw new InvalidOperationException("Invalid capture dimensions");
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { if (!PrintWindow(hwnd, dc, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                string path = Path.Combine(output, name);
                bitmap.Save(path + ".tmp", ImageFormat.Png);
                File.Move(path + ".tmp", path);
                var frame = new { sequence, at = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds,
                    captureMs = timer.ElapsedMilliseconds - start, scheduledMs = sequence * 1000, file = name,
                    sessionId = target.SessionId, pid, width, height, actualWindowDpi = GetDpiForWindow(hwnd), error = (string?)null };
                frames.Add(frame);
                WriteLive(output, frame);
            }
            catch (Exception error)
            {
                failures++;
                var frame = new { sequence, at = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds,
                    sessionId = target.SessionId, pid, error = error.Message };
                frames.Add(frame);
                WriteLive(output, frame);
            }
        }
        report["frames"] = frames;
        report["targetFps"] = 1;
        report["failedFrames"] = failures;
        File.WriteAllText(Path.Combine(output, "stream.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (failures != 0) throw new InvalidOperationException($"{failures} capture frames failed");
    }

    static void WriteLive(string output, object frame)
    {
        string path = Path.Combine(output, "live.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(frame));
        File.Move(path + ".tmp", path, true);
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
            bmp.Save(path, ImageFormat.Png);
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
