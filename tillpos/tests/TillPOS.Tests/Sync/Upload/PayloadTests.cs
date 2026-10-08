using System.Text.Json;
using System.Text.RegularExpressions;
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Sync.Upload;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>Golden tests: the exact ERPNext fields and values each payload carries for representative bills and shifts.</summary>
public partial class PayloadTests
{
    private static readonly TimeSpan Uae = TimeSpan.FromHours(4);
    private static readonly DateTimeOffset At = new(2026, 10, 6, 15, 30, 5, Uae);

    internal static readonly PosSettings Counter1 = new("Al Ain Counter 1", "Al Ain Market", "Al Ain Market LLC", null, null, "AED",
        "Stores - AAML", "Standard Selling", "Walk-in Customer", "UAE VAT 5% - AAML", null, false, M("0.25"), M("0.05"),
        [new PaymentMode("Cash Counter 1", true), new PaymentMode("Credit Card", false)]);

    private static ReceiptLine Line(int no, string code, string name, decimal qty, decimal priceListRate, decimal rate, decimal amount,
        string uom = "Nos", string? barcode = null, string? taxTemplate = null, bool scale = false, string? fallbackFrom = null) =>
        new(no, code, name, barcode, uom, 1m, qty, priceListRate, rate, amount, null, taxTemplate, scale, fallbackFrom);

    private static Receipt Sale(string id, IReadOnlyList<ReceiptLine> lines, decimal grand, bool rounded, decimal roundedTotal,
        IReadOnlyList<ReceiptPayment> payments, decimal change, DateTimeOffset? at = null) =>
        new(id, ReceiptKind.Sale, null, "TILL2-SHIFT-20261006080000", "cashier1", at ?? At, lines, grand, Math.Round(grand / 1.05m, 3),
            grand - Math.Round(grand / 1.05m, 3), grand, rounded, roundedTotal, rounded ? roundedTotal - grand : 0m, payments, change,
            rounded ? roundedTotal - grand : 0m, null);

    private static InvoicePayload Build(Receipt r, string? returnAgainst = null, string? cashierUser = "cashier1@shop.local") =>
        PosInvoicePayload.Build(r, "Al Ain Counter 1", Counter1, "POS-OPE-2026-00042", cashierUser, "TILL2", returnAgainst);

    private static List<Dictionary<string, object?>> Rows(Dictionary<string, object?> doc, string table) =>
        Assert.IsType<List<Dictionary<string, object?>>>(doc[table]);

    [Fact]
    public void Cash_rounded_sale_matches_the_golden_json()
    {
        var r = Sale("TILL2-20261006153005-000001",
            [Line(1, "000089", "CUCUMBER/KIYAR", M("0.740"), M("3.50"), M("3.50"), M("2.590"), "Kg", "2000089007400", scale: true)],
            M("2.590"), true, M("2.500"), [new ReceiptPayment("Cash Counter 1", 5m)], M("2.50"));

        var payload = Build(r);

        const string golden = """
            {"doctype":"POS Invoice","is_pos":1,"pos_profile":"Al Ain Counter 1","company":"Al Ain Market","customer":"Walk-in Customer",
            "set_posting_time":1,"posting_date":"2026-10-06","posting_time":"15:30:05","selling_price_list":"Standard Selling",
            "set_warehouse":"Stores - AAML","ignore_pricing_rule":1,"taxes_and_charges":"UAE VAT 5% - AAML","disable_rounded_total":0,
            "items":[{"item_code":"000089","item_name":"CUCUMBER/KIYAR","qty":0.740,"uom":"Kg","conversion_factor":1,"price_list_rate":3.50,
            "rate":3.50,"barcode":"2000089007400","warehouse":"Stores - AAML","posa_row_id":"1"}],
            "payments":[{"mode_of_payment":"Cash Counter 1","amount":5}],"change_amount":2.50,
            "posa_pos_opening_shift":"POS-OPE-2026-00042","posa_cashier":"cashier1@shop.local",
            "posa_client_request_id":"TILL2-20261006153005-000001","custom_till":"TILL2","docstatus":0}
            """;
        Assert.Equal(golden.ReplaceLineEndings("").Replace("\n", ""), ErpFormat.Json(payload.Doc));
        Assert.Equal(new ExpectedTotals(M("2.590"), M("2.500"), true), payload.Expected);
    }

