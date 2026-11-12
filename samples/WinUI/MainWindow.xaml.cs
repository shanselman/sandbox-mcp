using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;

namespace HelloWinUI;

public sealed partial class MainWindow : Window
{
    private int _count;
    private DispatcherTimer? _heartbeat;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    public MainWindow()
    {
        StartupTrace.Write("window.initialize.begin");
        InitializeComponent();
        StartupTrace.Write("window.initialize.end");
        StartupTrace.Write("window.dpi.begin");
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        StartupTrace.Write("window.dpi.end", new { scale });
        StartupTrace.Write("window.initial-resize.begin");
        AppWindow.Resize(new SizeInt32((int)(520 * scale), (int)(400 * scale)));
        StartupTrace.Write("window.initial-resize.end");
        string? variant = Environment.GetEnvironmentVariable("SBX_VISUAL_VARIANT");
        if (variant is null) return;
        var calculatorMatch = Regex.Match(variant, "^sample-calculator-(0[1-9]|1[0-6])$");
        int calculator = calculatorMatch.Success ? int.Parse(calculatorMatch.Groups[1].Value) : 0;
        if (calculator == 0 && variant is not ("light" or "dark" or "dark-large")) throw new ArgumentException("Unsupported visual fixture variant");
        StartupTrace.Write("window.theme.begin", new { variant, calculator });
        Root.RequestedTheme = variant == "light" || calculator % 2 == 1 ? ElementTheme.Light : ElementTheme.Dark;
        StartupTrace.Write("window.theme.end");
        if (calculator > 0)
        {
            StartupTrace.Write("window.title.begin");
            Title = $"Sample calculator {calculator:00}";
            StartupTrace.Write("window.title.end");
            StartupTrace.Write("window.content.clear.begin");
            Root.Children.Clear(); Root.RowDefinitions.Clear();
            StartupTrace.Write("window.content.clear.end");
            StartupTrace.Write("window.calculator.create.begin");
            Root.Children.Add(CalculatorView.Create(calculator, VariantInfo));
            StartupTrace.Write("window.calculator.create.end");
            StartupTrace.Write("window.calculator.resize.begin");
            AppWindow.Resize(new SizeInt32((int)(640 * scale), (int)(560 * scale)));
            StartupTrace.Write("window.calculator.resize.end");
        }
        double layoutScale = calculator > 0 ? 1 + ((calculator - 1) / 2 % 4) * 0.25
            : variant == "dark-large" ? 1.5 : 1;
        if (layoutScale != 1)
        {
            StartupTrace.Write("window.scale.begin", new { layoutScale });
            int width = calculator > 0 ? 640 : 520, height = calculator > 0 ? 560 : 400;
            AppWindow.Resize(new SizeInt32((int)(width * scale * layoutScale), (int)(height * scale * layoutScale)));
            if (!GetClientRect(WinRT.Interop.WindowNative.GetWindowHandle(this), out var client))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Read fixture client rectangle");
            Root.Width = (client.Right - client.Left) / scale / layoutScale;
            Root.Height = (client.Bottom - client.Top) / scale / layoutScale;
            Root.HorizontalAlignment = HorizontalAlignment.Left;
            Root.VerticalAlignment = VerticalAlignment.Top;
            Root.RenderTransform = new ScaleTransform { ScaleX = layoutScale, ScaleY = layoutScale };
            StartupTrace.Write("window.scale.end");
        }
        Root.Loaded += (_, _) =>
        {
            StartupTrace.Write("window.loaded.begin");
            uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            VariantInfo.Visibility = Visibility.Visible;
            string description = $"{Root.ActualTheme} | actual DPI {dpi} | fixture scale {layoutScale:0.##}x"
                + (layoutScale != 1 ? " (not monitor DPI)" : "");
            int tick = 0;
            VariantInfo.Text = description + " | tick 0";
            _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _heartbeat.Tick += (_, _) => VariantInfo.Text = description + $" | tick {++tick}";
            _heartbeat.Start();
            string output = Environment.GetEnvironmentVariable("SBX_VISUAL_OUTPUT")
                ?? throw new InvalidOperationException("Missing visual fixture output");
            var settings = new { variant, theme = Root.ActualTheme.ToString(), actualWindowDpi = dpi,
                rasterizationScale = Root.XamlRoot.RasterizationScale, fixtureLayoutScale = layoutScale,
                dpiWasChanged = false, scaleIsSimulation = layoutScale != 1,
                workload = calculator > 0 ? "calculator-sample" : "fixture",
                application = calculator > 0 ? "SandboxShowcase.WinUI.Calculator" : "SandboxShowcase.WinUI" };
            string file = Path.Combine(output, "fixture-settings.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(file + ".tmp", file, true);
            StartupTrace.Write("window.loaded.end", settings);
        };
        Closed += (_, _) => _heartbeat?.Stop();
        StartupTrace.Write("window.constructor.end");
    }

    private void GreetRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        var name = NameBox.Text.Trim();
        Output.Text = $"Hello, {(name.Length == 0 ? "Sandbox" : name)}!";
    }

    private void IncrementRequested(XamlUICommand sender, ExecuteRequestedEventArgs args) =>
        Counter.Text = $"Count: {++_count}";
}
