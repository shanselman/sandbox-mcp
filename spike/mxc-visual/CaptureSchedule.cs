internal static class CaptureSchedule
{
    public static long NextDue(long previousDueMs, long completedAtMs, int intervalMs)
    {
        if (previousDueMs < 0 || completedAtMs < previousDueMs || intervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(completedAtMs), "Capture times must be monotonic and interval positive");
        long followingSlot = checked(previousDueMs + intervalMs);
        long firstAvailableSlot = checked((completedAtMs + intervalMs - 1) / intervalMs * intervalMs);
        return Math.Max(followingSlot, firstAvailableSlot);
    }
}
