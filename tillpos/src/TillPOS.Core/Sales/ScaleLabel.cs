using System.Globalization;

namespace TillPOS.Core.Sales;

/// <summary>A scale-printed EAN-13 label: '2' + item code (6) + value (5) + check digit. The value is grams for items sold by weight
/// and a piece count otherwise (the cart decides from the line's unit),
/// e.g. 2000089007400 = item code 000089, 740 g (Kg item); 2000060000017 = item code 000060, 1 piece (PCS item).</summary>
public sealed record ScaleLabel(string Code, int Value)
{
    /// <summary>Barcodes looked up in the database, in order: the first 7 digits (2000089), then digits 2–7 (000089).</summary>
    public IReadOnlyList<string> LookupKeys => [Code[..7], Code[1..7]];

    public static bool IsScaleLabelShape(string code) =>
        code.Length == 13 && code[0] == '2' && code.All(char.IsAsciiDigit);

    /// <summary>Null when the code is not a scale label, its check digit is wrong, or the value is zero.</summary>
    public static ScaleLabel? TryParse(string code)
    {
        if (!IsScaleLabelShape(code) || !HasValidCheckDigit(code)) return null;
        var value = int.Parse(code.AsSpan(7, 5), CultureInfo.InvariantCulture);
        return value == 0 ? null : new ScaleLabel(code, value);
    }

    public static bool HasValidCheckDigit(string ean13)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (ean13[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == ean13[12] - '0';
    }
}
