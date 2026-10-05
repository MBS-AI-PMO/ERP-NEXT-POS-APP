using System.Text.Json;
using TillPOS.Core.Money;
using TillPOS.Sync.Verification;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync;

public class InvoiceReplayTests
{
    private const string InclusiveVat =
        """[{"idx":1,"charge_type":"On Net Total","account_head":"VAT 5% - S","description":"VAT 5%","rate":5,"included_in_print_rate":1}]""";

    private static JsonElement Invoice(string items, string taxes, string totals, string extra = "") =>
        JsonDocument.Parse($$"""{"name":"ACC-PSINV-0001","items":{{items}},"taxes":{{taxes}},{{totals}}{{extra}}}""").RootElement.Clone();

    private static ReplayResult Replay(JsonElement invoice) => InvoiceReplay.Replay(invoice, new MoneySettings());

    [Fact]
    public void Inclusive_vat_invoice_matches()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":105,"item_tax_rate":"{}"}]""", InclusiveVat,
            "\"net_total\":100,\"total_taxes_and_charges\":5,\"grand_total\":105,\"rounded_total\":105,\"disable_rounded_total\":0"));

        Assert.Equal(ReplayOutcome.Match, r.Outcome);
        Assert.Equal(4, r.Fields.Count);
    }

    [Fact]
    public void Different_erpnext_total_is_a_mismatch()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":105,"item_tax_rate":"{}"}]""", InclusiveVat,
            "\"net_total\":100,\"total_taxes_and_charges\":5,\"grand_total\":104.99,\"rounded_total\":105,\"disable_rounded_total\":0"));

        Assert.Equal(ReplayOutcome.Mismatch, r.Outcome);
        var grand = Assert.Single(r.Fields, f => !f.Ok);
        Assert.Equal("grand_total", grand.Field);
        Assert.Equal(M("105.00"), grand.Till);
        Assert.Equal(M("104.99"), grand.Erp);
    }

    [Fact]
    public void Line_item_tax_rate_overrides_the_invoice_tax_rate()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":105,"item_tax_rate":"{}"},{"qty":1,"rate":50,"item_tax_rate":"{\"VAT 5% - S\": 0}"}]""", InclusiveVat,
            "\"net_total\":150,\"total_taxes_and_charges\":5,\"grand_total\":155,\"rounded_total\":155,\"disable_rounded_total\":0"));

        Assert.Equal(ReplayOutcome.Match, r.Outcome);
    }

    [Fact]
    public void Bill_level_discount_is_skipped()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":105,"item_tax_rate":"{}"}]""", InclusiveVat,
            "\"net_total\":95,\"total_taxes_and_charges\":4.75,\"grand_total\":99.75,\"rounded_total\":100,\"discount_amount\":5"));

        Assert.Equal(ReplayOutcome.Skipped, r.Outcome);
        Assert.Contains("discount", r.Reason);
    }

    [Fact]
    public void Unsupported_tax_row_is_skipped()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":100,"item_tax_rate":"{}"}]""",
            """[{"idx":1,"charge_type":"Actual","account_head":"Delivery - S","rate":0,"tax_amount":10}]""",
            "\"net_total\":100,\"total_taxes_and_charges\":10,\"grand_total\":110,\"rounded_total\":110"));

        Assert.Equal(ReplayOutcome.Skipped, r.Outcome);
        Assert.Contains("Actual", r.Reason);
    }

    [Fact]
    public void Rounded_total_is_not_compared_when_rounding_is_disabled()
    {
        var r = Replay(Invoice(
            """[{"qty":1,"rate":99.40,"item_tax_rate":"{}"}]""",
            """[{"idx":1,"charge_type":"On Net Total","account_head":"VAT 5% - S","description":"VAT 5%","rate":5,"included_in_print_rate":0}]""",
            "\"net_total\":99.40,\"total_taxes_and_charges\":4.97,\"grand_total\":104.37,\"rounded_total\":0,\"disable_rounded_total\":1"));

        Assert.Equal(ReplayOutcome.Match, r.Outcome);
        Assert.DoesNotContain(r.Fields, f => f.Field == "rounded_total");
    }
}
