using TillPOS.Core.Sales;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>A bill uploads with the POS Profile, warehouse and rounding it was taken with, even when the shift's counter
/// settings differ by the time it is uploaded; bills from before these were saved use the shift's counter.</summary>
public partial class PayloadTests
{
    private static Receipt CashBill() => Sale("TILL2-20261006153005-000009",
        [Line(1, "MILK", "Full Cream Milk 1L", 1m, M("6.79"), M("6.79"), M("6.790"))],
        M("6.790"), true, M("6.750"), [new ReceiptPayment("Cash Counter 1", 10m)], M("3.25"));

    [Fact]
    public void The_bills_own_profile_and_warehouse_win_over_the_shifts_counter()
    {
        var r = CashBill() with { PosProfile = "Test Counter", Warehouse = "Test Stores - AAML", DisableRoundedTotal = false };

        var doc = Build(r).Doc;

        Assert.Equal("Test Counter", doc["pos_profile"]);
        Assert.Equal("Test Stores - AAML", doc["set_warehouse"]);
        Assert.Equal("Test Stores - AAML", Assert.Single(Rows(doc, "items"))["warehouse"]);
        Assert.Equal(0, doc["disable_rounded_total"]);
    }

    [Fact]
    public void A_bill_taken_with_the_rounded_total_disabled_stays_disabled()
    {
        var r = CashBill() with { DisableRoundedTotal = true };
        Assert.Equal(1, Build(r).Doc["disable_rounded_total"]);
    }

    [Fact]
    public void An_older_bill_uses_the_shifts_counter()
    {
        var doc = Build(CashBill()).Doc;

        Assert.Equal("Al Ain Counter 1", doc["pos_profile"]);
        Assert.Equal("Stores - AAML", doc["set_warehouse"]);
        Assert.Equal("Stores - AAML", Assert.Single(Rows(doc, "items"))["warehouse"]);
        Assert.Equal(0, doc["disable_rounded_total"]);
    }
}
