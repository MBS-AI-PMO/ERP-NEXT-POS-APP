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
        CounterName = "Al Ain Hypermarket Main Entrance Express Counter 12",
    };

    /// <summary>A credit note as the till stores it: negative quantities, amounts, totals and payment. The milk line was sold
    /// on offer (rate below the list price).</summary>
    private static Receipt CreditNote() => Sale(ReceiptKind.Return, "TILL2-20261006120000-000000") with
    {
        Lines =
        [
            new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, -2m, M("6.79"), M("6.54"), M("-13.080"), "OFFER", null, false, null),
            new ReceiptLine(2, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, M("-0.740"), M("3.50"), M("3.50"), M("-2.590"),
                null, null, true, null),
        ],
        Total = M("-15.670"),
        NetTotal = M("-14.924"),
        TotalTaxes = M("-0.746"),
        GrandTotal = M("-15.670"),
        UsesErpRoundedTotal = false,
        RoundedTotal = 0m,
        RoundingAdjustment = 0m,
        Payments = [new ReceiptPayment("Cash Counter 2", M("-15.670"))],
        Change = 0m,
        RoundingDifference = 0m,
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
            "Rounding", "AMOUNT DUE", "Cash ", "Change", "Thank you for shopping with us", "Prices include 5% VAT");
        Assert.Contains(lines, l => l.StartsWith("Cash ") && l.EndsWith(" 20.00"));
        Assert.DoesNotContain(lines, l => l.Contains("Counter 2"));
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
            // A QR line's text is the QR payload, not paper text; the text file prints a placeholder for it.
            Assert.All(layout.Where(l => l.Style != LineStyle.Qr), l => Assert.True(l.Text.Length <= columns, $"[{l.Text}]"));
            Assert.All(Text(receipt, paper), l => Assert.True(l.Length <= columns, $"[{l}]"));
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
        Assert.Contains(lines, l => l.EndsWith(" 432.10"));                  // printed with 2 decimals
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
        Assert.Contains(lines, l => l.Length == 32 && l.EndsWith(" 432.10") && l.Contains("123.456 Kg x 3.50"));
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
        Assert.Equal(ReceiptRenderer.TextLines(Sale(), Header, PaperWidth.Mm80).Select(l => l.Trim()),
            layout.Select(l => l.Style == LineStyle.Qr ? "[QR code]" : l.Text.Trim()));
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

        Assert.Equal("TAX CREDIT NOTE", Assert.Single(layout, l => l.Style == LineStyle.Title).Text);
        Assert.Contains("Return of : TILL2-20261006120000-000000", text);
        Assert.DoesNotContain("TAX INVOICE", text);
        Assert.DoesNotContain("Return without receipt", text);
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Credit_note_with_negative_values_stays_inside_the_paper_and_aligned(PaperWidth paper)
    {
        var width = (int)paper;
        var layout = ReceiptRenderer.Layout(CreditNote(), Header, paper);
        var lines = Text(CreditNote(), paper);

        Assert.Equal("TAX CREDIT NOTE", Assert.Single(layout, l => l.Style == LineStyle.Title).Text);
        Assert.All(layout.Where(l => l.Style != LineStyle.Qr), l => Assert.True(l.Text.Length <= width, $"[{l.Text}]"));
        Assert.Contains(lines, l => l.StartsWith("VAT 5%") && l.EndsWith(" -0.75"));
        Assert.Contains(lines, l => l.StartsWith("TOTAL AED") && l.EndsWith(" -15.67"));
        Assert.Contains(lines, l => l.StartsWith("Refund paid (cash)") && l.EndsWith(" -15.67"));
        Assert.DoesNotContain(lines, l => l.StartsWith("Cash Counter 2"));
        Assert.DoesNotContain(lines, l => l.Contains("Offer") || l.Contains("You saved"));

        // Item rows and the column header share their right edges (Qty, Price at 80 mm; the amount at the paper edge).
        var header = lines.FindIndex(l => l.StartsWith("Item") && l.Contains("Amount"));
        var rows = new[] { lines.FindIndex(l => l.Contains("-2 ")), lines.FindIndex(l => l.Contains("-0.740 Kg")) }.Select(i => lines[i]).ToList();
        Assert.Contains(rows, r => r.EndsWith(" -13.08"));
        Assert.Contains(rows, r => r.EndsWith(" -2.59"));
        foreach (var row in rows.Append(lines[header]))
        {
            Assert.Equal(width, row.Length);
            if (paper == PaperWidth.Mm80)
            {
                Assert.True(row[27] != ' ' && row[28] == ' ', $"Qty edge: [{row}]");
                Assert.True(row[37] != ' ' && row[38] == ' ', $"Price edge: [{row}]");
            }
            else
            {
                Assert.True(row[22] != ' ' && row[23] == ' ', $"Qty x Price edge: [{row}]");
            }
        }
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Sale_has_a_centred_barcode_of_the_invoice_number_after_the_invoice_block(PaperWidth paper)
    {
        var width = (int)paper;
        var layout = ReceiptRenderer.Layout(Sale(), Header, paper).ToList();

        var barcode = Assert.Single(layout, l => l.Style == LineStyle.Barcode);
        Assert.Equal("TILL2-20261006153000-000001", barcode.Text);
        var index = layout.IndexOf(barcode);
        Assert.StartsWith("Till ", layout[index - 1].Text);
        Assert.Equal(new string('-', width), layout[index + 1].Text);

        var text = Text(Sale(), paper);
        Assert.Contains(new string(' ', (width - barcode.Text.Length) / 2) + barcode.Text, text);
    }

    [Fact]
    public void A_credit_note_has_no_barcode() =>
        Assert.DoesNotContain(ReceiptRenderer.Layout(CreditNote(), Header, PaperWidth.Mm80), l => l.Style == LineStyle.Barcode);

    [Theory]
    [InlineData(PaperWidth.Mm80, 2)]
    [InlineData(PaperWidth.Mm58, 1)]
    public void Escpos_prints_the_barcode_centred_with_a_module_width_that_fits_the_paper(PaperWidth paper, byte moduleWidth)
    {
        var bytes = ReceiptRenderer.EscPosBytes(Sale(), Header, paper, openDrawer: false);

        var expected = new EscPos().Align(Alignment.Center).Barcode128("TILL2-20261006153000-000001", moduleWidth).ToArray();
        Assert.Equal(1, Count(bytes, expected));
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(CreditNote(), Header, paper, openDrawer: false), [0x1D, 0x6B, 0x49]));
    }

    [Fact]
    public void Credit_note_shows_the_refund_reason_and_approver()
    {
        var note = CreditNote() with { Reason = "Damaged", ApprovedBy = "SUP-1" };

        var lines = Text(note);

        AssertInOrder(lines, "TAX CREDIT NOTE", "Invoice No", "Return of : TILL2-20261006120000-000000", "Till", "Reason    : Damaged",
            "Approved  : SUP-1", "TOTAL AED", "Refund paid (cash)");
        Assert.Contains(lines, l => l.StartsWith("Refund paid (cash)") && l.EndsWith(" -15.67"));
    }

    [Fact]
    public void Credit_note_without_a_receipt_says_so_and_omits_empty_reason_and_approver()
    {
        var lines = Text(CreditNote() with { ReturnAgainst = null });

        Assert.Contains("Return without receipt", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Return of") || l.StartsWith("Reason") || l.StartsWith("Approved"));
    }

    [Fact]
    public void A_sale_has_no_refund_reason_or_approver_lines()
    {
        var lines = Text(Sale() with { Reason = "x", ApprovedBy = "SUP-1" });
        Assert.DoesNotContain(lines, l => l.StartsWith("Reason") || l.StartsWith("Approved") || l.Contains("Refund paid"));
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
        AssertInOrder(lines, "Card (Visa/Master)", "Cash ", "Change");
    }

    [Fact]
    public void Payment_rows_print_cash_or_card_not_the_erpnext_mode_names()
    {
        var split = Sale() with { Payments = [new ReceiptPayment("Credit Card", 10m), new ReceiptPayment("Cash Counter 1", 10m)] };

        var lines = Text(split);

        Assert.Contains(lines, l => l.StartsWith("Card (Visa/Master)") && l.EndsWith(" 10.00"));
        Assert.Contains(lines, l => l.StartsWith("Cash ") && l.EndsWith(" 10.00"));
        Assert.DoesNotContain(lines, l => l.Contains("Credit Card") || l.Contains("Cash Counter"));
    }

    [Fact]
    public void The_counters_card_modes_decide_which_row_is_the_card()
    {
        var bill = Sale() with { Payments = [new ReceiptPayment("Network POS", 10m), new ReceiptPayment("Cash Counter 1", 10m)] };

        var lines = Text(bill, header: Header with { CardModes = ["Network POS"] });

        Assert.Contains(lines, l => l.StartsWith("Card (Visa/Master)") && l.EndsWith(" 10.00"));
        Assert.Contains(lines, l => l.StartsWith("Cash ") && l.EndsWith(" 10.00"));
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

        // A card bill keeps ERPNext's rounded total for the upload only: the customer still sees the exact amount.
        var exact = string.Join("\n", Text(card with { RoundedTotal = M("16.250"), RoundingAdjustment = M("0.080"), ExactCardOnRoundedTotal = true }));
        Assert.DoesNotContain("Rounding", exact);
        Assert.DoesNotContain("AMOUNT DUE", exact);
        Assert.Contains("Card (Visa/Master)", exact);
    }

    [Fact]
    public void Without_a_trn_there_is_no_qr_code_and_no_trn_line()
    {
        var header = Header with { Trn = null };
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), header, PaperWidth.Mm80, false), [0x1D, 0x28, 0x6B]));
        Assert.DoesNotContain(Text(Sale(), header: header), l => l.Contains("TRN"));
    }

    private const string SampleNote = "SAMPLE QR - FOR TESTING ONLY";
    private static readonly byte[] QrCommand = [0x1D, 0x28, 0x6B];
    private static readonly byte[] QrPrint = [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30];

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void With_a_trn_the_real_qr_is_the_last_line_and_has_no_sample_note(PaperWidth paper)
    {
        var receipt = Sale();
        var layout = ReceiptRenderer.Layout(receipt, Header with { SampleQr = true }, paper);

        var qr = Assert.Single(layout, l => l.Style == LineStyle.Qr);
        Assert.Equal(FtaQr.Encode(Header.CompanyName, Header.Trn!, receipt.CreatedAt, receipt.GrandTotal, receipt.TotalTaxes), qr.Text);
        Assert.Equal("100000000000003", FtaQrTests.Decode(qr.Text)[2]);
        Assert.Same(qr, layout[^1]);
        Assert.Contains("Prices include 5% VAT", layout[^2].Text);
        Assert.DoesNotContain(layout, l => l.Text.Contains(SampleNote));
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Without_a_trn_a_sample_qr_has_a_zero_trn_and_a_centred_sample_note(PaperWidth paper)
    {
        var layout = ReceiptRenderer.Layout(Sale(), Header with { Trn = null, SampleQr = true }, paper);

        var qr = Assert.Single(layout, l => l.Style == LineStyle.Qr);
        var fields = FtaQrTests.Decode(qr.Text);
        Assert.Equal(Header.CompanyName, fields[1]);
        Assert.Equal("000000000000000", fields[2]);
        Assert.Equal("2026-10-06T11:30:00Z", fields[3]);
        Assert.Equal("16.17", fields[4]);
        Assert.Equal("0.77", fields[5]);
        Assert.Same(qr, layout[^2]);
        Assert.Contains("Prices include 5% VAT", layout[^3].Text);
        Assert.Equal(new PrintLine(new string(' ', ((int)paper - SampleNote.Length) / 2) + SampleNote), layout[^1]);
        Assert.DoesNotContain(layout, l => l.Text.Contains("TRN"));
    }

    [Fact]
    public void Without_a_trn_or_the_sample_setting_there_is_no_qr_at_all()
    {
        var header = Header with { Trn = null };

        Assert.DoesNotContain(ReceiptRenderer.Layout(Sale(), header, PaperWidth.Mm80), l => l.Style == LineStyle.Qr || l.Text.Contains(SampleNote));
        Assert.DoesNotContain(Text(Sale(), header: header), l => l.Contains("[QR code]"));
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), header, PaperWidth.Mm80, true), QrCommand));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Escpos_prints_the_qr_once_centred_then_resets_before_feed_cut_and_drawer(bool sample)
    {
        var receipt = Sale();
        var header = sample ? Header with { Trn = null, SampleQr = true } : Header;
        var bytes = ReceiptRenderer.EscPosBytes(receipt, header, PaperWidth.Mm80, openDrawer: true);
        var payload = ReceiptRenderer.Layout(receipt, header, PaperWidth.Mm80).Single(l => l.Style == LineStyle.Qr).Text;

        Assert.Equal(1, Count(bytes, QrPrint));
        Assert.Equal(4 + 1, Count(bytes, QrCommand));                                           // model, size, ECC, store + print
        Assert.Equal(1, Count(bytes, System.Text.Encoding.ASCII.GetBytes(payload)));
        var qrStart = IndexOf(bytes, QrCommand);
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x01 }, bytes[(qrStart - 3)..qrStart]);          // centred
        var qrEnd = IndexOf(bytes, QrPrint) + QrPrint.Length;
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x00, 0x1B, 0x45, 0x00, 0x1D, 0x21, 0x00 }, bytes[qrEnd..(qrEnd + 9)]); // reset
        var note = IndexOf(bytes, System.Text.Encoding.ASCII.GetBytes(SampleNote));
        if (sample) Assert.True(note > qrEnd);
        else Assert.Equal(-1, note);
        var cut = LastIndexOf(bytes, [0x1D, 0x56, 0x42, 0x00]);
        Assert.True(cut > qrEnd && cut > note);
        Assert.Equal(new byte[] { 0x1B, 0x64, 0x03 }, bytes[(cut - 3)..cut]);
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, bytes[^5..]);
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Text_receipt_shows_a_centred_qr_placeholder(PaperWidth paper)
    {
        var lines = Text(Sale(), paper, Header with { Trn = null, SampleQr = true });

        var at = lines.FindIndex(l => l.Contains("[QR code]"));
        Assert.Equal(new string(' ', ((int)paper - "[QR code]".Length) / 2) + "[QR code]", lines[at]);
        Assert.Equal(new string(' ', ((int)paper - SampleNote.Length) / 2) + SampleNote, lines[at + 1]);
        Assert.Single(lines, l => l.Contains("[QR code]"));
        Assert.All(lines, l => Assert.True(l.Length <= (int)paper, $"[{l}]"));
    }

    private static int Count(byte[] haystack, byte[] needle)
    {
        var count = 0;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) count++;
        return count;
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Copy_mark_is_printed_only_on_a_copy_right_after_the_title_block(PaperWidth paper)
    {
        var width = (int)paper;
        Assert.DoesNotContain(ReceiptRenderer.Layout(Sale(), Header, paper), l => l.Text.Contains("COPY"));
        Assert.DoesNotContain(Text(Sale(), paper), l => l.Contains("COPY"));

        var layout = ReceiptRenderer.Layout(Sale(), Header, paper, copy: true).ToList();
        var mark = Assert.Single(layout, l => l.Text.Contains("*** COPY ***"));
        Assert.Equal(LineStyle.Bold, mark.Style);
        Assert.Equal(new string(' ', (width - "*** COPY ***".Length) / 2) + "*** COPY ***", mark.Text);
        var index = layout.IndexOf(mark);
        Assert.Equal(new string('=', width), layout[index - 1].Text);
        Assert.Equal(LineStyle.Title, layout[index - 2].Style);
        Assert.Contains(ReceiptRenderer.TextLines(Sale(), Header, paper, copy: true), l => l.Trim() == "*** COPY ***");
    }

    [Fact]
    public void Escpos_copy_carries_the_mark_and_no_drawer_when_not_asked()
    {
        var mark = System.Text.Encoding.ASCII.GetBytes("*** COPY ***");
        var copy = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: false, copy: true);

        Assert.True(EscPosTests.Contains(copy, mark));
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: false), mark));
        Assert.False(EscPosTests.Contains(copy, [0x1B, 0x70, 0x00, 0x19, 0xFA]));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void The_counter_is_printed_under_the_till()
    {
        var lines = Text(Sale() with { CounterName = "Counter 1" });
        var till = lines.IndexOf("Till      : Till 2");
        Assert.Equal("Counter   : Counter 1", lines[till + 1]);
    }

    [Fact]
    public void A_bill_from_before_counters_has_no_counter_line() =>
        Assert.DoesNotContain(Text(Sale()), l => l.StartsWith("Counter", StringComparison.Ordinal));
}
