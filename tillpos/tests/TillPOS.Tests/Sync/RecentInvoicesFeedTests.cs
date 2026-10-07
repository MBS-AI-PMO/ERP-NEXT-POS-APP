using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync;

public sealed class RecentInvoicesFeedTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly SyncContext ctx;
    private readonly RemoteReceiptStore remote;
    private readonly Dictionary<string, List<Dictionary<string, object?>>> items = [];
    private readonly Dictionary<string, List<Dictionary<string, object?>>> payments = [];

    public RecentInvoicesFeedTests()
    {
        var store = new CatalogStore(temp.Db);
        store.SavePosSettings(new PosSettings("Al Ain Counter 2", "Al Ain Marketing LLC", "Al Ain Marketing LLC", null, null, "AED",
            "Stores - AAML", "Standard Selling", "Walk-in Customer", "UAE VAT 5% - AAML", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));
        ctx = new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Al Ain Counter 2");
        remote = new RemoteReceiptStore(temp.Db);
        // The child-table joins answer one row per item / payment of the invoices asked for.
        erp.Override = q =>
        {
            var table = q.Fields.Any(f => f.Contains("tabPOS Invoice Item", StringComparison.Ordinal)) ? items
                : q.Fields.Any(f => f.Contains("tabSales Invoice Payment", StringComparison.Ordinal)) ? payments
                : null;
            if (table is null) return null;
            var names = (IEnumerable<string>)q.Filters.Single(f => (string)f[0] == "name")[2];
            return names.SelectMany(n => table.GetValueOrDefault(n) ?? []).Cast<object>().ToList();
        };
    }

    public void Dispose() => temp.Dispose();

    private RecentInvoicesFeed Feed() => new(ctx, remote, 2, () => Now);

    private Dictionary<string, object?> Invoice(string name, string? clientId, string postingDate, string modified, int docstatus = 1,
        string company = "Al Ain Marketing LLC", string? returnAgainst = null, params (string Item, decimal Qty, string? RowId)[] lines)
    {
        var row = new Dictionary<string, object?>
        {
            ["name"] = name, ["modified"] = modified, ["docstatus"] = docstatus, ["company"] = company, ["posa_client_request_id"] = clientId,
            ["pos_profile"] = "Al Ain Counter 1", ["posting_date"] = postingDate, ["posting_time"] = "9:05:03.123456",
            ["customer"] = "Walk-in Customer", ["grand_total"] = 7.13m, ["rounded_total"] = 7.25m, ["net_total"] = 6.79m,
            ["discount_amount"] = name == "ACC-PSINV-2026-00042" ? "0.5" : 0, ["additional_discount_percentage"] = name == "ACC-PSINV-2026-00042" ? 2.5m : 0,
                        ["total_taxes_and_charges"] = 0.34m, ["is_return"] = returnAgainst is null ? 0 : 1, ["return_against"] = returnAgainst,
        };
        erp.AddRow("POS Invoice", row);
        items[name] = lines.Select((l, i) => new Dictionary<string, object?>
        {
            ["name"] = name, ["item_code"] = l.Item, ["item_name"] = l.Item + " name", ["qty"] = l.Qty, ["uom"] = "PCS",
            ["conversion_factor"] = 1, ["rate"] = "6.79", ["price_list_rate"] = "6.79", ["amount"] = l.Qty * 6.79m, ["barcode"] = "111",
            ["item_tax_template"] = "VAT 5% - AAML", ["posa_row_id"] = l.RowId, ["idx"] = i + 1,
        }).Reverse().ToList();                                                                  // answered out of order
        payments[name] =
        [
            new() { ["name"] = name, ["mode_of_payment"] = "Credit Card", ["amount"] = 0, ["idx"] = 2 },
            new() { ["name"] = name, ["mode_of_payment"] = "Cash Counter 1", ["amount"] = 7.25m, ["idx"] = 1 },
        ];
        return row;
    }

    [Fact]
    public async Task Stores_the_other_tills_invoices_with_their_lines_and_payments_and_skips_this_tills()
    {
        Invoice("ACC-PSINV-2026-00042", "TILL3-20261006090503-000007", "2026-10-06", "2026-10-06 09:05:04.000000",
            lines: [("MILK", 2m, "1"), ("RICE5", 1m, "2")]);
        Invoice("ACC-PSINV-2026-00043", null, "2026-10-06", "2026-10-06 09:06:00.000000", lines: [("MILK", 1m, "x9a")]);
        Invoice("ACC-PSINV-2026-00044", "TILL2-20261006091000-000003", "2026-10-06", "2026-10-06 09:10:00.000000", lines: [("MILK", 1m, "1")]);
        Invoice("ACC-PSINV-2026-00045", "TILL4-20261006100000-000001", "2026-10-06", "2026-10-06 10:00:00.000000",
            returnAgainst: "ACC-PSINV-2026-00042", lines: [("MILK", -1m, "1")]);

        Assert.Equal(4, await Feed().RunAsync(default));

        var sale = remote.FindByClientId("TILL3-20261006090503-000007")!;
        Assert.Equal(("ACC-PSINV-2026-00042", "TILL3", "Al Ain Counter 1", new DateTime(2026, 10, 6, 9, 5, 3)),
            (sale.ErpName, sale.Till, sale.PosProfile, sale.Posting));
        Assert.Equal((M("7.13"), M("7.25"), false), (sale.GrandTotal, sale.RoundedTotal, sale.IsReturn));
        Assert.Equal(new[] { ("1", "MILK", M("2")), ("2", "RICE5", M("1")) }, sale.Lines.Select(l => (l.RowId ?? "", l.ItemCode, l.Qty)));
        Assert.Equal(("MILK name", "PCS", 1m, M("6.79"), "111", "VAT 5% - AAML"),
            (sale.Lines[0].ItemName, sale.Lines[0].Uom, sale.Lines[0].ConversionFactor, sale.Lines[0].Rate, sale.Lines[0].Barcode, sale.Lines[0].ItemTaxTemplate));
        Assert.Equal(new[] { new RemotePayment("Cash Counter 1", M("7.25")) }, sale.Payments);   // unused modes left out

        Assert.Equal((M("0.50"), M("2.5")), (sale.DiscountAmount, sale.AdditionalDiscountPercentage));
        Assert.Null(remote.FindByErpName("ACC-PSINV-2026-00043")!.Till);                      // a POS Awesome counter's bill
        Assert.Null(remote.FindByErpName("ACC-PSINV-2026-00044"));                            // this till's own bill
        var ret = remote.FindByErpName("ACC-PSINV-2026-00045")!;
        Assert.Equal((true, "ACC-PSINV-2026-00042"), (ret.IsReturn, ret.ReturnAgainst));
        Assert.Equal(3, remote.Count());
        Assert.Equal(1m, remote.ReturnedQtyByLine("ACC-PSINV-2026-00042")[1]);
    }

    [Fact]
    public async Task Asks_only_for_the_companys_submitted_or_cancelled_invoices_of_the_last_30_days_and_only_reads()
    {
        Invoice("OTHER-CO", "TILL3-1", "2026-10-06", "2026-10-06 09:00:00.000000", company: "Another LLC", lines: [("MILK", 1m, "1")]);
        Invoice("DRAFT", "TILL3-2", "2026-10-06", "2026-10-06 09:00:00.000000", docstatus: 0, lines: [("MILK", 1m, "1")]);
        Invoice("TOO-OLD", "TILL3-3", "2026-09-06", "2026-10-06 09:00:00.000000", lines: [("MILK", 1m, "1")]);
        Invoice("FIRST-DAY", "TILL3-4", "2026-09-07", "2026-10-06 09:00:00.000000", lines: [("MILK", 1m, "1")]);

        await Feed().RunAsync(default);

        Assert.Equal(new[] { "FIRST-DAY" }, new[] { "OTHER-CO", "DRAFT", "TOO-OLD", "FIRST-DAY" }.Where(n => remote.FindByErpName(n) is not null));
        var query = erp.ListCalls.First(q => q.Doctype == "POS Invoice");
        Assert.Contains(query.Filters, f => (string)f[0] == "company" && (string)f[1] == "=" && (string)f[2]! == "Al Ain Marketing LLC");
        Assert.Contains(query.Filters, f => (string)f[0] == "posting_date" && (string)f[1] == ">=" && (string)f[2]! == "2026-09-07");
        Assert.Empty(erp.Inserted);
        Assert.Empty(erp.Submitted);
        Assert.Empty(erp.Calls);
    }

    [Fact]
    public async Task A_cancelled_invoice_is_removed_and_old_ones_are_pruned()
    {
        var row = Invoice("ACC-PSINV-2026-00050", "TILL3-1", "2026-10-06", "2026-10-06 09:00:00.000000", lines: [("MILK", 1m, "1")]);
        remote.Upsert(RemoteReceiptStoreTests.Sale("ANCIENT", "TILL1-1", new DateTime(2026, 9, 1, 12, 0, 0)), Now);
        await Feed().RunAsync(default);
        Assert.NotNull(remote.FindByErpName("ACC-PSINV-2026-00050"));
        Assert.Null(remote.FindByErpName("ANCIENT"));

        row["docstatus"] = 2;
        row["modified"] = "2026-10-07 09:30:00.000000";
        await Feed().RunAsync(default);

        Assert.Null(remote.FindByErpName("ACC-PSINV-2026-00050"));
        Assert.Equal(0, remote.Count());
    }

    [Fact]
    public async Task Without_the_POS_settings_the_feed_fails_without_asking_ERPNext()
    {
        using var other = new TempDb();
        var empty = new SyncContext(erp, new CatalogStore(other.Db), new KeysetPager(erp, new InMemorySyncState()), "Al Ain Counter 2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecentInvoicesFeed(empty, remote, 2, () => Now).RunAsync(default));
        Assert.Empty(erp.ListCalls);
    }
}