    [Fact]
    public void Card_bill_is_exact_and_not_rounded()
    {
        var r = Sale("TILL2-1", [Line(1, "RICE5", "RICE 5KG", 1m, M("10.500"), M("10.500"), M("10.500"))], M("10.500"), false, 0m,
            [new ReceiptPayment("Credit Card", M("10.500"))], 0m);

        var payload = Build(r);

        Assert.Equal(1, payload.Doc["disable_rounded_total"]);
        var pay = Assert.Single(Rows(payload.Doc, "payments"));
        Assert.Equal("Credit Card", pay["mode_of_payment"]);
        Assert.Equal(M("10.500"), pay["amount"]);
        Assert.Equal(0m, payload.Doc["change_amount"]);
        Assert.Equal(new ExpectedTotals(M("10.500"), M("10.500"), false), payload.Expected);
        Assert.False(payload.Doc.ContainsKey("rounded_total"));
        Assert.False(payload.Doc.ContainsKey("grand_total"));
    }

    [Fact]
    public void Split_bill_sends_card_exact_and_cash_tendered_with_change()
    {
        var r = Sale("TILL2-2", [Line(1, "OIL", "OIL 1L", 2m, M("12.630"), M("12.630"), M("25.260"))], M("25.260"), false, 0m,
            [new ReceiptPayment("Credit Card", 20m), new ReceiptPayment("Cash Counter 1", 10m)], M("4.75"));

        var payload = Build(r);

        Assert.Equal(1, payload.Doc["disable_rounded_total"]);
        Assert.Equal(new[] { ("Credit Card", 20m), ("Cash Counter 1", 10m) },
            Rows(payload.Doc, "payments").Select(p => ((string)p["mode_of_payment"]!, (decimal)p["amount"]!)));
        Assert.Equal(M("4.75"), payload.Doc["change_amount"]);
    }

    [Fact]
    public void Weighed_scale_line_keeps_the_label_barcode_and_weight()
    {
        var r = Sale("TILL2-3", [Line(1, "000088", "TOMATO", M("0.305"), M("4.00"), M("4.00"), M("1.220"), "Kg", "2000088003050", scale: true)],
            M("1.220"), true, M("1.250"), [new ReceiptPayment("Cash Counter 1", M("1.25"))], 0m);

        var item = Assert.Single(Rows(Build(r).Doc, "items"));

        Assert.Equal("2000088003050", item["barcode"]);
        Assert.Equal(M("0.305"), item["qty"]);
        Assert.Equal("Kg", item["uom"]);
        Assert.Equal(1m, item["conversion_factor"]);
    }

    [Fact]
    public void Unit_fallback_line_is_sent_in_the_stock_unit()
    {
        var r = Sale("TILL2-4", [Line(1, "SOAP", "SOAP BAR", 1m, M("3.000"), M("3.000"), M("3.000"), "Nos", "6291000000017",
            fallbackFrom: "Box")], M("3.000"), true, M("3.000"), [new ReceiptPayment("Cash Counter 1", 3m)], 0m);

        var item = Assert.Single(Rows(Build(r).Doc, "items"));

        Assert.Equal("Nos", item["uom"]);
        Assert.Equal(1m, item["conversion_factor"]);
        Assert.False(item.ContainsKey("uom_fallback_from"));
    }

