using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TillPOS.Presentation;

/// <summary>An amount typed on the keypad or keyboard: digits, one dot, at most 3 decimals, at most 7 whole digits.
/// A whole-number entry (a count) refuses the dot.</summary>
public sealed partial class NumericEntry(bool wholeNumbers = false) : ObservableObject
{
    private string text = "";
    private bool replaceOnNextInput;

    public bool WholeNumbers { get; } = wholeNumbers;

    public event Action? Changed;

    /// <summary>True while a prefilled value is untouched: the next digit or dot starts a new amount instead of extending it
    /// (the view also selects the text so typing on the keyboard replaces it). Any edit, Set or Clear ends it.</summary>
    public bool ReplaceOnNextInput => replaceOnNextInput;

    public string Text
    {
        get => text;
        set
        {
            if (!Valid().IsMatch(value) || (WholeNumbers && value.Contains('.'))) return;
            replaceOnNextInput = false;
            if (!SetProperty(ref text, value)) return;
            OnPropertyChanged(nameof(Value));
            Changed?.Invoke();
        }
    }

    public decimal? Value => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Digit(char digit)
    {
        if (!char.IsAsciiDigit(digit)) return;
        var current = TakeBase();
        Text = current == "0" ? digit.ToString() : current + digit;
    }

    public void Dot()
    {
        if (WholeNumbers) return;
        var current = TakeBase();
        if (!current.Contains('.')) Text = current.Length == 0 ? "0." : current + ".";
    }

    public void Backspace()
    {
        if (text.Length > 0) Text = text[..^1];
    }

    public void Clear() => Text = "";

    public void Set(decimal value)
    {
        Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        replaceOnNextInput = false;
    }

    /// <summary>Sets a suggested value that the next typed digit or dot replaces.</summary>
    public void Prefill(decimal value)
    {
        Set(value);
        replaceOnNextInput = true;
    }

    private string TakeBase()
    {
        if (!replaceOnNextInput) return text;
        replaceOnNextInput = false;
        return "";
    }

    [GeneratedRegex(@"^\d{0,7}(\.\d{0,3})?$")]
    private static partial Regex Valid();
}
