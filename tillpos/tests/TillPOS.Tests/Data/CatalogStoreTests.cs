using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly CatalogStore store;

    public CatalogStoreTests() => store = new CatalogStore(temp.Db);

    public void Dispose() => temp.Dispose();

    private static ItemSnapshot Snap(string code, string name, params string[] barcodes) => new(
        new Item(code, name, "Food", null, "Nos", false, true),
        barcodes.Select(b => new ItemBarcode(b, code, null)).ToList(),
        [new ItemUom(code, "Nos", 1m)],
        []);

    [Fact]
    public void Connections_use_full_synchronous_commits()
    {
        using var c = temp.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous";
        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        temp.Db.Migrate();
        Assert.Empty(store.AllItemCodes());
    }

    [Fact]
    public void Item_with_barcodes_round_trips()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "8901234500024")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal("RICE5", catalog.FindBarcode("8901234500024")!.ItemCode);
        Assert.Equal("Basmati Rice 5kg", catalog.FindItem("RICE5")!.ItemName);
        Assert.Equal(1m, catalog.ConversionFactor("RICE5", "Nos"));
    }

    [Fact]
    public void Updating_an_item_replaces_its_barcodes()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "OLD")]);
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "NEW")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Null(catalog.FindBarcode("OLD"));
        Assert.NotNull(catalog.FindBarcode("NEW"));
    }

    [Fact]
    public void Barcode_moved_to_another_item_points_to_new_item()
    {
        store.UpsertItems([Snap("A", "Item A", "123")]);
        store.UpsertItems([Snap("B", "Item B", "123")]);
        Assert.Equal("B", new SqliteCatalog(temp.Db).FindBarcode("123")!.ItemCode);
    }

    [Fact]
    public void Prices_keep_exact_decimals()
    {
        store.UpsertItems([Snap("OIL", "Cooking Oil", "5")]);
        store.UpsertPrices([new ItemPrice("P1", "OIL", "Nos", M("579.50"), new DateOnly(2026, 10, 1), null)]);
        var price = Assert.Single(new SqliteCatalog(temp.Db).PricesFor("OIL"));
        Assert.Equal(M("579.50"), price.PriceListRate);
        Assert.Equal(new DateOnly(2026, 10, 1), price.ValidFrom);
    }

    [Fact]
    public void Deleting_an_item_removes_barcodes_prices_and_search_entry()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "1")]);
        store.UpsertPrices([new ItemPrice("P1", "RICE5", "Nos", 1m, null, null)]);
        store.DeleteDocument("Item", "RICE5");
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Null(catalog.FindItem("RICE5"));
        Assert.Null(catalog.FindBarcode("1"));
        Assert.Empty(catalog.PricesFor("RICE5"));
        Assert.Empty(catalog.Search("basmati"));
    }

    [Fact]
    public void Search_matches_word_prefixes_including_arabic()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "1"), Snap("RICE-AR", "أرز بسمتي", "2"), Snap("MILK", "Full Cream Milk", "3")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal("RICE5", Assert.Single(catalog.Search("basm ric")).ItemCode);
        Assert.Equal("RICE-AR", Assert.Single(catalog.Search("أرز")).ItemCode);
        Assert.Empty(catalog.Search("   "));
    }

    [Fact]
    public void Search_skips_disabled_and_non_sales_items_before_applying_the_limit()
    {
        store.UpsertItems([
            new ItemSnapshot(new Item("OLD-RICE", "Rice", "Food", null, "Nos", true, true), [], [], []),
            new ItemSnapshot(new Item("BAG", "Rice", "Food", null, "Nos", false, false), [], [], []),
            new ItemSnapshot(new Item("RICE5", "Rice Basmati 5kg", "Food", null, "Nos", false, true), [], [], []),
        ]);
        var found = new SqliteCatalog(temp.Db).Search("rice", limit: 1);
        Assert.Equal("RICE5", Assert.Single(found).ItemCode);
    }

    [Fact]
    public void Groups_rules_and_templates_round_trip()
    {
        store.ReplaceItemGroups([
            new ItemGroupSnapshot(new ItemGroupNode("All Item Groups", null, 1, 4), []),
            new ItemGroupSnapshot(new ItemGroupNode("Food", "All Item Groups", 2, 3), [new ItemTaxAssignment("Zero Rated", null, null, 1)]),
        ]);
        store.UpsertPricingRule(new PricingRule("R1", RuleApplyOn.ItemGroup, ["Food"], RuleKind.DiscountPercentage, 10m, 2, null, null, null, null, null));
        store.UpsertItemTaxTemplate(new ItemTaxTemplate("Zero Rated", new Dictionary<string, decimal> { ["VAT 5% - S"] = 0m }));
        store.UpsertSalesTaxTemplate(new SalesTaxTemplate("UAE VAT 5%", [new TaxRow(1, "VAT 5% - S", "VAT 5%", 5m, true)], null));
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal(2, catalog.FindGroup("Food")!.Lft);
        Assert.Equal("Zero Rated", Assert.Single(catalog.ItemGroupTaxes("Food")).ItemTaxTemplate);
        Assert.Equal(new[] { "Food" }, Assert.Single(catalog.PricingRules()).Targets);
        Assert.Equal(0m, catalog.FindItemTaxTemplate("Zero Rated")!.RatesByAccount["VAT 5% - S"]);
        Assert.True(catalog.FindSalesTaxTemplate("UAE VAT 5%")!.Rows[0].IncludedInPrintRate);

        store.DeletePricingRule("R1");
        catalog.Reload();
        Assert.Empty(catalog.PricingRules());
    }

    [Fact]
    public void Pos_settings_and_values_round_trip()
    {
        var s = new PosSettings("Till 1", "Shop LLC", "Shop LLC", "100000000000003", "Al Quoz, Dubai", "AED", "Stores - S", "Retail",
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, M("0.05"), [new PaymentMode("Cash", true), new PaymentMode("Card", false)]);
        store.SavePosSettings(s);
        store.SetValue("k", "v");

        var loaded = store.LoadPosSettings()!;
        Assert.Equal(s with { PaymentModes = loaded.PaymentModes }, loaded);   // records compare lists by reference
        Assert.Equal(s.PaymentModes, loaded.PaymentModes);                      // so compare the list elements separately
        Assert.Equal("v", store.GetValue("k"));
        Assert.Null(store.GetValue("missing"));
    }
}