    [Fact]
    public void Offer_line_sends_the_list_price_and_the_charged_rate_with_pricing_rules_ignored()
    {
        var r = Sale("TILL2-5", [Line(1, "JUICE", "JUICE 1L", 3m, M("6.000"), M("5.000"), M("15.000"), taxTemplate: "UAE VAT 5% - AAML")],
            M("15.000"), true, M("15.000"), [new ReceiptPayment("Cash Counter 1", 20m)], 5m);

        var payload = Build(r);
        var item = Assert.Single(Rows(payload.Doc, "items"));

        Assert.Equal(1, payload.Doc["ignore_pricing_rule"]);
        Assert.Equal(M("6.000"), item["price_list_rate"]);
        Assert.Equal(M("5.000"), item["rate"]);
        Assert.Equal("UAE VAT 5% - AAML", item["item_tax_template"]);
    }

    [Fact]
    public void Lines_without_a_tax_template_or_barcode_leave_those_fields_out()
    {
        var r = Sale("TILL2-6", [Line(1, "A", "A", 1m, 1m, 1m, 1m), Line(2, "B", "B", 1m, 1m, 1m, 1m)], 2m, true, 2m,
            [new ReceiptPayment("Cash Counter 1", 2m)], 0m);

        var items = Rows(Build(r, cashierUser: null).Doc, "items");

        Assert.All(items, i => Assert.False(i.ContainsKey("item_tax_template") || i.ContainsKey("barcode")));
        Assert.Equal(new[] { "1", "2" }, items.Select(i => (string)i["posa_row_id"]!));
        Assert.False(Build(r, cashierUser: null).Doc.ContainsKey("posa_cashier"));
    }

    private static Receipt Return(string? returnAgainst) =>
        new("TILL2-20261006170000-000009", ReceiptKind.Return, returnAgainst, "TILL2-SHIFT-20261006080000", "cashier1",
            new DateTimeOffset(2026, 10, 6, 17, 0, 0, Uae),
            [Line(1, "RICE5", "RICE 5KG", -1m, M("10.500"), M("10.500"), M("-10.500"))], M("-10.500"), M("-10.000"), M("-0.500"),
            M("-10.500"), true, M("-10.500"), 0m, [new ReceiptPayment("Cash Counter 1", M("-10.500"))], 0m, 0m, "sup1");

    [Fact]
    public void Return_with_receipt_has_negative_lines_and_return_against()
    {
        var payload = Build(Return("TILL2-20261006153005-000001"), returnAgainst: "ACC-PSINV-2026-01234");

        Assert.Equal(1, payload.Doc["is_return"]);
        Assert.Equal("ACC-PSINV-2026-01234", payload.Doc["return_against"]);
        Assert.Equal(-1m, Assert.Single(Rows(payload.Doc, "items"))["qty"]);
        Assert.Equal(M("-10.500"), Assert.Single(Rows(payload.Doc, "payments"))["amount"]);
        Assert.Equal(new ExpectedTotals(M("-10.500"), M("-10.500"), true), payload.Expected);
        Assert.Equal(0, payload.Doc["docstatus"]);
    }

    [Fact]
    public void A_bill_with_its_own_customer_sends_it_and_others_send_the_profiles()
    {
        var ret = Return("TILL2-20261006153005-000001") with { Customer = "Ahmed Trading" };

        Assert.Equal("Ahmed Trading", Build(ret, returnAgainst: "ACC-PSINV-2026-01234").Doc["customer"]);
        Assert.Equal("Walk-in Customer", Build(ret with { Customer = null }, returnAgainst: "ACC-PSINV-2026-01234").Doc["customer"]);

        var opening = new ShiftOpening("TILL2-SHIFT-20261006080000", "cashier1", "Al Ain Counter 1", At.AddHours(-7),
            [new ReceiptPayment("Cash Counter 1", 200m)]);
        var closing = new ShiftClosing(opening.ClientId, At.AddHours(1), [new ShiftModeSummary("Cash Counter 1", 200m, 200m, 200m, 0m)],
            1, 1, 0m, 0m, 0m);
        var doc = ClosingShiftPayload.Build(opening, closing, "POS-OPE-1", [("ACC-1", ret)], "Al Ain Counter 1", "Al Ain Market",
            "till2@shop.local", "Walk-in Customer");
        Assert.Equal("Ahmed Trading", Assert.Single(Rows(doc, "pos_transactions"))["customer"]);
    }

