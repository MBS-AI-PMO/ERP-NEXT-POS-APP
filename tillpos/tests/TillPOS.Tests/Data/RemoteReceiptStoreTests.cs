using TillPOS.Core.Sales;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class RemoteReceiptStoreTests : IDisposable
{
    private static readonly DateTimeOffset Fetched = new(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly RemoteReceiptStore store;
    private readonly ReceiptStore receipts;

    public RemoteReceiptStoreTests()
    {
        store = new RemoteReceiptStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
    }

    public void Dispose() => temp.Dispose();

    private static RemoteLine Milk(string? rowId, decimal qty) =>
        new(rowId, "MILK", "Full Cream Milk 1L", qty, "PCS", 1m, M("6.79"), M("6.79"), qty * M("6.79"), "111", "VAT 5% - T");

    private static RemoteLine Rice(string? rowId, decimal qty) =>
        new(rowId, "RICE5", "Rice 5kg", qty, "PCS", 1m, M("21.50"), M("21.50"), qty * M("21.50"), null, null);

    internal static RemoteReceipt Sale(string name, string? clientId, DateTime posting, params RemoteLine[] lines) => new(
        name, clientId, clientId?.Split('-')[0], "Al Ain Counter 1", posting, "Walk-in Customer", lines.Sum(l => l.Amount),
        Math.Round(lines.Sum(l => l.Amount) * 4m, MidpointRounding.AwayFromZero) / 4m, lines.Sum(l => l.Amount) / 1.05m, 0m, false, null,
        lines, [new RemotePayment("Cash Counter 1", lines.Sum(l => l.Amount))]);

    internal static RemoteReceipt Return(string name, string against, DateTime posting, params RemoteLine[] lines) =>
        Sale(name, null, posting, lines) with { IsReturn = true, ReturnAgainst = against };

    [Fact]
    public void An_invoice_round_trips_and_is_found_by_client_id_or_name_ignoring_case()
    {
        var sale = Sale("ACC-PSINV-2026-00042", "TILL3-20261006120000-000007", new DateTime(2026, 10, 6, 12, 0, 5), Milk("1", 2m), Rice("2", 1m));
        store.Upsert(sale, Fetched);

        var byName = store.FindByErpName("acc-psinv-2026-00042")!;
        Assert.Equal(sale with { Lines = byName.Lines, Payments = byName.Payments }, byName);
        Assert.Equal(sale.Lines, byName.Lines);
        Assert.Equal(sale.Payments, byName.Payments);
        Assert.Equal("ACC-PSINV-2026-00042", store.FindByClientId("till3-20261006120000-000007")!.ErpName);
        Assert.Null(store.FindByClientId("TILL3-20261006120000-000008"));
        Assert.Null(store.FindByErpName("ACC-PSINV-2026-00043"));

        store.Upsert(sale with { Customer = "Changed" }, Fetched);
        Assert.Equal(1, store.Count());
        Assert.Equal("Changed", store.FindByErpName(sale.ErpName)!.Customer);

        store.Delete(sale.ErpName);
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void As_a_receipt_it_keeps_its_name_posting_time_and_tills_line_numbers()
    {
        var posting = new DateTime(2026, 10, 6, 12, 0, 5);
        var sale = Sale("ACC-PSINV-2026-00042", "TILL3-1", posting, Milk("3", 2m), Rice("5", 1m));

        var receipt = sale.ToReceipt();

        Assert.Equal(("ACC-PSINV-2026-00042", ReceiptKind.Sale, (string?)null), (receipt.ClientId, receipt.Kind, receipt.ReturnAgainst));
        Assert.Equal(posting, receipt.CreatedAt.LocalDateTime);
        Assert.Equal(new[] { 3, 5 }, receipt.Lines.Select(l => l.LineNo));
        Assert.Equal(("MILK", "111", M("2"), M("6.79"), "VAT 5% - T"),
            (receipt.Lines[0].ItemCode, receipt.Lines[0].Barcode, receipt.Lines[0].Qty, receipt.Lines[0].Rate, receipt.Lines[0].ItemTaxTemplate));
        Assert.Equal(sale.GrandTotal, receipt.GrandTotal);
        Assert.Equal("Al Ain Counter 1", receipt.PosProfile);

        // POS Awesome's random row ids (or repeated numbers) are numbered by position.
        Assert.Equal(new[] { 1, 2 }, (sale with { Lines = [Milk("a7x", 1m), Rice("9", 1m)] }).ToReceipt().Lines.Select(l => l.LineNo));
        Assert.Equal(new[] { 1, 2 }, (sale with { Lines = [Milk("4", 1m), Rice("4", 1m)] }).ToReceipt().Lines.Select(l => l.LineNo));
    }

    [Fact]
    public void Returns_of_other_tills_are_matched_to_the_sale_lines_by_row_id_then_by_item()
    {
        var sale = Sale("SALE-1", "TILL3-1", new DateTime(2026, 10, 6, 12, 0, 0), Milk("k1", 2m), Rice("k2", 1m), Milk("k3", 3m));
        store.Upsert(sale, Fetched);
        store.Upsert(Return("RET-1", "SALE-1", new DateTime(2026, 10, 6, 13, 0, 0), Milk("k3", -1m)), Fetched);         // row id: line 3
        store.Upsert(Return("RET-2", "sale-1", new DateTime(2026, 10, 6, 14, 0, 0), Milk("zz", -2m), Rice(null, -1m)), Fetched); // by item
        store.Upsert(Return("RET-3", "SALE-9", new DateTime(2026, 10, 6, 14, 0, 0), Milk("k1", -1m)), Fetched);         // another sale

        var original = sale.ToReceipt();
        var returns = store.ReturnsAgainst(original);

        Assert.Equal(new[] { "RET-1", "RET-2" }, returns.Select(r => r.ClientId));
        Assert.All(returns, r => Assert.Equal(("SALE-1", ReceiptKind.Return), (r.ReturnAgainst, r.Kind)));
        Assert.Equal(new[] { (3, M("-1")) }, returns[0].Lines.Select(l => (l.LineNo, l.Qty)));
        Assert.Equal(new[] { (1, M("-2")), (2, M("-1")) }, returns[1].Lines.Select(l => (l.LineNo, l.Qty)));

        // Plus this till's own returns against the sale (by its ERPNext name).
        receipts.Save(LocalReturn("TILL2-20261007090000-000001", "SALE-1", lineNo: 3, qty: 1m));
        var returned = store.ReturnedQtyByLine("SALE-1");
        Assert.Equal(M("2"), returned[1]);
        Assert.Equal(M("1"), returned[2]);
        Assert.Equal(M("2"), returned[3]);
        Assert.Empty(store.ReturnedQtyByLine("RET-1"));
        Assert.Empty(store.ReturnedQtyByLine("NOPE"));
    }

    [Fact]
    public void Returns_of_other_tills_against_a_sale_of_this_till_are_found_once_it_is_uploaded()
    {
        var local = LocalSale("TILL2-20261006100000-000001");
        receipts.Save(local);
        store.Upsert(Return("RET-7", "ACC-PSINV-2026-00100", new DateTime(2026, 10, 6, 15, 0, 0), Milk("1", -1m)), Fetched);

        Assert.Empty(store.ReturnsAgainst(local));                        // not uploaded yet: its ERPNext name is unknown

        receipts.MarkSynced(local.ClientId, "ACC-PSINV-2026-00100");
        var ret = Assert.Single(store.ReturnsAgainst(local));
        Assert.Equal((local.ClientId, 1, M("-1")), (ret.ReturnAgainst, ret.Lines.Single().LineNo, ret.Lines.Single().Qty));
    }

    [Fact]
    public void Invoices_posted_before_the_window_are_pruned()
    {
        var today = new DateOnly(2026, 10, 7);
        store.Upsert(Sale("OLD", null, new DateTime(2026, 9, 6, 23, 59, 59), Milk("1", 1m)), Fetched);
        store.Upsert(Sale("EDGE", null, new DateTime(2026, 9, 7, 0, 0, 0), Milk("1", 1m)), Fetched);
        store.Upsert(Sale("NEW", null, new DateTime(2026, 10, 7, 9, 0, 0), Milk("1", 1m)), Fetched);

        Assert.Equal(1, store.DeleteOlderThan(30, today));

        Assert.Null(store.FindByErpName("OLD"));
        Assert.NotNull(store.FindByErpName("EDGE"));
        Assert.Equal(2, store.Count());
    }

    private static Receipt LocalSale(string id) => new(
        id, ReceiptKind.Sale, null, "S1", "simran", new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.FromHours(4)),
        [new ReceiptLine(1, "MILK", "Full Cream Milk 1L", "111", "PCS", 1m, 2m, M("6.79"), M("6.79"), M("13.58"), null, null, false, null)],
        M("13.58"), M("12.93"), M("0.65"), M("13.58"), false, 0m, 0m, [new ReceiptPayment("Credit Card", M("13.58"))], 0m, 0m, null);

    private static Receipt LocalReturn(string id, string against, int lineNo, decimal qty) => new(
        id, ReceiptKind.Return, against, "S1", "simran", new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(4)),
        [new ReceiptLine(lineNo, "MILK", "Full Cream Milk 1L", "111", "PCS", 1m, -qty, M("6.79"), M("6.79"), -qty * M("6.79"), null, null, false, null)],
        -qty * M("6.79"), 0m, 0m, -qty * M("6.79"), false, 0m, 0m, [new ReceiptPayment("Cash Counter 2", -qty * M("6.79"))], 0m, 0m, null);
}
