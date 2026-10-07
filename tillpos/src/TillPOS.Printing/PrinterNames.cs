namespace TillPOS.Printing;

/// <summary>Printer choice on the first start: never preselect a "printer" that only makes files (PDF, XPS, OneNote…),
/// and prefer an installed receipt printer over the Windows default.</summary>
public static class PrinterNames
{
    private static readonly string[] VirtualHints = ["PDF", "XPS", "OneNote", "Fax", "AnyDesk", "Send To", "Print to", "Snagit", "Adobe"];
    private static readonly string[] ReceiptHints = ["POS", "Receipt", "Thermal", "TM-", "XP-", "RP-"];

    /// <summary>True for Windows' file and app "printers" (Microsoft Print to PDF, XPS Document Writer, Fax, OneNote…).</summary>
    public static bool LooksVirtual(string name) => ContainsAny(name, VirtualHints);

    /// <summary>The first installed receipt-looking printer (POS-80C, EPSON TM-T20…), else the Windows default unless it is
    /// virtual, else null (receipts are saved as files).</summary>
    public static string? PickDefault(IReadOnlyList<string> installed, string? windowsDefault)
    {
        var receipt = installed.FirstOrDefault(n => !LooksVirtual(n) && ContainsAny(n, ReceiptHints));
        if (receipt is not null) return receipt;
        return string.IsNullOrWhiteSpace(windowsDefault) || LooksVirtual(windowsDefault) ? null : windowsDefault;
    }

    private static bool ContainsAny(string name, string[] hints) =>
        hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
}