    [Fact]
    public void Return_lines_carry_the_original_invoice_rows()
    {
        var payload = PosInvoicePayload.Build(Return("TILL2-20261006153005-000001"), "Al Ain Counter 1", Counter1, "POS-OPE-2026-00042",
            "cashier1@shop.local", "TILL2", "ACC-PSINV-2026-01234", new Dictionary<int, string> { [1] = "a1b2c3d4e5" });

        Assert.Equal("a1b2c3d4e5", Assert.Single(Rows(payload.Doc, "items"))["pos_invoice_item"]);
        Assert.Contains("\"posa_row_id\":\"1\",\"pos_invoice_item\":\"a1b2c3d4e5\"", ErpFormat.Json(payload.Doc));
        Assert.False(Assert.Single(Rows(Build(Return(null)).Doc, "items")).ContainsKey("pos_invoice_item"));
    }

    [Fact]
    public void A_return_sends_what_it_pays_back_and_no_change()
    {
        var json = ErpFormat.Json(Build(Return("TILL2-20261006153005-000001"), returnAgainst: "ACC-PSINV-2026-01234").Doc);

        Assert.Contains("\"payments\":[{\"mode_of_payment\":\"Cash Counter 1\",\"amount\":-10.500}],\"paid_amount\":-10.500,\"base_paid_amount\":-10.500,\"change_amount\":0,", json);
    }

    [Fact]
    public void A_sale_leaves_paid_amount_to_erpnext()
    {
        var r = Sale("TILL2-9", [Line(1, "A", "A", 1m, 1m, 1m, 1m)], 1m, true, 1m, [new ReceiptPayment("Cash Counter 1", 5m)], 4m);
        var doc = Build(r).Doc;
        Assert.False(doc.ContainsKey("paid_amount") || doc.ContainsKey("base_paid_amount"));
        Assert.Equal(4m, doc["change_amount"]);
    }

    [Fact]
    public void Return_without_receipt_has_no_return_against()
    {
        var payload = Build(Return(null), returnAgainst: null);

        Assert.Equal(1, payload.Doc["is_return"]);
        Assert.False(payload.Doc.ContainsKey("return_against"));
    }

    [Fact]
    public void A_sale_is_never_marked_as_a_return()
    {
        var r = Sale("TILL2-7", [Line(1, "A", "A", 1m, 1m, 1m, 1m)], 1m, true, 1m, [new ReceiptPayment("Cash Counter 1", 1m)], 0m);
        var doc = Build(r, returnAgainst: "IGNORED").Doc;
        Assert.False(doc.ContainsKey("is_return") || doc.ContainsKey("return_against"));
    }

    private static readonly ShiftOpening Opening = new("TILL2-SHIFT-20261006080000", "cashier1", "Al Ain Counter 1",
        new DateTimeOffset(2026, 10, 6, 8, 0, 0, Uae), [new ReceiptPayment("Cash Counter 1", 200m)]);

    [Fact]
    public void Opening_shift_matches_the_golden_json()
    {
        var doc = OpeningShiftPayload.Build(Opening, "Al Ain Counter 1", "Al Ain Market", "till2@shop.local");

        const string golden = """
            {"doctype":"POS Opening Shift","period_start_date":"2026-10-06 08:00:00","posting_date":"2026-10-06","company":"Al Ain Market",
            "pos_profile":"Al Ain Counter 1","user":"till2@shop.local","balance_details":[{"mode_of_payment":"Cash Counter 1","amount":200}],
            "custom_offline_id":"TILL2-SHIFT-20261006080000","docstatus":0}
            """;
        Assert.Equal(golden.ReplaceLineEndings("").Replace("\n", ""), ErpFormat.Json(doc));
    }

