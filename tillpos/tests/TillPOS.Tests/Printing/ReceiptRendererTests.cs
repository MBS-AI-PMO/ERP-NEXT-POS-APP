using TillPOS.Core.Sales;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class ReceiptRendererTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Nuaimiya 1, Al Ain Market, Ajman, UAE", "100000000000003",
        "Till 2", "Thank you for shopping with us", "+971 6 000 0000");

    private static Receipt Sale(ReceiptKind kind = ReceiptKind.Sale, string? returnAgainst = null) => new(
        "TILL2-20261006153000-000001", kind, returnAgainst, "S1", "simran", new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4)),
        [
            new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, 2m, M("6.79"), M("6.79"), M("13.580"), null, null, false, null),
            new ReceiptLine(2, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, M("0.740"), M("3.50"), M("3.50"), M("2.590"), null, null, true, null),
        ],
        M("16.170"), M("15.400"), M("0.770"), M("16.170"), true, M("16.250"), M("0.080"),
        [new ReceiptPayment("Cash Counter 2", 20m)], M("3.75"), M("0.080"), null) { CashierName = "Test Cashier" };

    /// <summary>A 60-character item name, a long weighed quantity, an offer and a 30-character payment mode.</summary>
    private static Receipt Long(ReceiptKind kind = ReceiptKind.Sale, string? returnAgainst = null) => Sale(kind, returnAgainst) with
    {
        Lines =
        [
            new ReceiptLine(1, "X", "EXTRA LARGE FAMILY PACK BASMATI RICE PREMIUM QUALITY 10KG BAG", null, "PCS", 1m, 2m, M("6.79"),
                M("6.54"), M("13.080"), "OFFER", null, false, null),
            new ReceiptLine(2, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, M("123.456"), M("3.50"), M("3.50"), M("432.096"),
                null, null, true, null),
        ],
        Payments = [new ReceiptPayment("Cash Counter 2 Main Entrance A", 500m)],
    };

    private static List<string> Text(Receipt r, PaperWidth paper = PaperWidth.Mm80, ReceiptHeader? header = null) =>
        ReceiptRenderer.TextLines(r, header ?? Header, paper).ToList();

    private static void AssertInOrder(IReadOnlyList<string> lines, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = lines.Select((l, i) => (l, i)).FirstOrDefault(x => x.i > at && x.l.Contains(part, StringComparison.Ordinal), (null!, -1)).Item2;
            Assert.True(next > at, $"'{part}' not found after line {at}:\n{string.Join("\n", lines)}");
            at = next;
        }
    }

    [Fact]
    public void Sale_receipt_has_the_tax_invoice_parts_in_order()
    {
        var lines = Text(Sale());

        AssertInOrder(lines,
            "AL AIN MARKETING L.L.C", "Nuaimiya 1, Al Ain Market, Ajman, UAE", "Tel: +971 6 000 0000", "TRN: 100000000000003",
            "TAX INVOICE", "Invoice No: TILL2-20261006153000-000001", "Date      : 06/10/2026 15:30", "Cashier   : Test Cashier",
            "Till      : Till 2", "Item", "FULL CREAM MILK 1L", "CUCUMBER/KIYAR", "Items: 2", "Total excl. VAT", "VAT 5%", "TOTAL AED",
            "Rounding", "AMOUNT DUE", "Cash Counter 2", "Change", "Thank you for shopping with us", "Prices include 5% VAT");
        Assert.Contains(lines, l => l.StartsWith("Item") && l.EndsWith("Qty     Price    Amount"));
        Assert.Contains(lines, l => l.EndsWith("2      6.79     13.58"));
        Assert.Contains(lines, l => l.EndsWith("0.740 Kg      3.50      2.59"));
        Assert.Contains(lines, l => l.StartsWith("Total excl. VAT") && l.EndsWith(" 15.40"));
        Assert.Contains(lines, l => l.StartsWith("VAT 5%") && l.EndsWith(" 0.77"));
        Assert.Contains(lines, l => l.StartsWith("TOTAL AED") && l.EndsWith(" 16.17"));
        Assert.Contains(lines, l => l.StartsWith("Change") && l.EndsWith(" 3.75"));
    }

    [Fact]
    public void Phone_is_printed_under_the_address_only_when_set()
    {
        var with = Text(Sale());
        var address = with.FindIndex(l => l.Contains("Ajman"));
        Assert.Contains("Tel: +971 6 000 0000", with[address + 1]);
        Assert.DoesNotContain(Text(Sale(), header: Header with { Phone = null }), l => l.Contains("Tel:"));
    }

    [Fact]
    public void Without_a_cashier_name_the_cashier_id_is_printed() =>
        Assert.Contains("Cashier   : simran", Text(Sale() with { CashierName = null }));

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void No_line_is_wider_than_the_paper_and_titles_fit_half(PaperWidth paper)
    {
        var columns = (int)paper;
        foreach (var receipt in new[] { Sale(), Long(), Long(ReceiptKind.Return, "TILL2-20261006120000-000000") })
        {
            var layout = ReceiptRenderer.Layout(receipt, Header, paper);
            Assert.All(layout, l => Assert.True(l.Text.Length <= columns, $"[{l.Text}]"));
            Assert.All(layout.Where(l => l.Style == LineStyle.Title), l => Assert.True(l.Text.Length <= columns / 2, $"[{l.Text}]"));
        }
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Long_values_wrap_instead_of_being_cut(PaperWidth paper)
    {
        var lines = Text(Long(ReceiptKind.Return, "TILL2-20261006120000-000000"), paper);
        var all = string.Join(" ", lines.Select(l => l.Trim()));

        Assert.Contains("TILL2-20261006153000-000001", all);
        Assert.Contains("TILL2-20261006120000-000000", all);
        Assert.Contains("EXTRA LARGE FAMILY PACK BASMATI RICE PREMIUM QUALITY 10KG BAG", all);
        Assert.Contains(lines, l => l.Contains("123.456 Kg"));
        Assert.Contains(lines, l => l.EndsWith(" 432.096"));
    }

    [Fact]
    public void Qty_price_and_amount_right_edges_line_up_at_80mm()
    {
        var lines = Text(Long());
        var header = lines.FindIndex(l => l.StartsWith("Item") && l.Contains("Amount"));
        var end = lines.FindIndex(header + 2, l => l.StartsWith("---"));
        var rows = lines.Skip(header + 2).Take(end - header - 2).Where(l => l.StartsWith(' ') && !l.TrimStart().StartsWith("Offer")).ToList();

        Assert.Equal(2, rows.Count);
        foreach (var row in rows.Append(lines[header]))
        {
            Assert.Equal(48, row.Length);
            Assert.True(row[27] != ' ' && row[28] == ' ', $"Qty edge: [{row}]");
            Assert.True(row[37] != ' ' && row[38] == ' ', $"Price edge: [{row}]");
        }
    }

    [Fact]
    public void Item_rows_end_at_the_paper_edge_at_58mm()
    {
        var lines = Text(Long(), PaperWidth.Mm58);
        Assert.Contains(lines, l => l.Length == 32 && l.EndsWith(" 13.08") && l.Contains("2 x 6.54"));
        Assert.Contains(lines, l => l.Length == 32 && l.EndsWith(" 432.096") && l.Contains("123.456 Kg x 3.50"));
    }

    [Fact]
    public void Offer_and_you_saved_appear_only_when_a_line_is_below_its_list_price()
    {
        var plain = Text(Sale());
        Assert.DoesNotContain(plain, l => l.Contains("Offer") || l.Contains("You saved"));

        var offer = Text(Long());
        var name = offer.FindLastIndex(l => l.Contains("10KG BAG"));
        Assert.Equal("  Offer: -0.50", offer[name + 2]);
        Assert.Contains(offer, l => l.StartsWith("You saved") && l.EndsWith(" 0.50"));
    }

    [Fact]
    public void Layout_styles_title_total_and_company()
    {
        var layout = ReceiptRenderer.Layout(Sale(), Header, PaperWidth.Mm80);

        var title = Assert.Single(layout, l => l.Style == LineStyle.Title);
        Assert.Equal("TAX INVOICE", title.Text);
        Assert.Contains(layout, l => l.Style == LineStyle.Big && l.Text.StartsWith("TOTAL AED"));
        Assert.Contains(layout, l => l.Style == LineStyle.Big && l.Text.StartsWith("AMOUNT DUE"));
        Assert.Equal(LineStyle.Bold, layout[0].Style);
        Assert.Contains("AL AIN MARKETING L.L.C", layout[0].Text);
        Assert.Equal(ReceiptRenderer.TextLines(Sale(), Header, PaperWidth.Mm80).Select(l => l.Trim()), layout.Select(l => l.Text.Trim()));
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Text_receipt_centres_the_title_across_the_paper(PaperWidth paper)
    {
        var width = (int)paper;
        var title = Assert.Single(Text(Sale(), paper), l => l.Contains("TAX INVOICE"));
        Assert.Equal(new string(' ', (width - "TAX INVOICE".Length) / 2) + "TAX INVOICE", title);
        Assert.Equal("TAX INVOICE", Assert.Single(ReceiptRenderer.Layout(Sale(), Header, paper), l => l.Style == LineStyle.Title).Text);
    }

    [Fact]
    public void Return_receipt_is_a_tax_credit_note_referencing_the_sale()
    {
        var layout = ReceiptRenderer.Layout(Sale(ReceiptKind.Return, "TILL2-20261006120000-000000"), Header, PaperWidth.Mm80);
        var text = string.Join("\n", layout.Select(l => l.Text));

        Assert.Equal("CREDIT NOTE", Assert.Single(layout, l => l.Style == LineStyle.Title).Text);
        Assert.Contains("Tax Credit Note", text);
        Assert.Contains("Return of : TILL2-20261006120000-000000", text);
        Assert.DoesNotContain("TAX INVOICE", text);
    }

    [Fact]
    public void Escpos_output_prints_styles_and_resets_before_the_cut()
    {
        var bytes = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: false);

        Assert.True(EscPosTests.Contains(bytes, [0x1D, 0x21, 0x11]));              // title: double width + height
        Assert.True(EscPosTests.Contains(bytes, [0x1D, 0x21, 0x01]));              // big: double height
        Assert.True(EscPosTests.Contains(bytes, [0x1B, 0x45, 0x01]));              // bold
        Assert.True(EscPosTests.Contains(bytes, [0x1B, 0x61, 0x01]));              // centred title
        var cut = LastIndexOf(bytes, [0x1D, 0x56, 0x42, 0x00]);
        var lastSize = LastIndexOf(bytes, [0x1D, 0x21]);
        Assert.True(lastSize >= 0 && lastSize < cut);
        Assert.Equal(0x00, bytes[lastSize + 2]);
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

        var lines = Text(split);

        Assert.Contains(lines, l => l.StartsWith("Rounding") && l.EndsWith(" 0.08"));
        Assert.Contains(lines, l => l.StartsWith("AMOUNT DUE") && l.EndsWith(" 16.25"));
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

        var text = string.Join("\n", Text(card));

        Assert.DoesNotContain("Rounding", text);
        Assert.DoesNotContain("AMOUNT DUE", text);
        Assert.DoesNotContain("Change", text);
    }

    [Fact]
    public void Without_a_trn_there_is_no_qr_code_and_no_trn_line()
    {
        var header = Header with { Trn = null };
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), header, PaperWidth.Mm80, false), [0x1D, 0x28, 0x6B]));
        Assert.DoesNotContain(Text(Sale(), header: header), l => l.Contains("TRN"));
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
