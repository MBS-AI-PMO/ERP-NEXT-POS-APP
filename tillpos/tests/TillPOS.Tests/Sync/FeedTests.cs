using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync;

public sealed class FeedTests : IDisposable
{
    private const string T1 = "2026-10-05 10:00:01.000000";
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CatalogStore store;
    private readonly SyncContext ctx;

    public FeedTests()
    {
        store = new CatalogStore(temp.Db);
        ctx = new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Till 1");
        store.SavePosSettings(new PosSettings("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", "Retail",
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));
    }

    public void Dispose() => temp.Dispose();

    private SqliteCatalog Catalog() => new(temp.Db);

    [Fact]
    public async Task Item_feed_stores_items_with_barcodes_uoms_and_taxes()
    {
        erp.AddRow("Item", new() { ["name"] = "WATER", ["modified"] = T1, ["item_name"] = "Water 500ml", ["item_group"] = "Food",
            ["brand"] = null, ["stock_uom"] = "Nos", ["disabled"] = 0, ["is_sales_item"] = 1 });
        erp.Override = q => q.Fields.Any(f => f.Contains("tabItem Barcode"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["barcode"] = "111", ["barcode_uom"] = null },
                   new Dictionary<string, object?> { ["name"] = "WATER", ["barcode"] = "112", ["barcode_uom"] = "Box" }]
            : q.Fields.Any(f => f.Contains("tabUOM Conversion Detail"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["uom"] = "Box", ["conversion_factor"] = 12 }]
            : q.Fields.Any(f => f.Contains("tabItem Tax"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["item_tax_template"] = null, ["tax_category"] = null, ["valid_from"] = null, ["idx"] = null }]
            : null;

        Assert.Equal(1, await new ItemFeed(ctx).RunAsync(default));

        var c = Catalog();
        Assert.Equal("Water 500ml", c.FindItem("WATER")!.ItemName);
        Assert.Equal("Box", c.FindBarcode("112")!.Uom);
        Assert.Equal(12m, c.ConversionFactor("WATER", "Box"));
        Assert.Empty(c.ItemTaxes("WATER"));
    }

    [Fact]
    public async Task Item_price_feed_keeps_only_retail_prices_without_customer()
    {
        void Price(string name, string list, string? customer, string rate) => erp.AddRow("Item Price", new()
        {
            ["name"] = name, ["modified"] = T1, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = rate,
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = list, ["customer"] = customer, ["batch_no"] = null,
        });
        Price("P-RETAIL", "Retail", null, "2150");
        Price("P-WHOLESALE", "Wholesale", null, "1900");
        Price("P-CUSTOMER", "Retail", "Big Buyer LLC", "1800");

        await new ItemPriceFeed(ctx).RunAsync(default);

        Assert.Equal(new[] { "P-RETAIL" }, store.AllPriceNames());
    }

    [Fact]
    public async Task Item_price_moved_off_retail_is_removed_locally()
    {
        store.UpsertPrices([new ItemPrice("P1", "RICE5", "Nos", 2150m, null, null)]);
        erp.AddRow("Item Price", new()
        {
            ["name"] = "P1", ["modified"] = T1, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = "2150",
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = "Wholesale", ["customer"] = null, ["batch_no"] = null,
        });

        await new ItemPriceFeed(ctx).RunAsync(default);

        Assert.Empty(store.AllPriceNames());
    }