    [Fact]
    public void Closing_shift_carries_every_invoice_and_the_blind_count_reconciliation()
    {
        var cash = Sale("TILL2-20261006153005-000001",
            [Line(1, "000089", "CUCUMBER/KIYAR", M("0.740"), M("3.50"), M("3.50"), M("2.590"), "Kg", "2000089007400", scale: true)],
            M("2.590"), true, M("2.500"), [new ReceiptPayment("Cash Counter 1", 5m)], M("2.50"));
        var card = Sale("TILL2-20261006160000-000002", [Line(1, "RICE5", "RICE 5KG", 2m, M("10.500"), M("10.500"), M("21.000"))],
            M("21.000"), false, 0m, [new ReceiptPayment("Credit Card", M("21.000"))], 0m, new DateTimeOffset(2026, 10, 6, 16, 0, 0, Uae));
        var refund = Return(cash.ClientId) with
        {
            Lines = [Line(1, "000089", "CUCUMBER/KIYAR", M("-0.740"), M("3.50"), M("3.50"), M("-2.590"), "Kg", "2000089007400", scale: true)],
            GrandTotal = M("-2.590"), NetTotal = M("-2.467"), TotalTaxes = M("-0.123"), RoundedTotal = M("-2.500"),
            Payments = [new ReceiptPayment("Cash Counter 1", M("-2.500"))],
        };
        var receipts = new[] { cash, card, refund };
        var closedAt = new DateTimeOffset(2026, 10, 6, 22, 15, 0, Uae);
        var counted = new Dictionary<string, decimal> { ["Cash Counter 1"] = M("200.25"), ["Credit Card"] = M("21.000") };
        var closing = ShiftCalculator.Close(Opening, receipts, counted, new TenderModes("Cash Counter 1", "Credit Card"), closedAt,
            new MoneySettings(3, RoundingMethod.Bankers, M("0.25")));

        var doc = ClosingShiftPayload.Build(Opening, closing, "POS-OPE-2026-00042",
            [("ACC-PSINV-1", cash), ("ACC-PSINV-2", card), ("ACC-PSINV-3", refund)], "Al Ain Counter 1", "Al Ain Market",
            "till2@shop.local", "Walk-in Customer");

        Assert.Equal("POS Closing Shift", doc["doctype"]);
        Assert.Equal("POS-OPE-2026-00042", doc["pos_opening_shift"]);
        Assert.Equal("2026-10-06 08:00:00", doc["period_start_date"]);
        Assert.Equal("2026-10-06 22:15:00", doc["period_end_date"]);
        Assert.Equal("2026-10-06", doc["posting_date"]);
        Assert.Equal("Al Ain Market", doc["company"]);
        Assert.Equal("Al Ain Counter 1", doc["pos_profile"]);
        Assert.Equal("till2@shop.local", doc["user"]);
        Assert.Equal(M("21.000"), doc["grand_total"]);            // 2.590 + 21.000 − 2.590
        Assert.Equal(closing.NetTotal, doc["net_total"]);
        Assert.Equal(M("2.000"), doc["total_quantity"]);          // 0.740 + 2 − 0.740
        Assert.Equal("TILL2-SHIFT-20261006080000", doc["custom_offline_id"]);
        Assert.Equal(0, doc["docstatus"]);

        var tx = Rows(doc, "pos_transactions");
        Assert.Equal(new[] { "ACC-PSINV-1", "ACC-PSINV-2", "ACC-PSINV-3" }, tx.Select(t => (string)t["pos_invoice"]!));
        Assert.All(tx, t => Assert.Equal("Walk-in Customer", t["customer"]));
        Assert.Equal(new[] { M("2.590"), M("21.000"), M("-2.590") }, tx.Select(t => (decimal)t["grand_total"]!));
        Assert.Equal("2026-10-06", tx[0]["posting_date"]);

        // Cash: 200 opening + 5 tendered − 2.50 change − 2.50 refund = 200.000 expected; 200.25 counted → +0.250.
        var rec = Rows(doc, "payment_reconciliation");
        Assert.Equal(new[] { "Cash Counter 1", "Credit Card" }, rec.Select(r => (string)r["mode_of_payment"]!));
        Assert.Equal(200m, rec[0]["opening_amount"]);
        Assert.Equal(M("200.000"), rec[0]["expected_amount"]);
        Assert.Equal(M("200.250"), rec[0]["closing_amount"]);
        Assert.Equal(M("0.250"), rec[0]["difference"]);
        Assert.Equal(0m, rec[1]["opening_amount"]);
        Assert.Equal(M("21.000"), rec[1]["expected_amount"]);
        Assert.Equal(0m, rec[1]["difference"]);
        for (var i = 0; i < rec.Count; i++)
        {
            Assert.Equal(closing.Modes[i].Expected, rec[i]["expected_amount"]);
            Assert.Equal(closing.Modes[i].Counted, rec[i]["closing_amount"]);
            Assert.Equal(closing.Modes[i].Difference, rec[i]["difference"]);
        }
    }

