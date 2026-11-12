namespace SandboxMcp.FullBroker;

internal static class StartupPacing
{
    private static readonly IReadOnlyDictionary<string, int> Delays = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["immediate"] = 0,
        ["settle-2s"] = 2000,
        ["settle-5s"] = 5000,
        ["settle-6s"] = 6000,
        ["settle-15s"] = 15000
    };

    public static string[] Modes => Delays.Keys.ToArray();

    public static int DelayMs(string? mode) => Delays.TryGetValue(mode ?? "immediate", out int delay)
        ? delay : throw new InvalidDataException("Unsupported fixed startup pacing");

    public static void ValidateDelay(int delay)
    {
        if (!Delays.Values.Contains(delay)) throw new ArgumentOutOfRangeException(nameof(delay));
    }
}
