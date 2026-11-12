using System.Globalization;

namespace HelloWinUI;

internal sealed class CalculatorModel
{
    private decimal _left;
    private string? _operation;
    private bool _replace = true;
    public string Display { get; private set; } = "0";
    public string Expression { get; private set; } = "";
    public string? Completed { get; private set; }

    public void Press(string button)
    {
        Completed = null;
        if (button == "clearButton")
        {
            Display = "0"; Expression = ""; _left = 0; _operation = null; _replace = true;
            return;
        }
        if (button.Length == 10 && button.StartsWith("num") && button.EndsWith("Button") && char.IsAsciiDigit(button[3]))
        {
            string value = _replace || Display == "0" ? button[3].ToString() : Display + button[3];
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                throw new OverflowException("Number is too large");
            Display = value; _replace = false;
            return;
        }
        decimal current = decimal.Parse(Display, CultureInfo.InvariantCulture);
        if (button is "plusButton" or "minusButton" or "multiplyButton" or "divideButton")
        {
            _left = current; _operation = button; _replace = true;
            Expression = $"{Display} {Symbol(button)}";
            return;
        }
        if (button != "equalButton") throw new ArgumentException("Unsupported calculator button", nameof(button));
        if (_operation is null) return;
        decimal result = _operation switch
        {
            "plusButton" => _left + current,
            "minusButton" => _left - current,
            "multiplyButton" => _left * current,
            "divideButton" => _left / current,
            _ => throw new InvalidOperationException("Unsupported calculator operation")
        };
        Expression = $"{_left.ToString(CultureInfo.InvariantCulture)} {Symbol(_operation)} {Display} =";
        Display = result.ToString("0.############################", CultureInfo.InvariantCulture);
        Completed = $"{Expression} {Display}";
        _operation = null; _replace = true;
    }

    private static string Symbol(string button) => button switch
    {
        "plusButton" => "+", "minusButton" => "-", "multiplyButton" => "x", "divideButton" => "/",
        _ => throw new ArgumentException("Unsupported operation", nameof(button))
    };
}