    [Fact]
    public void Closing_shift_matches_the_golden_json_with_taxes_and_no_pos_payments()
    {
        var cash = Sale("TILL2-1", [Line(1, "A", "A", 1m, M("2.590"), M("2.590"), M("2.590"))], M("2.590"), true, M("2.500"),
            [new ReceiptPayment("Cash Counter 1", 5m)], M("2.50"));
        var split = Sale("TILL2-2", [Line(1, "B", "B", 2m, M("10.500"), M("10.500"), M("21.000"))], M("21.000"), false, 0m,
            [new ReceiptPayment("Credit Card", 20m), new ReceiptPayment("Cash Counter 1", M("1.00"))], 0m, At.AddMinutes(10));
        var closing = new ShiftClosing(Opening.ClientId, new DateTimeOffset(2026, 10, 6, 22, 0, 0, Uae),
            [new ShiftModeSummary("Cash Counter 1", 200m, M("203.500"), M("203.500"), 0m), new ShiftModeSummary("Credit Card", 0m, 20m, 20m, 0m)],
            2, 0, M("23.590"), M("22.467"), M("1.123"));
        var vat = new SalesTaxTemplate("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);

        var doc = ClosingShiftPayload.Build(Opening, closing, "POS-OPE-1", [("ACC-1", cash), ("ACC-2", split)], "Al Ain Counter 1",
            "Al Ain Market", "till2@shop.local", "Walk-in Customer", vat);

        const string golden = """
            {"doctype":"POS Closing Shift","pos_opening_shift":"POS-OPE-1","period_start_date":"2026-10-06 08:00:00",
            "period_end_date":"2026-10-06 22:00:00","posting_date":"2026-10-06","company":"Al Ain Market","pos_profile":"Al Ain Counter 1",
            "user":"till2@shop.local","grand_total":23.590,"net_total":22.467,"total_quantity":3,
            "pos_transactions":[{"pos_invoice":"ACC-1","posting_date":"2026-10-06","customer":"Walk-in Customer","grand_total":2.590},
            {"pos_invoice":"ACC-2","posting_date":"2026-10-06","customer":"Walk-in Customer","grand_total":21.000}],
            "pos_payments":[],
            "taxes":[{"account_head":"VAT 5% - AAML","rate":5,"amount":1.123}],
            "payment_reconciliation":[{"mode_of_payment":"Cash Counter 1","opening_amount":200,"expected_amount":203.500,"closing_amount":203.500,"difference":0},
            {"mode_of_payment":"Credit Card","opening_amount":0,"expected_amount":20,"closing_amount":20,"difference":0}],
            "custom_offline_id":"TILL2-SHIFT-20261006080000","docstatus":0}
            """;
        Assert.Equal(golden.ReplaceLineEndings("").Replace("\n", ""), ErpFormat.Json(doc));
    }

    [Fact]
    public void Closing_taxes_share_the_vat_over_several_template_rows_by_rate()
    {
        var r = Sale("TILL2-1", [Line(1, "A", "A", 1m, 10m, 10m, 10m)], 10m, true, 10m, [new ReceiptPayment("Cash Counter 1", 10m)], 0m);
        var closing = ShiftCalculator.Close(Opening, [r], new Dictionary<string, decimal>(), new TenderModes("Cash Counter 1", "Credit Card"), At,
            new MoneySettings(3));
        var template = new SalesTaxTemplate("Two", [new TaxRow(2, "Tax B", "B", 1m, true), new TaxRow(1, "Tax A", "A", 2m, true)], null);

        var taxes = Rows(ClosingShiftPayload.Build(Opening, closing, "O", [("N", r)], "P", "C", "u", "W", template), "taxes");

        Assert.Equal(["Tax A", "Tax B"], taxes.Select(t => (string)t["account_head"]!));
        Assert.Equal(r.TotalTaxes, taxes.Sum(t => (decimal)t["amount"]!));
        Assert.Equal(Math.Round(r.TotalTaxes * 2 / 3, 3), taxes[0]["amount"]);
        Assert.Empty(Rows(ClosingShiftPayload.Build(Opening, closing, "O", [("N", r)], "P", "C", "u", "W"), "taxes"));
    }

    [Fact]
    public void Approval_matches_the_tillpos_approval_fields()
    {
        var record = new ApprovalRecord("a1b2", ApprovalAction.ReturnWithoutReceipt, "cashier1", "sup1", "TILL2-SHIFT-20261006080000",
            "TILL2-20261006170000-000009", null, M("10.500"), "Damaged", new DateTimeOffset(2026, 10, 6, 17, 0, 3, Uae));

        var doc = ApprovalPayload.Build(record, "TILL2", "POS-OPE-2026-00042", "ACC-PSINV-2026-01300");

        const string golden = """
            {"doctype":"TillPOS Approval","action":"ReturnWithoutReceipt","cashier":"cashier1","supervisor":"sup1",
            "shift":"POS-OPE-2026-00042","invoice":"ACC-PSINV-2026-01300","item_code":null,"amount":10.500,"reason":"Damaged",
            "at":"2026-10-06 17:00:03","till":"TILL2","custom_offline_id":"a1b2"}
            """;
        Assert.Equal(golden.ReplaceLineEndings("").Replace("\n", ""), ErpFormat.Json(doc));
    }

    [Fact]
    public void Approval_without_a_shift_or_cashier_sends_nulls()
    {
        var record = new ApprovalRecord("x", ApprovalAction.SettingsChange, "", "sup1", "", null, null, 0m, "Change till settings", At);
        var doc = ApprovalPayload.Build(record, "TILL2", null, null);
        Assert.Null(doc["cashier"]);
        Assert.Null(doc["shift"]);
        Assert.Null(doc["invoice"]);
    }

    [Fact]
    public void Every_payload_uses_snake_case_keys_only()
    {
        var r = Sale("TILL2-8", [Line(1, "A", "A", 1m, 1m, 1m, 1m, barcode: "1", taxTemplate: "T")], 1m, true, 1m,
            [new ReceiptPayment("Cash Counter 1", 1m)], 0m);
        var closing = ShiftCalculator.Close(Opening, [r], new Dictionary<string, decimal>(), new TenderModes("Cash Counter 1", "Credit Card"),
            At, new MoneySettings(3));
        var docs = new object[]
        {
            Build(Return("TILL2-8"), "ACC-1").Doc,
            Build(r).Doc,
            OpeningShiftPayload.Build(Opening, "P", "C", "u"),
            ClosingShiftPayload.Build(Opening, closing, "O", [("N", r)], "P", "C", "u", "Walk-in Customer"),
            ApprovalPayload.Build(new ApprovalRecord("i", ApprovalAction.LineVoid, "c", "s", "x", null, "A", 1m, "r", At), "TILL2", "O", null),
        };

        foreach (var doc in docs)
        {
            using var json = JsonDocument.Parse(ErpFormat.Json(doc));
            Assert.All(Keys(json.RootElement), k => Assert.Matches(SnakeCase(), k));
        }
    }

    private static IEnumerable<string> Keys(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Keys(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Keys),
        _ => [],
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCase();
}
