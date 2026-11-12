using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HelloWinUI;

internal static class CalculatorView
{
    public static StackPanel Create(int index, TextBlock telemetry)
    {
        var model = new CalculatorModel();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"Sample calculator {index:00}", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "Our unpackaged WinUI demo - not Windows Calculator", FontSize = 12 });
        var expression = new TextBlock { Text = "Ready", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right };
        var result = new TextBlock { Text = "0", FontSize = 42, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(result, "CalculatorResults");
        var display = new StackPanel { Spacing = 2 };
        display.Children.Add(expression); display.Children.Add(result);
        panel.Children.Add(new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = display });
        var body = new Grid { ColumnSpacing = 16 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var keypad = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (int i = 0; i < 4; i++)
        {
            keypad.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            keypad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var history = new StackPanel { Spacing = 10 };
        history.Children.Add(new TextBlock { Text = "Calculation history", FontSize = 14 });
        history.Children.Add(new TextBlock { Text = "UI Automation will press the same buttons you can.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var entries = new StackPanel { Spacing = 12 };
        history.Children.Add(entries);
        var keys = new (string Label, string Id)[] {
            ("7", "num7Button"), ("8", "num8Button"), ("9", "num9Button"), ("/", "divideButton"),
            ("4", "num4Button"), ("5", "num5Button"), ("6", "num6Button"), ("x", "multiplyButton"),
            ("1", "num1Button"), ("2", "num2Button"), ("3", "num3Button"), ("-", "minusButton"),
            ("C", "clearButton"), ("0", "num0Button"), ("=", "equalButton"), ("+", "plusButton")
        };
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var command = new XamlUICommand();
            command.ExecuteRequested += (_, _) =>
            {
                try
                {
                    model.Press(key.Id);
                    expression.Text = model.Expression.Length == 0 ? "Ready" : model.Expression;
                    result.Text = model.Display;
                    if (model.Completed is not null)
                    {
                        entries.Children.Add(new TextBlock { Text = model.Completed, FontSize = 16, TextWrapping = TextWrapping.Wrap });
                        if (entries.Children.Count > 3) entries.Children.RemoveAt(0);
                    }
                }
                catch (Exception error) when (error is DivideByZeroException or OverflowException)
                {
                    result.Text = "Error";
                    expression.Text = error is DivideByZeroException ? "Cannot divide by zero - press C" : "Number too large - press C";
                    model.Press("clearButton");
                }
            };
            var button = new Button { Content = key.Label, Command = command, Height = 44, FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, key.Id);
            if (key.Id == "equalButton") button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Grid.SetRow(button, i / 4); Grid.SetColumn(button, i % 4); keypad.Children.Add(button);
        }
        body.Children.Add(keypad); Grid.SetColumn(history, 1); body.Children.Add(history);
        panel.Children.Add(body); panel.Children.Add(telemetry);
        return panel;
    }
}
