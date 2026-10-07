using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TillPOS.Presentation;

/// <summary>An amount typed on the keypad or keyboard: digits, one dot, at most 3 decimals, at most 7 whole digits.
/// A whole-number entry (a count) refuses the dot.</summary>
public sealed partial class NumericEntry(bool wholeNumbers = false) : ObservableObject
{
    private string text = "";

    public bool WholeNumbers { get; } = wholeNumbers;

    public event Action? Changed;

    public string Text
    {
        get => text;
        set
        {
            if (!Valid().IsMatch(value) || (WholeNumbers && value.Contains('.'))) return;
            if (!SetProperty(ref text, value)) return;
            OnPropertyChanged(nameof(Value));
            Changed?.Invoke();
        }
    }

    public decimal? Value => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Digit(char digit)
    {
        if (!char.IsAsciiDigit(digit)) return;
        Text = text == "0" ? digit.ToString() : text + digit;
    }

    public void Dot()
    {
        if (!WholeNumbers && !text.Contains('.')) Text = text.Length == 0 ? "0." : text + ".";
    }

    public void Backspace()
    {
        if (text.Length > 0) Text = text[..^1];
    }

    public void Clear() => Text = "";

    public void Set(decimal value) => Text = value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d{0,7}(\.\d{0,3})?$")]
    private static partial Regex Valid();
}