    [Fact]
    public async Task Pricing_rule_feed_upserts_and_deletes_disabled_rules()
    {
        erp.AddRow("Pricing Rule", new() { ["name"] = "R-ON", ["modified"] = T1 });
        erp.AddRow("Pricing Rule", new() { ["name"] = "R-OFF", ["modified"] = T1 });
        erp.Docs[("Pricing Rule", "R-ON")] = new Dictionary<string, object?>
        {
            ["name"] = "R-ON", ["apply_on"] = "Item Code", ["items"] = new[] { new Dictionary<string, object?> { ["item_code"] = "RICE5" } },
            ["selling"] = 1, ["rate_or_discount"] = "Discount Percentage", ["discount_percentage"] = 10, ["priority"] = "1",
        };
        erp.Docs[("Pricing Rule", "R-OFF")] = new Dictionary<string, object?> { ["name"] = "R-OFF", ["apply_on"] = "Item Code", ["selling"] = 1, ["disable"] = 1 };
        store.UpsertPricingRule(new PricingRule("R-OFF", RuleApplyOn.ItemCode, ["X"], RuleKind.DiscountPercentage, 5m, 0, null, null, null, null, null));

        var feed = new DocFeed<PricingRule>(ctx, "Pricing Rule", d => TillPOS.Erp.Mapping.CatalogMapper.PricingRule(d), store.UpsertPricingRule, store.DeletePricingRule);
        await feed.RunAsync(default);

        Assert.Equal("R-ON", Assert.Single(Catalog().PricingRules()).Name);
    }

    [Fact]
    public async Task Deletion_feed_removes_deleted_items()
    {
        store.UpsertItems([new ItemSnapshot(new Item("GONE", "Gone", "Food", null, "Nos", false, true), [new ItemBarcode("9", "GONE", null)], [], [])]);
        erp.AddRow("Deleted Document", new() { ["name"] = "DD-1", ["modified"] = T1, ["deleted_doctype"] = "Item", ["deleted_name"] = "GONE" });

        await new DeletionFeed(ctx).RunAsync(default);

        Assert.Null(Catalog().FindBarcode("9"));
    }

