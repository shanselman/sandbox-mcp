internal static class VisualWorkloads
{
    public const int MaxSessions = 16;
    public static readonly string[] Variants = ["light", "dark", "dark-large",
        .. Enumerable.Range(1, MaxSessions).Select(index => $"calculator-{index:D2}")];
    public static bool IsAllowed(string? variant) => variant is null || Variants.Contains(variant);
    public static int CalculatorNumber(string? variant) =>
        variant is not null && Variants.Skip(3).Contains(variant) ? int.Parse(variant[^2..]) : 0;

    internal sealed record Step(string Button, int? Expected = null);
    public static Step[] CalculatorSteps(int index)
    {
        if (index is < 1 or > MaxSessions) throw new ArgumentOutOfRangeException(nameof(index));
        var steps = new List<Step>();
        void Calculation(int left, string operation, int right, int expected)
        {
            steps.Add(new("clearButton"));
            steps.AddRange(left.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(digit => new Step($"num{digit}Button")));
            steps.Add(new(operation));
            steps.AddRange(right.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(digit => new Step($"num{digit}Button")));
            steps.Add(new("equalButton", expected));
        }
        Calculation(10 + index, "plusButton", 7 + index, 17 + 2 * index);
        Calculation(20 + index, "multiplyButton", 3, 60 + 3 * index);
        Calculation(100 + index, "minusButton", index, 100);
        return steps.ToArray();
    }
}
