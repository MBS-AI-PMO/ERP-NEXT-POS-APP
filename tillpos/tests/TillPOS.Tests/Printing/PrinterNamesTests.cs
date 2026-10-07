using TillPOS.Printing;

namespace TillPOS.Tests.Printing;

public class PrinterNamesTests
{
    [Theory]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("Microsoft XPS Document Writer")]
    [InlineData("OneNote (Desktop)")]
    [InlineData("Send To OneNote 2016")]
    [InlineData("Fax")]
    [InlineData("AnyDesk Printer")]
    [InlineData("Snagit 2024")]
    [InlineData("Adobe PDF")]
    [InlineData("print TO file")]
    [InlineData("microsoft print to pdf")]
    public void Virtual_printers_are_recognised(string name) => Assert.True(PrinterNames.LooksVirtual(name));

    [Theory]
    [InlineData("POS-80C")]
    [InlineData("EPSON TM-T20II Receipt")]
    [InlineData("HP LaserJet Pro M404")]
    [InlineData("XP-80C")]
    public void Real_printers_are_not_virtual(string name) => Assert.False(PrinterNames.LooksVirtual(name));

    [Fact]
    public void Receipt_printer_beats_a_virtual_default() =>
        Assert.Equal("POS-80C", PrinterNames.PickDefault(["Microsoft Print to PDF", "POS-80C", "HP LaserJet"], "Microsoft Print to PDF"));

    [Fact]
    public void Only_a_virtual_printer_gives_none() =>
        Assert.Null(PrinterNames.PickDefault(["Microsoft Print to PDF"], "Microsoft Print to PDF"));

    [Fact]
    public void Real_default_is_kept_when_no_receipt_printer_is_installed() =>
        Assert.Equal("HP LaserJet", PrinterNames.PickDefault(["HP LaserJet"], "HP LaserJet"));

    [Fact]
    public void Receipt_printer_beats_a_real_default() =>
        Assert.Equal("XP-80C", PrinterNames.PickDefault(["HP LaserJet", "XP-80C"], "HP LaserJet"));

    [Theory]
    [InlineData("EPSON TM-T20II")]
    [InlineData("Rongta RP-80")]
    [InlineData("Thermal Printer 80")]
    [InlineData("Receipt1")]
    [InlineData("pos58")]
    public void Receipt_hints_are_recognised(string name) =>
        Assert.Equal(name, PrinterNames.PickDefault(["Microsoft Print to PDF", name], null));

    [Fact]
    public void First_receipt_printer_in_the_list_wins() =>
        Assert.Equal("POS-58", PrinterNames.PickDefault(["POS-58", "TM-T88"], null));

    [Fact]
    public void Virtual_printer_with_a_receipt_hint_is_not_picked() =>
        Assert.Null(PrinterNames.PickDefault(["Print to PDF (POS)"], null));

    [Fact]
    public void No_default_and_no_receipt_printer_gives_none()
    {
        Assert.Null(PrinterNames.PickDefault(["HP LaserJet"], null));
        Assert.Null(PrinterNames.PickDefault([], null));
    }
}