    [Fact]
    public async Task Reconcile_removes_items_missing_on_server()
    {
        store.UpsertItems([
            new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], []),
            new ItemSnapshot(new Item("RENAMED-OLD", "Old name", "Food", null, "Nos", false, true), [], [], []),
        ]);
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });

        var removed = await new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "KEEP" }, store.AllItemCodes());
    }

    [Fact]
    public async Task Reconcile_refuses_to_wipe_everything_when_server_returns_no_items()
    {
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default));
        Assert.Equal(new[] { "KEEP" }, store.AllItemCodes());
    }

    [Fact]
    public async Task Reconcile_runs_at_most_once_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        await new ReconcileFeed(ctx, () => now).RunAsync(default);
        var callsAfterFirst = erp.ListCalls.Count;

        await new ReconcileFeed(ctx, () => now.AddHours(5)).RunAsync(default);

        Assert.Equal(callsAfterFirst, erp.ListCalls.Count);
    }

    [Fact]
    public async Task Deletion_feed_keeps_documents_recreated_under_the_same_name()
    {
        store.UpsertItems([new ItemSnapshot(new Item("BACK", "Back", "Food", null, "Nos", false, true), [new ItemBarcode("7", "BACK", null)], [], [])]);
        erp.AddRow("Item", new() { ["name"] = "BACK", ["modified"] = T1 });
        erp.AddRow("Deleted Document", new() { ["name"] = "DD-2", ["modified"] = T1, ["deleted_doctype"] = "Item", ["deleted_name"] = "BACK" });

        await new DeletionFeed(ctx).RunAsync(default);
        await new DeletionFeed(ctx).RunAsync(default);

        Assert.NotNull(Catalog().FindItem("BACK"));
    }

    [Fact]
    public async Task Item_price_feed_drops_old_list_prices_when_price_list_changes()
    {
        void Price(string name, string list, string at = T1) => erp.AddRow("Item Price", new()
        {
            ["name"] = name, ["modified"] = at, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = "2150",
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = list, ["customer"] = null, ["batch_no"] = null,
        });
        Price("P-OLD", "Retail");
        Price("P-NEW", "Retail 2");
        Price("P-LATE", "Retail", "2026-10-05 11:00:00.000000");
        await new ItemPriceFeed(ctx).RunAsync(default);
        store.SavePosSettings(new PosSettings("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", "Retail 2",
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));

        await new ItemPriceFeed(ctx).RunAsync(default);

        Assert.Equal(new[] { "P-NEW" }, store.AllPriceNames());
    }

    private void ServerPrice(string name, string? customer = null) => erp.AddRow("Item Price", new()
    {
        ["name"] = name, ["modified"] = T1, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = "2150",
        ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = "Retail", ["customer"] = customer, ["batch_no"] = null,
    });

    [Fact]
    public async Task Reconcile_backfills_prices_missing_locally()
    {
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        ServerPrice("P-1");
        ServerPrice("P-CUST", "Big Buyer LLC");

        await new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default);

        Assert.Equal(new[] { "P-1" }, store.AllPriceNames());
    }

    [Fact]
    public async Task Reconcile_refuses_to_wipe_prices_when_server_returns_none()
    {
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        store.UpsertPrices([new ItemPrice("P-LOCAL", "KEEP", "Nos", 1m, null, null)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default));
        Assert.Equal(new[] { "P-LOCAL" }, store.AllPriceNames());
    }

    [Fact]
    public async Task Puller_continues_after_a_feed_fails()
    {
        erp.Fail = q => q.Doctype == "Deleted Document" ? new ErpException(403, "Not permitted", "PermissionError") : null;
        erp.AddRow("Item", new() { ["name"] = "A", ["modified"] = T1, ["item_name"] = "A", ["item_group"] = "Food",
            ["brand"] = null, ["stock_uom"] = "Nos", ["disabled"] = 0, ["is_sales_item"] = 1 });
        var reloaded = false;
        var puller = new CatalogPuller([new DeletionFeed(ctx), new ItemFeed(ctx)], () => reloaded = true);

        var report = await puller.RunAsync();

        Assert.False(report.Ok);
        Assert.Equal("Not permitted", report.Feeds[0].Error);
        Assert.Null(report.Feeds[1].Error);
        Assert.NotNull(Catalog().FindItem("A"));
        Assert.True(reloaded);
    }

    private void UsePriceList(string list) => store.SavePosSettings(new PosSettings("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", list,
        "Walk-in Customer", "UAE VAT 5%", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));

    [Fact]
    public async Task Switching_price_list_back_restores_the_old_lists_prices()
    {
        void Price(string name, string list, string at = T1) => erp.AddRow("Item Price", new()
        {
            ["name"] = name, ["modified"] = at, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = "2150",
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = list, ["customer"] = null, ["batch_no"] = null,
        });
        Price("P-A0", "A", "2026-10-05 09:00:00.000000");
        Price("P-A1", "A");
        Price("P-A2", "A");
        Price("P-B1", "B");

        UsePriceList("A");
        await new ItemPriceFeed(ctx).RunAsync(default);
        Assert.Equal(new[] { "P-A0", "P-A1", "P-A2" }, store.AllPriceNames().Order());

        UsePriceList("B");
        await new ItemPriceFeed(ctx).RunAsync(default);
        Assert.Equal(new[] { "P-B1" }, store.AllPriceNames());

        UsePriceList("A");
        await new ItemPriceFeed(ctx).RunAsync(default);
        Assert.Equal(new[] { "P-A0", "P-A1", "P-A2" }, store.AllPriceNames().Order());
    }

    [Fact]
    public async Task Item_group_feed_refuses_to_wipe_groups_when_server_returns_none()
    {
        store.ReplaceItemGroups([new ItemGroupSnapshot(new ItemGroupNode("Food", null, 1, 2), [])]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemGroupFeed(ctx).RunAsync(default));

        Assert.NotNull(Catalog().FindGroup("Food"));
    }

    [Fact]
    public async Task Reconcile_runs_when_stored_time_is_in_the_future()
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        store.SetValue("reconciled_at", now.AddYears(2).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        await new ReconcileFeed(ctx, () => now).RunAsync(default);

        Assert.NotEmpty(erp.ListCalls);
    }
}
