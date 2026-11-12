internal static class CaptureRunLimits
{
    public const int StartupMs = 120000;
    public const int BarrierMs = VisualWorkloads.MaxSessions * StartupMs + 60000;
    public const int CaptureMs = 90000;
    public const int ProgressMs = 30000;
    public const int ExitMs = 15000;
    public const int RunMs = 540000;
    public const int ResponseMs = 660000;
    public static int RunForMembers(int count) => count <= 3 ? RunMs : count * (StartupMs + 30000) + CaptureMs + ExitMs + 30000;
    public static int ResponseForMembers(int count) => RunForMembers(count) + Math.Max(120000, count * 30000);
}
