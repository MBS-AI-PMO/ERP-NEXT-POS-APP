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

    /// <summary>The default counter (the fixture's usual one): rounded cash, "Cash Counter 2".</summary>
    public static readonly CounterSettings CounterTwo = new("Al Ain Counter 2", "Counter 2", "Cash Counter 2", "Credit Card");

    /// <summary>A second counter like the live "Test Counter": its POS Profile disables the rounded total, so cash is exact (the
    /// live "Al Ain Counter 1/2" round cash to 0.25, like <see cref="CounterTwo"/>). Its own cash mode tells the counters apart.</summary>
    public static readonly CounterSettings TestCounter = new("Test Counter", "Test Counter", "Cash Counter 1", "Credit Card");

    /// <summary>A counter whose POS Profile sells from another price list (prices are synced for the default one only).</summary>
    public static readonly CounterSettings WholesaleCounter = new("Wholesale Counter", "Wholesale", "Cash Wholesale", "Credit Card");

    /// <summary>Profiles whose POS settings the fake "has not downloaded" (tests add to it).</summary>
    public HashSet<string> NotDownloaded { get; } = [];

    /// <summary>A configured counter whose POS settings were never downloaded.</summary>
    public static readonly CounterSettings CounterNine = new("Al Ain Counter 9", "Counter 9", "Cash Counter 9", "Credit Card");

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
            2, [CounterTwo, TestCounter],
            profile => NotDownloaded.Contains(profile)
                ? throw new InvalidOperationException($"{profile} is not downloaded.")
                : profile switch
            {
                "Al Ain Counter 2" => new SaleContext(Catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m), "Standard Selling",
                    "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 7))
                    { PosProfile = "Al Ain Counter 2", Company = "Al Ain Marketing LLC" },
                "Test Counter" => new SaleContext(Catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m, DisableRoundedTotal: true),
                    "Standard Selling", "Test Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 7))
                    { PosProfile = "Test Counter", Company = "Al Ain Marketing LLC" },
                "Wholesale Counter" => new SaleContext(Catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m), "Wholesale",
                    "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 7))
                    { PosProfile = "Wholesale Counter", Company = "Al Ain Marketing LLC" },
                _ => throw new InvalidOperationException($"The POS settings of {profile} are not downloaded yet."),
            },
            text => Catalog.Items.Where(i => i.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList(),
            new Authenticator(() => [Simran, Sup]), new PinAttemptLimiter(() => Clock.Now), new PinAttemptLimiter(() => Clock.Now),
            new ShiftStore(db), new ReceiptStore(db), new ApprovalStore(db), new CatalogStore(db),
            Clock, Output, Navigator, Dialogs, ShowReceiptPreview: true, new HeldCartStore(db))
        {
            RemoteReceipts = new RemoteReceiptStore(db),
        };
    }

    /// <summary>A shift opened at 08:00 with a 200 float at <paramref name="counter"/> (default: Counter 2), joined by Simran.</summary>
    public void LogInWithOpenShift(CounterSettings? counter = null)
    {
        counter ??= CounterTwo;
        Session.Cashier = Simran;
        var shift = new ShiftOpening("TILL2-SHIFT-20261007080000", "simran", counter.PosProfile, Clock.Now.AddHours(-2),
            [new ReceiptPayment(counter.CashMode, 200m)])
        {
            CounterName = counter.DisplayName,
            CashMode = counter.CashMode,
            CardMode = counter.CardMode,
        };
        Ctx.Shifts.Open(shift);
        Session.Shift = shift;
        Session.Counter = counter;
    }

    public void Dispose() => Temp.Dispose();
}
