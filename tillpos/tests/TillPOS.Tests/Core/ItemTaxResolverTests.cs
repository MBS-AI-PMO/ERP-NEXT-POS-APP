using TillPOS.Core.Catalog;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Core;

public class ItemTaxResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", null, "Nos", false, true);

    private string? Resolve(string? category = null) => new ItemTaxResolver(catalog, category).TemplateFor(rice, Today);

    [Fact]
    public void No_rows_means_no_item_template() => Assert.Null(Resolve());

    [Fact]
    public void Item_row_beats_group_row()
    {
        catalog.GroupTaxRows["Rice"] = [new ItemTaxAssignment("Group T", null, null, 1)];
        catalog.ItemTaxRows["RICE5"] = [new ItemTaxAssignment("Item T", null, null, 1)];
        Assert.Equal("Item T", Resolve());
    }

    [Fact]
    public void Parent_group_row_is_inherited()
    {
        catalog.GroupTaxRows["Food"] = [new ItemTaxAssignment("Food T", null, null, 1)];
        Assert.Equal("Food T", Resolve());
    }

    [Fact]
    public void Rows_for_another_tax_category_are_skipped()
    {
        catalog.ItemTaxRows["RICE5"] = [new ItemTaxAssignment("Export T", "Export", null, 1)];
        Assert.Null(Resolve());
        Assert.Equal("Export T", Resolve("Export"));
    }

    [Fact]
    public void Future_rows_are_skipped_and_latest_started_row_wins()
    {
        catalog.ItemTaxRows["RICE5"] =
        [
            new ItemTaxAssignment("Old T", null, new DateOnly(2025, 1, 1), 1),
            new ItemTaxAssignment("Current T", null, new DateOnly(2026, 1, 1), 2),
            new ItemTaxAssignment("Future T", null, new DateOnly(2027, 1, 1), 3),
        ];
        Assert.Equal("Current T", Resolve());
    }
}
