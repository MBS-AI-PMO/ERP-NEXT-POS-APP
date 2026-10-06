using TillPOS.Core.Sales;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class ReceiptRendererTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Shop 4, Al Quoz, Ajman", "100000000000003", "Till 2",
        "Thank you for shopping with us");

    private static Receipt Sale(ReceiptKind kind = ReceiptKind.Sale, string? returnAgainst = null) => new(
        "TILL2-20261006153000-000001", kind, returnAgainst, "S1", "simran", new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4)),
        [
            new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, 2m, M("6.79"), M("6.79"), M("13.580"), null, null, false, null),
            new ReceiptLine(2, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, M("0.740"), M("3.50"), M("3.50"), M("2.590"), null, null, true, null),
        ],
        M("16.170"), M("15.400"), M("0.770"), M("16.170"), true, M("16.250"), M("0.080"),
        [new ReceiptPayment("Cash Counter 2", 20m)], M("3.75"), M("0.080"), null);

    [Fact]
    public void Phone_is_printed_under_the_address_only_when_set()
    {
        var with = ReceiptRenderer.TextLines(Sale(), Header with { Phone = "+971 6 000 0000" }, PaperWidth.Mm80);
        var address = with.ToList().FindIndex(l => l.Contains("Ajman"));
        Assert.Contains("Tel: +971 6 000 0000", with[address + 1]);
        Assert.DoesNotContain(ReceiptRenderer.TextLines(Sale(), Header, PaperWidth.Mm80), l => l.Contains("Tel:"));
    }

    [Fact]
    public void Sale_receipt_has_the_tax_invoice_parts()
    {
        var text = string.Join("\n", ReceiptRenderer.TextLines(Sale(), Header, PaperWidth.Mm80));

        Assert.Contains("TAX INVOICE", text);
        Assert.Contains("AL AIN MARKETING L.L.C", text);
        Assert.Contains("TRN: 100000000000003", text);
        Assert.Contains("TILL2-20261006153000-000001", text);
        Assert.Contains("FULL CREAM MILK 1L", text);
        Assert.Contains("  2 x 6.79", text);
        Assert.Contains("  0.740 Kg x 3.50", text);
        Assert.Contains("13.58", text);
        Assert.Contains("2.59", text);
        Assert.Contains("VAT included", text);
        Assert.Contains("Amount due", text);
        Assert.Contains("16.25", text);
        Assert.Contains("Change", text);
        Assert.Contains("Thank you for shopping with us", text);
    }

    [Theory]
    [InlineData(PaperWidth.Mm80, 48)]
    [InlineData(PaperWidth.Mm58, 32)]
    public void No_line_is_wider_than_the_paper(PaperWidth paper, int columns) =>
        Assert.All(ReceiptRenderer.TextLines(Sale(), Header, paper), line => Assert.True(line.Length <= columns, line));

    [Fact]
    public void Return_receipt_is_a_tax_credit_note_referencing_the_sale()
    {
        var text = string.Join("\n", ReceiptRenderer.TextLines(Sale(ReceiptKind.Return, "TILL2-20261006120000-000000"), Header, PaperWidth.Mm80));
        Assert.Contains("TAX CREDIT NOTE", text);
        Assert.Contains("TILL2-20261006120000-000000", text);
        Assert.DoesNotContain("TAX INVOICE", text);
    }

    [Fact]
    public void Escpos_output_has_qr_cut_and_drawer_only_when_asked()
    {
        var withDrawer = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: true);
        var without = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: false);

        Assert.True(EscPosTests.Contains(withDrawer, [0x1D, 0x28, 0x6B]));
        Assert.True(EscPosTests.Contains(without, [0x1D, 0x56, 0x42, 0x00]));
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, withDrawer[^5..]);
        Assert.False(EscPosTests.Contains(without, [0x1B, 0x70, 0x00, 0x19, 0xFA]));
    }

    [Fact]
    public void Qr_total_is_the_grand_total_even_when_cash_was_rounded()
    {
        var receipt = Sale();
        var bytes = ReceiptRenderer.EscPosBytes(receipt, Header, PaperWidth.Mm80, false);

        var expected = FtaQr.Encode(Header.CompanyName, Header.Trn!, receipt.CreatedAt, receipt.GrandTotal, receipt.TotalTaxes);
        var rounded = FtaQr.Encode(Header.CompanyName, Header.Trn!, receipt.CreatedAt, receipt.RoundedTotal, receipt.TotalTaxes);
        Assert.True(EscPosTests.Contains(bytes, System.Text.Encoding.ASCII.GetBytes(expected)));
        Assert.False(EscPosTests.Contains(bytes, System.Text.Encoding.ASCII.GetBytes(rounded)));
    }

    [Fact]
    public void Split_receipt_with_a_rounding_difference_shows_rounding_and_amount_due()
    {
        var split = Sale() with
        {
            UsesErpRoundedTotal = false,
            RoundedTotal = 0m,
            RoundingAdjustment = 0m,
            Payments = [new ReceiptPayment("Credit Card", 10m), new ReceiptPayment("Cash Counter 2", 10m)],
            Change = M("3.75"),
            RoundingDifference = M("0.080"),
        };

        var lines = ReceiptRenderer.TextLines(split, Header, PaperWidth.Mm80);

        Assert.Contains(lines, l => l.StartsWith("Rounding") && l.EndsWith(" 0.08"));
        Assert.Contains(lines, l => l.StartsWith("Amount due") && l.EndsWith(" 16.25"));
    }

    [Fact]
    public void Card_receipt_has_no_rounding_lines()
    {
        var card = Sale() with
        {
            UsesErpRoundedTotal = false,
            RoundedTotal = 0m,
            RoundingAdjustment = 0m,
            Payments = [new ReceiptPayment("Credit Card", M("16.170"))],
            Change = 0m,
            RoundingDifference = 0m,
        };

        var text = string.Join("\n", ReceiptRenderer.TextLines(card, Header, PaperWidth.Mm80));

        Assert.DoesNotContain("Rounding", text);
        Assert.DoesNotContain("Amount due", text);
    }

    [Fact]
    public void Without_a_trn_there_is_no_qr_code() =>
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), Header with { Trn = null }, PaperWidth.Mm80, false),
            [0x1D, 0x28, 0x6B]));
}
