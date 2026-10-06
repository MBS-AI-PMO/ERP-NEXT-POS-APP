using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class PresentationFixture : IDisposable
{
    public static readonly Cashier Simran = new("simran", "Simran", "p.simran@quickgroc.com", PinHasher.Hash("1111"), false, true);
    public static readonly Cashier Sup = new("sup", "Supervisor", "sup@quickgroc.com", PinHasher.Hash("9999"), true, true);
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);

    public TempDb Temp { get; } = new();
    public InMemoryCatalog Catalog { get; } = InMemoryCatalog.WithStandardGroups();
    public FakeDialogs Dialogs { get; } = new();
    public FakeNavigator Navigator { get; } = new();
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(4)));
    public FakeOutput Output { get; } = new();
    public SessionState Session { get; } = new();
    public TillContext Ctx { get; }

    public PresentationFixture()
    {
        Catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        Catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        Catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        Catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        Catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        Catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));

        var db = Temp.Db;
        Ctx = new TillContext(
            2, new TenderModes("Cash Counter 2", "Credit Card"),
            () => new SaleContext(Catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m), "Standard Selling", "Stores - AAML",
                null, Vat, () => new DateOnly(2026, 10, 7)),
            text => Catalog.Items.Where(i => i.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList(),
            new Authenticator(() => [Simran, Sup]), new PinAttemptLimiter(() => Clock.Now),
            new ShiftStore(db), new ReceiptStore(db), new ApprovalStore(db), new CatalogStore(db),
            Clock, Output, Navigator, Dialogs);
    }

    public void LogInWithOpenShift()
    {
        Session.Cashier = Simran;
        var shift = new ShiftOpening("TILL2-SHIFT-20261007080000", "simran", Clock.Now.AddHours(-2), [new ReceiptPayment("Cash Counter 2", 200m)]);
        Ctx.Shifts.Open(shift);
        Session.Shift = shift;
    }

    public void Dispose() => Temp.Dispose();
}
