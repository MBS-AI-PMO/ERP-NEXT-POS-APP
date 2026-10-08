using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class DeliveryPrintTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Ajman, UAE", "100000000000003", "Till 2", "Thank you");
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 10, 0, TimeSpan.FromHours(4));

    private static Receipt Bill(string id = "TILL2-20261008151000-000042") => new(id, ReceiptKind.Sale, null, "S1", "simran", At,
        [new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, 1m, M("11.429"), M("11.429"), M("11.429"), null, null, false, null)],
        M("11.429"), M("10.885"), M("0.544"), M("11.429"), false, 0m, 0m, [], 0m, 0m, null) { IsDelivery = true, CashierName = "Simran" };

    private static Delivery Slip() => new("TILL2-20261008151000-000042", DeliveryStatus.Open, At, "S1", "simran", "Simran", "Counter 2",
        Bill(), M("11.50"), M("11.43"));

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void The_slip_says_not_paid_shows_both_amounts_to_collect_and_has_a_barcode_but_no_qr(PaperWidth paper)
    {
        var layout = ReceiptRenderer.DeliverySlipLayout(Slip(), Header, paper);
        var text = ReceiptRenderer.DeliverySlipTextLines(Slip(), Header, paper);

        Assert.Equal("DELIVERY INVOICE", string.Join(" ", layout.Where(l => l.Style == LineStyle.Title).Select(l => l.Text)));
        Assert.Contains(text, l => l.Contains("NOT PAID"));
        Assert.Contains(text, l => l.Contains("AMOUNT TO COLLECT"));
        Assert.Contains(text, l => l.TrimStart().StartsWith("Cash") && l.EndsWith(" 11.50"));
        Assert.Contains(text, l => l.TrimStart().StartsWith("Card") && l.EndsWith(" 11.43"));
        Assert.Equal("TILL2-20261008151000-000042", Assert.Single(layout, l => l.Style == LineStyle.Barcode).Text);
        Assert.DoesNotContain(layout, l => l.Style == LineStyle.Qr);
        Assert.DoesNotContain(text, l => l.Contains("TAX INVOICE") || l.StartsWith("Change"));
        Assert.All(layout.Where(l => l.Style is LineStyle.Normal or LineStyle.Bold), l => Assert.True(l.Text.Length <= (int)paper, l.Text));
    }

    [Fact]
    public void A_reprinted_slip_is_marked_copy_and_never_kicks_the_drawer()
    {
        Assert.Contains("*** COPY ***", ReceiptRenderer.DeliverySlipTextLines(Slip(), Header, PaperWidth.Mm80, copy: true).Select(l => l.Trim()));
        var bytes = ReceiptRenderer.DeliverySlipEscPosBytes(Slip(), Header, PaperWidth.Mm80);
        Assert.False(EscPosTests.Contains(bytes, [0x1B, 0x70, 0x00, 0x19, 0xFA]));
    }

    [Fact]
    public void A_paid_delivery_prints_as_a_tax_invoice_marked_delivery_paid()
    {
        var paid = Bill() with { Payments = [new ReceiptPayment("Cash Counter 2", M("11.50"))], UsesErpRoundedTotal = true, RoundedTotal = M("11.50") };
        var text = ReceiptRenderer.TextLines(paid, Header, PaperWidth.Mm80).Select(l => l.Trim()).ToList();

        var title = text.IndexOf("TAX INVOICE");
        var marker = text.IndexOf("DELIVERY - PAID");
        Assert.True(title >= 0 && marker > title && marker < text.FindIndex(l => l.StartsWith("Invoice No")), string.Join("\n", text));
        Assert.DoesNotContain(text, l => l.Contains("NOT PAID"));
        Assert.DoesNotContain(ReceiptRenderer.TextLines(Bill() with { IsDelivery = false }, Header, PaperWidth.Mm80), l => l.Contains("DELIVERY - PAID"));
    }

    [Fact]
    public void The_z_report_lists_deliveries_paid_and_still_out()
    {
        var opening = new ShiftOpening("TILL2-SHIFT-1", "simran", "", At.AddHours(-8), [new ReceiptPayment("Cash Counter 2", 200m)]);
        var closing = new ShiftClosing("TILL2-SHIFT-1", At, [new ShiftModeSummary("Cash Counter 2", 200m, 211.5m, 211.5m, 0m)], 3, 0,
            M("45.72"), M("43.54"), M("2.18"));
        var summary = new DeliverySummary(2, M("34.29"), [Slip(), Slip() with { ClientId = "TILL2-20261008162210-000043" }]);

        var text = ShiftReportRenderer.TextLines(opening, closing, Header, "Simran", "Sup", PaperWidth.Mm80, null, summary);

        Assert.Contains(text, l => l.StartsWith("Deliveries paid (2)") && l.EndsWith(" 34.29"));
        Assert.Contains(text, l => l.StartsWith("DELIVERIES STILL OUT"));
        Assert.Contains(text, l => l.StartsWith("TILL2-20261008151000-000042") && l.EndsWith(" 11.43"));
        Assert.Contains(text, l => l.StartsWith("Total to collect") && l.EndsWith(" 22.86"));
        Assert.DoesNotContain(ShiftReportRenderer.TextLines(opening, closing, Header, "Simran", "Sup", PaperWidth.Mm80), l => l.Contains("DELIVER"));
    }
}
