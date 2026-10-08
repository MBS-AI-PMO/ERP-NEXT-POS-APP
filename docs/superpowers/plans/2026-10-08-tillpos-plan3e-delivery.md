# TillPOS Delivery Bills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delivery bills on the till: make an unpaid delivery from the sale screen (NOT PAID slip), keep it in a Delivery list, change/cancel it with a supervisor, and take the exact payment later in whichever shift is open; the Z report shows deliveries paid and still out.

**Architecture:** A delivery is stored on the till only (new `delivery` table) as its unpaid bill — a `Receipt` with no payments, fixed prices — plus the cash/card amounts to collect. Paying restores those lines into a cart *at their stored prices* (`Cart.RestoreFixed`) and runs the normal payment screen through a new `IPaymentHost` interface (the sale screen and the new `DeliveriesViewModel` both implement it); the paid receipt keeps the delivery's number and is saved together with the delivery's status change in one transaction, then uploads like any sale.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit. Build/test: `& 'C:\Program Files\dotnet\dotnet.exe' test -m:1` from `tillpos/`.

**Spec:** `docs/superpowers/specs/2026-10-08-tillpos-delivery-design.md`

## Global Constraints

- ERPNext learns about a delivery only once it is paid; an open or cancelled delivery never uploads.
- No customer details are recorded.
- Prices are fixed when the delivery is made; paying never re-prices.
- A paid delivery is a normal sale of the shift open at payment: payment time, that shift's counter / POS Profile / payment modes; it keeps its bill number.
- Payment is exact: Cash pre-filled with the amount due (change 0.00); card is the total to 2 decimals; split as for any bill. Cash rounding to AED 0.25 as for every bill.
- Change items and Cancel need a supervisor PIN and are logged (`ApprovalAction.DeliveryChange`, `ApprovalAction.DeliveryCancel`, appended to the enum).
- Cancel reasons: "Refused", "Not delivered", "Other".
- Amounts shown/printed with 2 decimals (`Format.Money`, `ReceiptRenderer.Money`).
- Version 0.4.4 (`0.4.4-test`).
- Ruling (plan): the "N deliveries out" count is a badge on the Deliveries button (like Recall's), not in the window header — same information, no shell change. Recorded for the owner's review.

## Review Focus

1. The price list changes between making and paying a delivery → it is paid at the delivery's prices (Task 5 test `Paying_uses_the_delivery_prices_after_a_price_change`).
2. A delivery made at Counter 2 is paid in a shift at Test Counter → the receipt uses Test Counter's cash mode and profile (Task 5 test `Paying_in_a_shift_at_another_counter_uses_that_counters_modes`).
3. The same delivery is paid twice, or paid after it was cancelled (stale screen) → the second is refused, one receipt only (Task 2 test `Pay_is_refused_once_paid_or_cancelled_and_saves_no_receipt`).
4. The till restarts with deliveries out → they are still listed (Task 2 test `Open_deliveries_survive_a_new_store_instance`).
5. A slip barcode scanned on the sale screen for a paid/cancelled delivery, or while the bill has items → a clear message, no item added (Task 5 tests `Scanning_a_paid_delivery_slip_says_so`, `Scanning_a_slip_with_items_on_the_bill_asks_to_finish_first`).

---

### Task 1: Core — delivery model, fixed-price restore, recorder split

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/Delivery.cs`
- Modify: `tillpos/src/TillPOS.Core/Sales/Receipt.cs` (add `IsDelivery`)
- Modify: `tillpos/src/TillPOS.Core/Sales/Cart.cs` (add `RestoreFixed`)
- Modify: `tillpos/src/TillPOS.Core/Sales/SaleRecorder.cs` (split `Build` out of `CompleteSale`)
- Modify: `tillpos/src/TillPOS.Core/Security/Security.cs` (append `DeliveryChange, DeliveryCancel`)
- Test: `tillpos/tests/TillPOS.Tests/Core/DeliveryTests.cs`

**Interfaces:**
- Produces:
  - `enum DeliveryStatus { Open, Paid, Cancelled }`
  - `record Delivery(string ClientId, DeliveryStatus Status, DateTimeOffset CreatedAt, string ShiftClientId, string Cashier, string? CashierName, string? CounterName, Receipt Bill, decimal CashToCollect, decimal CardToCollect)` with init `ClosedAt`, `ClosedBy`, `Reason`
  - `record DeliverySummary(int PaidCount, decimal PaidTotal, IReadOnlyList<Delivery> StillOut)`
  - `static class Deliveries { Delivery Make(Cart cart, string clientId, DateTimeOffset at, string shiftClientId, string cashier, string? cashierName, string? counterName); Delivery Rebill(Delivery d, Cart cart); }`
  - `Receipt.IsDelivery` (init bool)
  - `Cart.RestoreFixed(IEnumerable<ReceiptLine> lines)` (void; throws when the cart has lines)
  - `SaleRecorder.Build(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser, string? cashierName, string? deliveryClientId)` → `Receipt` (does not save, does not clear)
  - `ApprovalAction.DeliveryChange`, `ApprovalAction.DeliveryCancel`

- [ ] **Step 1: Write the failing tests** — `tillpos/tests/TillPOS.Tests/Core/DeliveryTests.cs`

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class DeliveryTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 10, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    public DeliveryTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("11.429"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
    }

    private Cart NewCart() => new(new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 8))
        { PosProfile = "Al Ain Counter 2" });

    private Delivery MakeOne(int milks = 1)
    {
        var cart = NewCart();
        for (var i = 0; i < milks; i++) cart.AddBarcode("111");
        return Deliveries.Make(cart, "TILL2-20261008151000-000042", At, "TILL2-SHIFT-1", "simran", "Simran", "Counter 2");
    }

    [Fact]
    public void A_delivery_is_the_unpaid_bill_with_both_amounts_to_collect()
    {
        var d = MakeOne();

        Assert.Equal(DeliveryStatus.Open, d.Status);
        Assert.Equal("TILL2-20261008151000-000042", d.Bill.ClientId);
        Assert.Empty(d.Bill.Payments);
        Assert.True(d.Bill.IsDelivery);
        Assert.Equal(M("11.429"), d.Bill.GrandTotal);
        Assert.Equal(M("11.50"), d.CashToCollect);              // cash rounded to 0.25
        Assert.Equal(M("11.43"), d.CardToCollect);              // card to 2 decimals
        Assert.Equal("Al Ain Counter 2", d.Bill.PosProfile);
        Assert.Equal("MILK", Assert.Single(d.Bill.Lines).ItemCode);
    }

    [Fact]
    public void Restoring_a_delivery_keeps_its_prices_even_after_a_price_change()
    {
        var d = MakeOne(2);
        catalog.Prices.Clear();
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("20.000"), null, null));

        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("11.429"), line.Rate);
        Assert.Equal(2m, line.Qty);
        Assert.Equal(M("22.858"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Restoring_works_for_an_item_no_longer_in_the_catalog()
    {
        var d = MakeOne();
        catalog.Items.Clear();

        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);

        Assert.Equal("Full Cream Milk 1L", Assert.Single(cart.Lines).Item.ItemName);
    }

    [Fact]
    public void Restoring_onto_a_bill_with_lines_is_refused()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        Assert.Throws<InvalidOperationException>(() => cart.RestoreFixed(MakeOne().Bill.Lines));
    }

    [Fact]
    public void Rebill_after_removing_items_recalculates_the_bill_and_amounts()
    {
        var d = MakeOne(2);
        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);
        cart.Decrement(cart.Lines[0].Id);

        var changed = Deliveries.Rebill(d, cart);

        Assert.Equal(d.ClientId, changed.ClientId);
        Assert.Equal(M("11.429"), changed.Bill.GrandTotal);
        Assert.Equal(M("11.50"), changed.CashToCollect);
        Assert.Equal(M("11.43"), changed.CardToCollect);
        Assert.Equal(1m, Assert.Single(changed.Bill.Lines).Qty);
    }

    [Fact]
    public void Build_with_a_delivery_number_keeps_it_and_marks_the_receipt()
    {
        var d = MakeOne();
        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);
        var plan = new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Cash(M("11.50")));
        var store = new InMemoryReceiptStore();
        var recorder = new SaleRecorder(store, 2, new TenderModes("Cash Counter 1", "Credit Card"), () => At.AddHours(2), "Test Counter");

        var receipt = recorder.Build(cart, plan, "simran", "TILL2-SHIFT-2", null, "Simran", d.ClientId);

        Assert.Equal(d.ClientId, receipt.ClientId);
        Assert.True(receipt.IsDelivery);
        Assert.Equal(At.AddHours(2), receipt.CreatedAt);                      // the payment time
        Assert.Equal("TILL2-SHIFT-2", receipt.ShiftClientId);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 1", M("11.50")) }, receipt.Payments);
        Assert.Equal(0m, receipt.Change);
        Assert.Empty(store.Saved);                                               // Build never saves
        Assert.Single(cart.Lines);                                               // nor clears
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `& 'C:\Program Files\dotnet\dotnet.exe' test -m:1 --filter "FullyQualifiedName~DeliveryTests"` (from `tillpos/`)
Expected: build errors (`Deliveries`, `RestoreFixed`, `Build`, `IsDelivery` do not exist).

- [ ] **Step 3: Implement**

`Receipt.cs` — after `ExactCardOnRoundedTotal`:

```csharp
    /// <summary>A delivery bill: the unpaid bill stored with a delivery (no payments), or the sale it became once paid (it keeps
    /// the delivery's number). The paid invoice prints "DELIVERY - PAID".</summary>
    public bool IsDelivery { get; init; }
```

`Security.cs` — the enum's last line becomes `ReturnCrossTillOffline, ShiftClose, DeliveryChange, DeliveryCancel }`.

`Cart.cs` — after `Restore`:

```csharp
    /// <summary>Re-adds a stored bill's lines at their stored prices (a delivery: never re-priced), into an empty cart. An item no
    /// longer in the catalog keeps its stored name and unit.</summary>
    public void RestoreFixed(IEnumerable<ReceiptLine> stored)
    {
        if (lines.Count > 0) throw new InvalidOperationException("Finish or hold the current bill first.");
        foreach (var s in stored)
        {
            var item = ctx.Catalog.FindItem(s.ItemCode) ?? new Item(s.ItemCode, s.ItemName, "", null, s.Uom, false, true);
            var line = new CartLine(item, s.Uom, s.ConversionFactor, s.PriceListRate, null, s.Rate, s.ItemTaxTemplate, s.Barcode,
                s.FromScaleLabel ? s.Qty : null, s.UomFallbackFrom);
            if (!s.FromScaleLabel) line.Qty = s.Qty;
            lines.Add(line);
        }
    }
```

`SaleRecorder.cs` — replace `CompleteSale` with:

```csharp
    public Receipt CompleteSale(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser = null,
        string? cashierName = null)
    {
        var receipt = Build(cart, plan, cashier, shiftClientId, cashierUser, cashierName, null);
        store.Save(receipt);
        cart.Clear();
        return receipt;
    }

    /// <summary>The paid bill as a receipt, not saved and the cart not cleared. <paramref name="deliveryClientId"/>: a delivery
    /// being paid keeps its number (and the receipt is marked <see cref="Receipt.IsDelivery"/>); null takes the next number.</summary>
    public Receipt Build(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser, string? cashierName,
        string? deliveryClientId)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var totals = cart.Totals();
        if (plan.GrandTotal != totals.GrandTotal)
            throw new InvalidOperationException("The payment was calculated for a different total; calculate it again.");
        if (!plan.IsComplete) throw new InvalidOperationException($"Still to pay: {plan.Shortfall}.");

        var at = now();
        // Card only with rounding on: the card pays the exact total, ERPNext still gets its rounded total (Receipt.ExactCardOnRoundedTotal).
        var exactCard = plan.Kind == TenderKind.Card && !totals.RoundedTotalDisabled;
        return new Receipt(
            deliveryClientId ?? ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Sale, null, shiftClientId, cashier, at,
            ToLines(cart, totals), totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : exactCard ? totals.RoundedTotal : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : exactCard ? totals.RoundingAdjustment : 0m,
            Payments(plan, modes), plan.Change, plan.RoundingDifference, null)
        {
            ExactCardOnRoundedTotal = exactCard,
            CashierUser = cashierUser,
            CashierName = cashierName,
            CounterName = counterName,
            PosProfile = cart.Context.PosProfile,
            Warehouse = cart.Context.Warehouse,
            DisableRoundedTotal = cart.Context.Money.DisableRoundedTotal,
            IsDelivery = deliveryClientId is not null,
        };
    }
```

(Keep the body's existing details exactly as in the current `CompleteSale`; only the id, `IsDelivery` and the save/clear move.)

Create `Delivery.cs`:

```csharp
using TillPOS.Core.Payments;

namespace TillPOS.Core.Sales;

public enum DeliveryStatus { Open, Paid, Cancelled }

/// <summary>A delivery bill kept on the till until the driver brings the money (never uploaded before). <see cref="Bill"/> is
/// the unpaid bill as made — its lines carry the fixed prices, it has no payments, and its ClientId is the delivery's number.
/// The amounts to collect: cash rounded like any cash bill, card to 2 decimals.</summary>
public sealed record Delivery(string ClientId, DeliveryStatus Status, DateTimeOffset CreatedAt, string ShiftClientId, string Cashier,
    string? CashierName, string? CounterName, Receipt Bill, decimal CashToCollect, decimal CardToCollect)
{
    /// <summary>When it was paid or cancelled.</summary>
    public DateTimeOffset? ClosedAt { get; init; }
    /// <summary>Who paid it (cashier id) or the supervisor who cancelled it.</summary>
    public string? ClosedBy { get; init; }
    /// <summary>A cancel's reason ("Refused", "Not delivered", "Other").</summary>
    public string? Reason { get; init; }
}

/// <summary>For the shift (Z) report: deliveries paid in the shift (already in its sales) and those still out.</summary>
public sealed record DeliverySummary(int PaidCount, decimal PaidTotal, IReadOnlyList<Delivery> StillOut);

public static class Deliveries
{
    /// <summary>The cart as an open delivery (the cart is not cleared).</summary>
    public static Delivery Make(Cart cart, string clientId, DateTimeOffset at, string shiftClientId, string cashier, string? cashierName,
        string? counterName)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var bill = Bill(cart, clientId, at, shiftClientId, cashier, cashierName, counterName);
        var (cash, card) = ToCollect(cart);
        return new Delivery(clientId, DeliveryStatus.Open, at, shiftClientId, cashier, cashierName, counterName, bill, cash, card);
    }

    /// <summary>The delivery with the cart's (changed) lines: bill and amounts worked out again; number, time and maker kept.</summary>
    public static Delivery Rebill(Delivery d, Cart cart)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("A delivery needs at least one line; cancel it instead.");
        var (cash, card) = ToCollect(cart);
        return d with
        {
            Bill = Bill(cart, d.ClientId, d.CreatedAt, d.ShiftClientId, d.Cashier, d.CashierName, d.CounterName),
            CashToCollect = cash,
            CardToCollect = card,
        };
    }

    private static Receipt Bill(Cart cart, string clientId, DateTimeOffset at, string shiftClientId, string cashier, string? cashierName,
        string? counterName)
    {
        var totals = cart.Totals();
        return new Receipt(clientId, ReceiptKind.Sale, null, shiftClientId, cashier, at, SaleRecorder.ToLines(cart, totals),
            totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal, false, 0m, 0m, [], 0m, 0m, null)
        {
            CashierName = cashierName,
            CounterName = counterName,
            PosProfile = cart.Context.PosProfile,
            Warehouse = cart.Context.Warehouse,
            IsDelivery = true,
        };
    }

    private static (decimal Cash, decimal Card) ToCollect(Cart cart)
    {
        var calc = new PaymentCalculator(cart.Context.Money);
        var grand = cart.Totals().GrandTotal;
        return (calc.Plan(grand, Tender.Cash(0m)).AmountDue, calc.Plan(grand, Tender.Card()).AmountDue);
    }
}
```

- [ ] **Step 4: Run all tests** — Run: `& 'C:\Program Files\dotnet\dotnet.exe' test -m:1`. Expected: PASS (all, including existing `SaleRecorderTests`).

- [ ] **Step 5: Commit**

```bash
git add tillpos/src/TillPOS.Core tillpos/tests/TillPOS.Tests/Core/DeliveryTests.cs
git commit -m "feat(core): delivery bills: model, fixed-price restore, recorder build split"
```

---

### Task 2: Data — delivery table and store (pay in one transaction)

**Files:**
- Modify: `tillpos/src/TillPOS.Data/Migrations.cs` (append one entry)
- Modify: `tillpos/src/TillPOS.Data/ReceiptStore.cs` (extract `Insert`, add `RaiseSaved`)
- Create: `tillpos/src/TillPOS.Data/DeliveryStore.cs`
- Test: `tillpos/tests/TillPOS.Tests/Data/DeliveryStoreTests.cs`

**Interfaces:**
- Consumes: `Delivery`, `DeliveryStatus`, `Deliveries.Make`, `Receipt.IsDelivery` (Task 1).
- Produces: `DeliveryStore(TillDb db, ReceiptStore receipts)` with `void Add(Delivery d)`, `Delivery? Get(string clientId)`, `IReadOnlyList<Delivery> Open()` (oldest first), `bool Update(Delivery d)` (only while Open), `bool Cancel(string clientId, DateTimeOffset at, string by, string reason)`, `bool Pay(Receipt receipt, DateTimeOffset at, string by)`. `ReceiptStore.Insert(SqliteConnection, SqliteTransaction?, Receipt)` (internal static), `ReceiptStore.RaiseSaved()` (internal).

- [ ] **Step 1: Write the failing tests** — `tillpos/tests/TillPOS.Tests/Data/DeliveryStoreTests.cs`

```csharp
using TillPOS.Core.Sales;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class DeliveryStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ReceiptStore receipts;
    private readonly DeliveryStore store;

    public DeliveryStoreTests()
    {
        receipts = new ReceiptStore(temp.Db);
        store = new DeliveryStore(temp.Db, receipts);
    }

    public void Dispose() => temp.Dispose();

    private static Receipt Bill(string id, decimal grand) => new(id, ReceiptKind.Sale, null, "S1", "simran", At,
        [new ReceiptLine(1, "MILK", "Milk", "111", "PCS", 1m, 1m, grand, grand, grand, null, null, false, null)],
        grand, grand, 0m, grand, false, 0m, 0m, [], 0m, 0m, null) { IsDelivery = true };

    private static Delivery Open(string id, DateTimeOffset at, decimal grand = 11.429m) =>
        new(id, DeliveryStatus.Open, at, "S1", "simran", "Simran", "Counter 2", Bill(id, grand), 11.50m, 11.43m);

    [Fact]
    public void Added_deliveries_are_listed_oldest_first_and_read_back_whole()
    {
        store.Add(Open("TILL2-B", At.AddMinutes(5)));
        store.Add(Open("TILL2-A", At));

        Assert.Equal(new[] { "TILL2-A", "TILL2-B" }, store.Open().Select(d => d.ClientId));
        var a = store.Get("TILL2-A")!;
        Assert.Equal(M("11.50"), a.CashToCollect);
        Assert.True(a.Bill.IsDelivery);
        Assert.Equal("MILK", Assert.Single(a.Bill.Lines).ItemCode);
    }

    [Fact]
    public void Open_deliveries_survive_a_new_store_instance()
    {
        store.Add(Open("TILL2-A", At));
        Assert.Single(new DeliveryStore(temp.Db, new ReceiptStore(temp.Db)).Open());
    }

    [Fact]
    public void Update_changes_an_open_delivery_only()
    {
        store.Add(Open("TILL2-A", At));
        Assert.True(store.Update(Open("TILL2-A", At, 5m) with { CashToCollect = 5m }));
        Assert.Equal(5m, store.Get("TILL2-A")!.CashToCollect);

        Assert.True(store.Cancel("TILL2-A", At.AddHours(1), "sup", "Refused"));
        Assert.False(store.Update(Open("TILL2-A", At, 1m)));
    }

    [Fact]
    public void Cancel_closes_it_with_reason_and_supervisor()
    {
        store.Add(Open("TILL2-A", At));
        Assert.True(store.Cancel("TILL2-A", At.AddHours(1), "sup", "Refused"));

        var d = store.Get("TILL2-A")!;
        Assert.Equal(DeliveryStatus.Cancelled, d.Status);
        Assert.Equal(("sup", "Refused", At.AddHours(1)), (d.ClosedBy, d.Reason, d.ClosedAt!.Value));
        Assert.Empty(store.Open());
        Assert.Null(receipts.Get("TILL2-A"));
    }

    [Fact]
    public void Pay_saves_the_receipt_and_closes_the_delivery_together()
    {
        store.Add(Open("TILL2-A", At));
        var saved = 0;
        receipts.Saved += () => saved++;
        var paid = Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)], CreatedAt = At.AddHours(2) };

        Assert.True(store.Pay(paid, At.AddHours(2), "simran"));

        Assert.Equal(DeliveryStatus.Paid, store.Get("TILL2-A")!.Status);
        Assert.Equal(At.AddHours(2), receipts.Get("TILL2-A")!.CreatedAt);
        Assert.Empty(store.Open());
        Assert.Equal(1, saved);                                  // the upload is nudged like any sale
    }

    [Fact]
    public void Pay_is_refused_once_paid_or_cancelled_and_saves_no_receipt()
    {
        store.Add(Open("TILL2-A", At));
        store.Add(Open("TILL2-B", At));
        var paidA = Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] };
        Assert.True(store.Pay(paidA, At, "simran"));
        Assert.False(store.Pay(paidA, At, "simran"));            // paid twice

        store.Cancel("TILL2-B", At, "sup", "Refused");
        var paidB = Bill("TILL2-B", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] };
        Assert.False(store.Pay(paidB, At, "simran"));            // paid after cancel
        Assert.Null(receipts.Get("TILL2-B"));
        Assert.Single(receipts.ListPending(10));
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~DeliveryStoreTests"`. Expected: build error (`DeliveryStore` missing).

- [ ] **Step 3: Implement**

`Migrations.cs` — append after the 0.4.0 entry:

```csharp
        // 0.4.4: delivery bills kept on the till until paid (json = the Delivery record).
        """
        CREATE TABLE delivery (client_id TEXT PRIMARY KEY, status TEXT NOT NULL, created_at TEXT NOT NULL, json TEXT NOT NULL);
        CREATE INDEX ix_delivery_status ON delivery(status, created_at);
        """,
```

`ReceiptStore.cs` — replace `Save` with:

```csharp
    public void Save(Receipt receipt)
    {
        using (var c = db.Open()) Insert(c, null, receipt);
        Saved?.Invoke();
    }

    /// <summary>Inserts a receipt row (inside the caller's transaction, if any). Callers raise <see cref="Saved"/> afterwards.</summary>
    internal static void Insert(SqliteConnection c, SqliteTransaction? tx, Receipt receipt) =>
        c.Exec(tx, """
            INSERT INTO receipt (client_id, kind, return_against, shift_client_id, created_at, json)
            VALUES (@id, @k, @ra, @s, @at, @j)
            """,
            ("@id", receipt.ClientId), ("@k", receipt.Kind.ToString()), ("@ra", receipt.ReturnAgainst), ("@s", receipt.ShiftClientId),
            ("@at", receipt.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("@j", JsonSerializer.Serialize(receipt, Json)));

    /// <summary>Tells the upload a bill was saved by another store's transaction (a paid delivery).</summary>
    internal void RaiseSaved() => Saved?.Invoke();
```

Create `DeliveryStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

/// <summary>Delivery bills on this till (never uploaded while open). Paying writes the receipt and closes the delivery in one
/// transaction, so a crash cannot leave a paid bill still listed, or a paid delivery without its receipt.</summary>
public sealed class DeliveryStore(TillDb db, ReceiptStore receipts)
{
    public void Add(Delivery d)
    {
        using var c = db.Open();
        c.Exec(null, "INSERT INTO delivery (client_id, status, created_at, json) VALUES (@id, @s, @at, @j)",
            ("@id", d.ClientId), ("@s", d.Status.ToString()), ("@at", Utc(d.CreatedAt)), ("@j", JsonSerializer.Serialize(d, ReceiptStore.Json)));
    }

    public Delivery? Get(string clientId)
    {
        using var c = db.Open();
        return c.Scalar(null, "SELECT json FROM delivery WHERE client_id = @id", ("@id", clientId)) is string json ? Read(json) : null;
    }

    /// <summary>The deliveries still out, oldest first.</summary>
    public IReadOnlyList<Delivery> Open()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM delivery WHERE status = 'Open' ORDER BY created_at", r => Read(r.GetString(0)));
    }

    /// <summary>Saves a changed open delivery; false when it is no longer open.</summary>
    public bool Update(Delivery d)
    {
        using var c = db.Open();
        return c.Exec(null, "UPDATE delivery SET json = @j WHERE client_id = @id AND status = 'Open'",
            ("@id", d.ClientId), ("@j", JsonSerializer.Serialize(d with { Status = DeliveryStatus.Open }, ReceiptStore.Json))) > 0;
    }

    public bool Cancel(string clientId, DateTimeOffset at, string by, string reason) =>
        Close(clientId, DeliveryStatus.Cancelled, at, by, reason, null);

    /// <summary>Saves the paid receipt (its ClientId is the delivery's) and marks the delivery paid, together; false (and nothing
    /// saved) when it is no longer open.</summary>
    public bool Pay(Receipt receipt, DateTimeOffset at, string by)
    {
        if (!Close(receipt.ClientId, DeliveryStatus.Paid, at, by, null, receipt)) return false;
        receipts.RaiseSaved();
        return true;
    }

    private bool Close(string clientId, DeliveryStatus status, DateTimeOffset at, string by, string? reason, Receipt? paid)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (c.Scalar(tx, "SELECT json FROM delivery WHERE client_id = @id AND status = 'Open'", ("@id", clientId)) is not string json)
            return false;
        var closed = Read(json) with { Status = status, ClosedAt = at, ClosedBy = by, Reason = reason };
        if (paid is not null) ReceiptStore.Insert(c, tx, paid);
        c.Exec(tx, "UPDATE delivery SET status = @s, json = @j WHERE client_id = @id",
            ("@id", clientId), ("@s", status.ToString()), ("@j", JsonSerializer.Serialize(closed, ReceiptStore.Json)));
        tx.Commit();
        return true;
    }

    private static Delivery Read(string json) =>
        JsonSerializer.Deserialize<Delivery>(json, ReceiptStore.Json) ?? throw new InvalidDataException("Stored delivery is empty.");

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run all tests** — Expected: PASS (the schema-version tests follow `Migrations.All.Length`).

- [ ] **Step 5: Commit**

```bash
git add tillpos/src/TillPOS.Data tillpos/tests/TillPOS.Tests/Data/DeliveryStoreTests.cs
git commit -m "feat(data): delivery table and store; paying saves the receipt and closes the delivery together"
```

---

### Task 3: Printing — delivery slip, paid marker, Z report section

**Files:**
- Modify: `tillpos/src/TillPOS.Printing/ReceiptRenderer.cs`
- Modify: `tillpos/src/TillPOS.Printing/ShiftReportRenderer.cs`
- Modify: `tillpos/src/TillPOS.Presentation/Abstractions.cs` (`IReceiptOutput`)
- Modify: `tillpos/src/TillPOS.App/ReceiptOutput.cs`
- Modify: `tillpos/tests/TillPOS.Tests/Presentation/Fakes.cs` (`FakeOutput`)
- Test: `tillpos/tests/TillPOS.Tests/Printing/DeliveryPrintTests.cs`

**Interfaces:**
- Consumes: `Delivery`, `DeliverySummary`, `Receipt.IsDelivery` (Task 1).
- Produces: `ReceiptRenderer.DeliverySlipLayout(Delivery d, ReceiptHeader h, PaperWidth paper, bool copy = false)`, `ReceiptRenderer.DeliverySlipTextLines(...)`, `ReceiptRenderer.DeliverySlipEscPosBytes(...)` (never a drawer kick); `ShiftReportRenderer.Layout/TextLines/EscPosBytes(..., decimal? firstCountDifference = null, DeliverySummary? deliveries = null)`; `IReceiptOutput.PrintDelivery(Delivery delivery, bool copy)`; `IReceiptOutput.PrintShiftReport(ShiftOpening, ShiftClosing, string cashierName, string? approvedBy, decimal? firstCountDifference, DeliverySummary? deliveries = null)`; `FakeOutput.Deliveries` (list of `(Delivery Delivery, bool Copy)`), `FakeOutput.ShiftReports` gains a 6th tuple item `DeliverySummary? Deliveries`.

- [ ] **Step 1: Write the failing tests** — `tillpos/tests/TillPOS.Tests/Printing/DeliveryPrintTests.cs`

```csharp
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class DeliveryPrintTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Ajman, UAE", "100000000000003", "Till 2", "Thank you");
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 10, 0, TimeSpan.FromHours(4));

    private static Receipt Bill(string id = "TILL2-20261008151000-000042") => new(id, ReceiptKind.Sale, null, "S1", "simran", At,
        [new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, 1m, M("11.429"), M("11.429"), M("11.429"), null, null, false, null)],
        M("11.429"), M("10.885"), M("0.544"), M("11.429"), false, 0m, 0m, [], 0m, 0m, null) { IsDelivery = true, CashierName = "Simran" };

    private static Delivery Slip() => new("TILL2-20261008151000-000042", DeliveryStatus.Open, At, "S1", "simran", "Simran", "Counter 2",
        Bill(), M("11.50"), M("11.43"));

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void The_slip_says_not_paid_shows_both_amounts_to_collect_and_has_a_barcode_but_no_qr(PaperWidth paper)
    {
        var layout = ReceiptRenderer.DeliverySlipLayout(Slip(), Header, paper);
        var text = ReceiptRenderer.DeliverySlipTextLines(Slip(), Header, paper);

        Assert.Equal("DELIVERY INVOICE", string.Join(" ", layout.Where(l => l.Style == LineStyle.Title).Select(l => l.Text)));
        Assert.Contains(text, l => l.Contains("NOT PAID"));
        Assert.Contains(text, l => l.Contains("AMOUNT TO COLLECT"));
        Assert.Contains(text, l => l.TrimStart().StartsWith("Cash") && l.EndsWith(" 11.50"));
        Assert.Contains(text, l => l.TrimStart().StartsWith("Card") && l.EndsWith(" 11.43"));
        Assert.Equal("TILL2-20261008151000-000042", Assert.Single(layout, l => l.Style == LineStyle.Barcode).Text);
        Assert.DoesNotContain(layout, l => l.Style == LineStyle.Qr);
        Assert.DoesNotContain(text, l => l.Contains("TAX INVOICE") || l.StartsWith("Change"));
        Assert.All(layout.Where(l => l.Style is LineStyle.Normal or LineStyle.Bold), l => Assert.True(l.Text.Length <= (int)paper, l.Text));
    }

    [Fact]
    public void A_reprinted_slip_is_marked_copy_and_never_kicks_the_drawer()
    {
        Assert.Contains("*** COPY ***", ReceiptRenderer.DeliverySlipTextLines(Slip(), Header, PaperWidth.Mm80, copy: true).Select(l => l.Trim()));
        var bytes = ReceiptRenderer.DeliverySlipEscPosBytes(Slip(), Header, PaperWidth.Mm80);
        Assert.False(EscPosTests.Contains(bytes, [0x1B, 0x70, 0x00, 0x19, 0xFA]));
    }

    [Fact]
    public void A_paid_delivery_prints_as_a_tax_invoice_marked_delivery_paid()
    {
        var paid = Bill() with { Payments = [new ReceiptPayment("Cash Counter 2", M("11.50"))], UsesErpRoundedTotal = true, RoundedTotal = M("11.50") };
        var text = ReceiptRenderer.TextLines(paid, Header, PaperWidth.Mm80).Select(l => l.Trim()).ToList();

        var title = text.IndexOf("TAX INVOICE");
        var marker = text.IndexOf("DELIVERY - PAID");
        Assert.True(title >= 0 && marker > title && marker < text.FindIndex(l => l.StartsWith("Invoice No")), string.Join("\n", text));
        Assert.DoesNotContain(text, l => l.Contains("NOT PAID"));
        Assert.DoesNotContain("DELIVERY - PAID", ReceiptRenderer.TextLines(Bill() with { IsDelivery = false }, Header, PaperWidth.Mm80));
    }

    [Fact]
    public void The_z_report_lists_deliveries_paid_and_still_out()
    {
        var opening = new ShiftOpening("TILL2-SHIFT-1", "simran", "", At.AddHours(-8), [new ReceiptPayment("Cash Counter 2", 200m)]);
        var closing = new ShiftClosing("TILL2-SHIFT-1", At, [new ShiftModeSummary("Cash Counter 2", 200m, 211.5m, 211.5m, 0m)], 3, 0,
            M("45.72"), M("43.54"), M("2.18"));
        var summary = new DeliverySummary(2, M("34.29"), [Slip(), Slip() with { ClientId = "TILL2-20261008162210-000043" }]);

        var text = ShiftReportRenderer.TextLines(opening, closing, Header, "Simran", "Sup", PaperWidth.Mm80, null, summary);

        Assert.Contains(text, l => l.StartsWith("Deliveries paid (2)") && l.EndsWith(" 34.29"));
        Assert.Contains(text, l => l.StartsWith("DELIVERIES STILL OUT"));
        Assert.Contains(text, l => l.StartsWith("TILL2-20261008151000-000042") && l.EndsWith(" 11.43"));
        Assert.Contains(text, l => l.StartsWith("Total to collect") && l.EndsWith(" 22.86"));
        Assert.DoesNotContain(ShiftReportRenderer.TextLines(opening, closing, Header, "Simran", "Sup", PaperWidth.Mm80), l => l.Contains("DELIVER"));
    }
}
```

(`ShiftClosing(ShiftClientId, ClosedAt, Modes, Sales, Returns, GrandTotal, NetTotal, TotalTaxes)` — `ShiftCalculator.cs:23`.) Amounts still out are each delivery's `CardToCollect` (the exact amount; cash may round it) and the total is their sum.

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~DeliveryPrintTests"`. Expected: build errors.

- [ ] **Step 3: Implement**

`ReceiptRenderer.cs`:
1. Rename the body of `Layout(Receipt r, ReceiptHeader h, PaperWidth paper, bool copy = false)` into `private static IReadOnlyList<PrintLine> Build(Receipt r, ReceiptHeader h, PaperWidth paper, bool copy, Delivery? slip)`; `Layout` becomes `=> Build(r, h, paper, copy, null)`.
2. In `Build`:
   - title text: `slip is not null ? "DELIVERY INVOICE" : isReturn ? "TAX CREDIT NOTE" : "TAX INVOICE"`.
   - right after the second `Rule('=')`: `if (slip is not null) Centered("*** NOT PAID ***", LineStyle.Bold); else if (r.IsDelivery && !isReturn) Centered("DELIVERY - PAID", LineStyle.Bold);` (then the existing `if (copy) Centered(CopyMark, …)`).
   - from the rounding block to the end of the payment lines: when `slip is not null`, instead print
     ```csharp
     Rule('-');
     Add("AMOUNT TO COLLECT", LineStyle.Bold);
     Add(Pair("  Cash (rounded)", Money(slip.CashToCollect), w));
     Add(Pair("  Card (exact)", Money(slip.CardToCollect), w));
     Rule('-');
     ```
     and skip Rounding / AMOUNT DUE / payments / Change (keep "You saved").
   - QR block: skip when `slip is not null`.
3. Add:
```csharp
    /// <summary>The NOT PAID delivery slip (no QR: it is not a paid tax invoice yet).</summary>
    public static IReadOnlyList<PrintLine> DeliverySlipLayout(Delivery d, ReceiptHeader h, PaperWidth paper, bool copy = false) =>
        Build(d.Bill, h, paper, copy, d);

    public static IReadOnlyList<string> DeliverySlipTextLines(Delivery d, ReceiptHeader h, PaperWidth paper, bool copy = false) =>
        PlainText(DeliverySlipLayout(d, h, paper, copy), paper);

    public static byte[] DeliverySlipEscPosBytes(Delivery d, ReceiptHeader h, PaperWidth paper, bool copy = false) =>
        StyledBytes(DeliverySlipLayout(d, h, paper, copy), openDrawer: false, paper);
```

`ShiftReportRenderer.cs` — add `DeliverySummary? deliveries = null` as the last parameter of `Layout`, `TextLines`, `EscPosBytes` (pass it through), and after the mode table's `Rule();`:

```csharp
        if (deliveries is { PaidCount: > 0 } paid)
            Add(Pair($"Deliveries paid ({paid.PaidCount.ToString(CultureInfo.InvariantCulture)})", Money(paid.PaidTotal), w));
        if (deliveries is { StillOut.Count: > 0 } outstanding)
        {
            foreach (var part in Wrap("DELIVERIES STILL OUT (not in the drawer): " +
                outstanding.StillOut.Count.ToString(CultureInfo.InvariantCulture), w)) Add(part, LineStyle.Bold);
            foreach (var d in outstanding.StillOut)
            {
                var amount = Money(d.CardToCollect);
                if (d.ClientId.Length + 1 + amount.Length <= w) Add(Pair(d.ClientId, amount, w));
                else { Add(Fit(d.ClientId, w)); Add(amount.PadLeft(w)); }
            }
            Add(Pair("Total to collect", Money(outstanding.StillOut.Sum(d => d.CardToCollect)), w));
            Rule();
        }
```

(`using TillPOS.Core.Sales;` at the top.)

`Abstractions.cs` — `IReceiptOutput`:

```csharp
    void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy, decimal? firstCountDifference,
        DeliverySummary? deliveries = null);

    /// <summary>Prints a delivery's NOT PAID slip (<paramref name="copy"/>: a reprint); never opens the drawer; throws if the printer fails.</summary>
    void PrintDelivery(Delivery delivery, bool copy);
```

`ReceiptOutput.cs` — pass `deliveries` through in `PrintShiftReport`, and add:

```csharp
    /// <summary>The delivery slip; with no printer it is written to "DELIVERY-{id}.txt" ("-COPY" for a reprint).</summary>
    public void PrintDelivery(Delivery delivery, bool copy)
    {
        var header = Header();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            File.WriteAllLines(Path.Combine(ReceiptsFolder(), $"DELIVERY-{delivery.ClientId}{(copy ? "-COPY" : "")}.txt"),
                ReceiptRenderer.DeliverySlipTextLines(delivery, header, settings.PaperWidth, copy));
            return;
        }
        RawPrinter.Send(settings.PrinterName, ReceiptRenderer.DeliverySlipEscPosBytes(delivery, header, settings.PaperWidth, copy));
    }
```

`Fakes.cs` — `FakeOutput`: add the 6th tuple item `DeliverySummary? Deliveries` to `ShiftReports` (record `deliveries`), and

```csharp
    public List<(Delivery Delivery, bool Copy)> Deliveries { get; } = [];

    public void PrintDelivery(Delivery delivery, bool copy)
    {
        OnPrint?.Invoke();
        if (Fail) throw new InvalidOperationException("Printer offline");
        Deliveries.Add((delivery, copy));
    }
```

Existing tests deconstructing 5-item `ShiftReports` tuples (e.g. `CounterSelectionTests.cs:319` `var (opening, closing, _, _, _)`) get one more `_`.

- [ ] **Step 4: Run all tests** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tillpos/src/TillPOS.Printing tillpos/src/TillPOS.Presentation/Abstractions.cs tillpos/src/TillPOS.App/ReceiptOutput.cs tillpos/tests
git commit -m "feat(print): delivery slip (NOT PAID, amounts to collect), DELIVERY - PAID marker, Z report deliveries"
```

---

### Task 4: Payment screen pays an `IPaymentHost` (sale or delivery), exact-cash prefill

**Files:**
- Create: `tillpos/src/TillPOS.Presentation/IPaymentHost.cs`
- Modify: `tillpos/src/TillPOS.Presentation/PaymentViewModel.cs`
- Modify: `tillpos/src/TillPOS.Presentation/SaleViewModel.cs` (implements `IPaymentHost`; `LinesOf` helper)
- Test: `tillpos/tests/TillPOS.Tests/Presentation/PaymentHostTests.cs`

**Interfaces:**
- Produces:
```csharp
public interface IPaymentHost
{
    Cart Cart { get; }
    IReadOnlyList<SaleLine> Lines { get; }
    string ItemCount { get; }
    string Discount { get; }
    string Vat { get; }
    MoneySettings Money { get; }
    bool PrefillExactCash { get; }
    Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift);
    void Completed(Receipt receipt, string? printError);
    void BackFromPayment();
}
```
  `PaymentViewModel(TillContext ctx, SessionState session, IPaymentHost host, TenderKind initialKind)`; `SaleViewModel.LinesOf(Cart cart)` → `IReadOnlyList<SaleLine>` (public static; the test project has no InternalsVisibleTo).

- [ ] **Step 1: Write the failing tests** — `tillpos/tests/TillPOS.Tests/Presentation/PaymentHostTests.cs`

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class PaymentHostTests : IDisposable
{
    private readonly PresentationFixture f = new();

    public PaymentHostTests() => f.LogInWithOpenShift();

    public void Dispose() => f.Dispose();

    private sealed class Host(Cart cart, bool prefill) : IPaymentHost
    {
        public Cart Cart => cart;
        public IReadOnlyList<SaleLine> Lines => SaleViewModel.LinesOf(cart);
        public string ItemCount => cart.Lines.Count.ToString();
        public string Discount => "0.00";
        public string Vat => "0.00";
        public MoneySettings Money => cart.Context.Money;
        public bool PrefillExactCash => prefill;
        public List<PaymentPlan> Recorded { get; } = [];
        public Receipt? Done { get; private set; }
        public int Backs { get; private set; }

        public Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift)
        {
            Recorded.Add(plan);
            return new Receipt("X-1", ReceiptKind.Sale, null, shift.ClientId, cashier.Id, DateTimeOffset.Now, [], 0m, 0m, 0m, plan.GrandTotal,
                false, 0m, 0m, [], plan.Change, 0m, null);
        }

        public void Completed(Receipt receipt, string? printError) => Done = receipt;
        public void BackFromPayment() => Backs++;
    }

    private Cart Milk()
    {
        var cart = new Cart(f.Ctx.SaleContextFor(f.Session));
        cart.AddBarcode("111");                                          // 6.79
        return cart;
    }

    [Fact]
    public void Exact_cash_is_prefilled_so_no_change_is_given()
    {
        var vm = new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: true), TenderKind.Cash);

        Assert.Equal("6.75", vm.Cash.Text);
        Assert.Equal("0.00", vm.Change);
        Assert.True(vm.Plan!.IsComplete);
    }

    [Fact]
    public void Switching_to_cash_later_prefills_too_but_never_overwrites_typed_cash()
    {
        var vm = new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: true), TenderKind.Card);
        vm.Kind = TenderKind.Cash;
        Assert.Equal("6.75", vm.Cash.Text);

        vm.Cash.Set(10m);
        vm.Kind = TenderKind.Card;
        vm.Kind = TenderKind.Cash;
        Assert.Equal("10", vm.Cash.Text);
    }

    [Fact]
    public void Without_prefill_the_cash_box_starts_empty() =>
        Assert.Equal("", new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: false), TenderKind.Cash).Cash.Text);

    [Fact]
    public void Completing_records_through_the_host_prints_and_hands_back()
    {
        var host = new Host(Milk(), prefill: true);
        var vm = new PaymentViewModel(f.Ctx, f.Session, host, TenderKind.Cash);

        vm.CompleteCommand.Execute(null);

        Assert.Single(host.Recorded);
        Assert.Equal("X-1", host.Done!.ClientId);
        Assert.Single(f.Output.Printed);
    }

    [Fact]
    public void Back_goes_to_the_host() 
    {
        var host = new Host(Milk(), prefill: false);
        new PaymentViewModel(f.Ctx, f.Session, host, TenderKind.Cash).BackCommand.Execute(null);
        Assert.Equal(1, host.Backs);
    }
}
```

(`NumericEntry.Text` for 6.75 — if `NumericEntry.Set` formats differently, assert `vm.Cash.Value == M("6.75")` instead of the text.)

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~PaymentHostTests"`. Expected: build errors.

- [ ] **Step 3: Implement**

Create `IPaymentHost.cs`:

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>What the payment screen pays: the sale screen's bill, or a delivery being paid.</summary>
public interface IPaymentHost
{
    Cart Cart { get; }
    IReadOnlyList<SaleLine> Lines { get; }
    string ItemCount { get; }
    string Discount { get; }
    string Vat { get; }
    MoneySettings Money { get; }
    /// <summary>Cash starts with the exact amount due in the cash box, so no change is given (deliveries).</summary>
    bool PrefillExactCash { get; }
    /// <summary>Saves the paid bill and empties the cart; throws when it cannot (nothing is then saved).</summary>
    Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift);
    /// <summary>After the bill is saved and printed (or the print failed).</summary>
    void Completed(Receipt receipt, string? printError);
    /// <summary>Back (Esc) from the payment screen.</summary>
    void BackFromPayment();
}
```

`PaymentViewModel.cs`:
- field `private readonly IPaymentHost host;` replaces `sale`; constructor parameter `IPaymentHost host` (was `SaleViewModel sale`); remove the `recorder` field and its construction.
- `calculator = new PaymentCalculator(host.Money)`, `grandTotal = host.Cart.Totals().GrandTotal`, `Lines = host.Lines.ToList()`, `LineCount = host.ItemCount`, `Discount = host.Discount`, `Vat = host.Vat`.
- `BackCommand = new RelayCommand(host.BackFromPayment);`
- after `kind = initialKind; Recalculate();` add `PrefillCash();`; in the `Kind` setter, after `Recalculate();` add `PrefillCash();`
```csharp
    /// <summary>Deliveries: the cash box gets the exact amount due when Cash is chosen and nothing was typed yet.</summary>
    private void PrefillCash()
    {
        if (!host.PrefillExactCash || kind != TenderKind.Cash || Cash.Value is not null) return;
        Cash.Set(calculator.Plan(grandTotal, Tender.Cash(0m)).AmountDue);
    }
```
- in `Complete()`, replace `receipt = recorder.CompleteSale(sale.Cart, p, cashier.Id, shift.ClientId, cashier.User, cashier.Name);` with `receipt = host.Record(p, cashier, shift);`; delete the `sale.ClearAutosave()` try/catch block (it moves into `SaleViewModel.Record`); replace `sale.SaleCompleted(receipt, printError);` with `host.Completed(receipt, printError);`.

`SaleViewModel.cs`:
- class declaration: `public sealed class SaleViewModel : ObservableObject, IPaymentHost`
- `IReadOnlyList<SaleLine> IPaymentHost.Lines => Lines;` (the `ObservableCollection` is an `IReadOnlyList`)
- add:
```csharp
    public bool PrefillExactCash => false;

    /// <summary>Saves the sale (next bill number) and clears the cart and its autosave.</summary>
    public Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift)
    {
        var counter = ctx.CounterOf(session);
        var receipt = new SaleRecorder(ctx.Receipts, ctx.TillNumber, counter.Modes, () => ctx.Clock.Now, counter.DisplayName)
            .CompleteSale(Cart, plan, cashier.Id, shift.ClientId, cashier.User, cashier.Name);
        try
        {
            ClearAutosave();
        }
        catch (Exception)
        {
            // The bill is saved and the cart is empty; the next change on the sale screen rewrites the autosave.
        }
        return receipt;
    }

    public void Completed(Receipt receipt, string? printError) => SaleCompleted(receipt, printError);

    public void BackFromPayment() => ctx.Navigator.Show(this);

    /// <summary>A cart's lines as the screens show them (also the payment screen's list).</summary>
    public static IReadOnlyList<SaleLine> LinesOf(Cart cart)
    {
        var totals = cart.Totals();
        return cart.Lines.Select((l, i) => new SaleLine(l.Id, i + 1, l.Item.ItemName, l.Barcode,
            l.FromScaleLabel || l.Qty != decimal.Truncate(l.Qty)
                ? $"{l.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {l.Uom}"
                : l.Qty.ToString("0", CultureInfo.InvariantCulture),
            Format.Money(l.Rate), l.Rule?.Label ?? "", Format.Money(totals.Lines[i].Amount), l.FromScaleLabel)).ToList();
    }
```
- `Refresh()`'s loop becomes `foreach (var line in LinesOf(Cart)) Lines.Add(line);` (keep the rest of `Refresh`).
- (`using TillPOS.Core.Shifts;` for `ShiftOpening`.)

- [ ] **Step 4: Run all tests** — Expected: PASS (existing `PaymentViewModelTests` construct `new PaymentViewModel(f.Ctx, f.Session, sale, kind)`; `sale` is an `IPaymentHost`).

- [ ] **Step 5: Commit**

```bash
git add tillpos/src/TillPOS.Presentation tillpos/tests/TillPOS.Tests/Presentation/PaymentHostTests.cs
git commit -m "refactor(payment): the payment screen pays an IPaymentHost; exact-cash prefill option"
```

---

### Task 5: Delivery flow — make, list, change, cancel, pay; Z report summary

**Files:**
- Create: `tillpos/src/TillPOS.Presentation/DeliveriesViewModel.cs`
- Modify: `tillpos/src/TillPOS.Presentation/Abstractions.cs` (`TillContext.Deliveries`)
- Modify: `tillpos/src/TillPOS.Presentation/SaleViewModel.cs` (Delivery F9, Deliveries, slip scan, `DeliveryCount`)
- Modify: `tillpos/src/TillPOS.Presentation/CloseShiftViewModel.cs` (delivery summary on the Z report)
- Modify: `tillpos/tests/TillPOS.Tests/Presentation/PresentationFixture.cs` (`Deliveries = new DeliveryStore(db, receipts)`)
- Test: `tillpos/tests/TillPOS.Tests/Presentation/DeliveryFlowTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4 (`Deliveries.Make/Rebill`, `DeliveryStore`, `IReceiptOutput.PrintDelivery`, `IPaymentHost`, `SaleRecorder.Build`, `Cart.RestoreFixed`, `ApprovalAction.DeliveryChange/DeliveryCancel`).
- Produces: `TillContext.Deliveries` (`DeliveryStore`, init, required); `SaleViewModel.MakeDeliveryCommand`, `OpenDeliveriesCommand`, `DeliveryCount`, `MakeDelivery()`, `OpenDeliveries(string? select = null)`, `RefreshDeliveryCount()`; `DeliveriesViewModel` (see code); `record DeliveryRow(string ClientId, string Made, string Cashier, string Amount, string Age, bool Old)`.

- [ ] **Step 1: Wire the fixture** — in `PresentationFixture` keep the `ReceiptStore` in a local and set the new property:

```csharp
        var receipts = new ReceiptStore(db);
        Ctx = new TillContext(
            ...,
            new ShiftStore(db), receipts, new ApprovalStore(db), new CatalogStore(db),
            Clock, Output, Navigator, Dialogs, ShowReceiptPreview: true, new HeldCartStore(db))
        {
            RemoteReceipts = new RemoteReceiptStore(db),
            RemoteReturnsCheck = RemoteCheck,
            Deliveries = new DeliveryStore(db, receipts),
        };
```

- [ ] **Step 2: Write the failing tests** — `tillpos/tests/TillPOS.Tests/Presentation/DeliveryFlowTests.cs`

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class DeliveryFlowTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object login = new();
    private readonly SaleViewModel sale;

    public DeliveryFlowTests()
    {
        f.LogInWithOpenShift();
        sale = new SaleViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session),
            (s, kind) => new PaymentViewModel(f.Ctx, f.Session, s, kind), () => login);
        f.Navigator.Show(sale);
    }

    public void Dispose() => f.Dispose();

    /// <summary>Two milks (13.58) made into a delivery; returns it.</summary>
    private Delivery Make()
    {
        sale.Scan("111");
        sale.Scan("111");
        f.Dialogs.ConfirmAnswers.Enqueue(true);
        sale.MakeDelivery();
        return Assert.Single(f.Ctx.Deliveries.Open());
    }

    private DeliveriesViewModel Open(string? id = null)
    {
        sale.OpenDeliveries(id);
        return Assert.IsType<DeliveriesViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Making_a_delivery_saves_it_prints_the_slip_and_clears_the_bill()
    {
        var d = Make();

        Assert.StartsWith("TILL2-", d.ClientId);
        Assert.Equal(M("13.58"), d.Bill.GrandTotal);
        Assert.Equal(M("13.50"), d.CashToCollect);
        Assert.Equal(M("13.58"), d.CardToCollect);
        var (printed, copy) = Assert.Single(f.Output.Deliveries);
        Assert.Equal(d.ClientId, printed.ClientId);
        Assert.False(copy);
        Assert.Empty(f.Output.Printed);                            // no receipt, no drawer
        Assert.Empty(sale.Lines);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
        Assert.Equal(1, sale.DeliveryCount);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));              // nothing to upload
    }

    [Fact]
    public void Declining_the_confirmation_keeps_the_bill()
    {
        sale.Scan("111");
        f.Dialogs.ConfirmAnswers.Enqueue(false);
        sale.MakeDelivery();
        Assert.Empty(f.Ctx.Deliveries.Open());
        Assert.Single(sale.Lines);
    }

    [Fact]
    public void An_empty_bill_cannot_become_a_delivery()
    {
        sale.MakeDelivery();
        Assert.True(sale.MessageIsError);
        Assert.Empty(f.Dialogs.Confirms);
    }

    [Fact]
    public void A_printer_failure_still_saves_the_delivery()
    {
        f.Output.Fail = true;
        var d = Make();
        Assert.Contains("Reprint it from Deliveries", sale.Message);
        Assert.Equal(DeliveryStatus.Open, d.Status);
    }

    [Fact]
    public void The_list_shows_open_deliveries_and_selects_the_first()
    {
        var d = Make();
        var vm = Open();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(d.ClientId, row.ClientId);
        Assert.Equal("13.58", row.Amount);
        Assert.Equal(d.ClientId, vm.SelectedId);
        Assert.Equal(2m, Assert.Single(vm.Cart.Lines).Qty);
    }

    [Fact]
    public void Paying_cash_is_exact_saves_a_delivery_sale_in_this_shift_and_closes_it()
    {
        var d = Make();
        var vm = Open();
        f.Clock.Now = f.Clock.Now.AddHours(1);

        vm.PayCashCommand.Execute(null);
        var pay = Assert.IsType<PaymentViewModel>(f.Navigator.Current);
        Assert.Equal("0.00", pay.Change);
        pay.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal(d.ClientId, receipt.ClientId);
        Assert.True(receipt.IsDelivery);
        Assert.Equal(f.Clock.Now, receipt.CreatedAt);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", M("13.50")) }, receipt.Payments);
        Assert.Equal(DeliveryStatus.Paid, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
        Assert.Same(sale, f.Navigator.Current);
        Assert.True(Assert.Single(f.Output.Printed).OpenDrawer);
        Assert.Equal(0, sale.DeliveryCount);
    }

    [Fact]
    public void Paying_by_card_charges_the_total_to_two_decimals()
    {
        Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);

        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("13.58")) }, Assert.Single(f.Ctx.Receipts.ListPending(10)).Payments);
    }

    [Fact]
    public void Paying_uses_the_delivery_prices_after_a_price_change()
    {
        Make();
        f.Catalog.Prices.Clear();
        f.Catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("20.00"), null, null));

        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);

        Assert.Equal(M("13.58"), Assert.Single(f.Ctx.Receipts.ListPending(10)).GrandTotal);
    }

    [Fact]
    public void Paying_in_a_shift_at_another_counter_uses_that_counters_modes()
    {
        // Its own fixture: the shared one always opens the same shift id. The delivery was made at Counter 2; the shift now
        // open is at Test Counter ("Cash Counter 1").
        using var g = new PresentationFixture();
        g.LogInWithOpenShift(PresentationFixture.TestCounter);
        var made = new Cart(g.Ctx.NewSaleContextFor("Al Ain Counter 2"));
        made.AddBarcode("111");
        var d = Deliveries.Make(made, "TILL2-20261007090000-000077", g.Clock.Now.AddHours(-1), "TILL2-SHIFT-OLD", "simran", "Simran", "Counter 2");
        g.Ctx.Deliveries.Add(d);
        var here = new SaleViewModel(g.Ctx, g.Session, new SupervisorGate(g.Ctx, g.Session),
            (s, kind) => new PaymentViewModel(g.Ctx, g.Session, s, kind), () => login);

        here.OpenDeliveries(d.ClientId);
        Assert.IsType<DeliveriesViewModel>(g.Navigator.Current).PayCashCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(g.Navigator.Current).CompleteCommand.Execute(null);

        var receipt = g.Ctx.Receipts.Get(d.ClientId)!;
        Assert.Equal("Cash Counter 1", Assert.Single(receipt.Payments).ModeOfPayment);
        Assert.Equal("Test Counter", receipt.PosProfile);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
    }

    [Fact]
    public void Back_from_the_payment_screen_returns_to_the_list_and_the_delivery_stays_open()
    {
        var d = Make();
        var vm = Open();
        vm.PayCashCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).BackCommand.Execute(null);

        Assert.Same(vm, f.Navigator.Current);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
    }

    [Fact]
    public async Task Reducing_a_line_needs_a_supervisor_and_saves_the_new_amounts()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        var changed = f.Ctx.Deliveries.Get(d.ClientId)!;
        Assert.Equal(M("6.79"), changed.Bill.GrandTotal);
        Assert.Equal(M("6.75"), changed.CashToCollect);
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.DeliveryChange);
    }

    [Fact]
    public async Task A_refused_pin_changes_nothing()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("1111");                              // a cashier PIN

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
        Assert.Single(vm.Cart.Lines);
    }

    [Fact]
    public async Task Removing_the_last_line_cancels_the_delivery()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(DeliveryStatus.Cancelled, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
        Assert.Empty(vm.Rows);
    }

    [Theory]
    [InlineData("Refused")]
    [InlineData("Not delivered")]
    [InlineData("Other")]
    public async Task Cancel_needs_a_supervisor_and_records_the_reason(string reason)
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CancelAsync(reason);

        var cancelled = f.Ctx.Deliveries.Get(d.ClientId)!;
        Assert.Equal((DeliveryStatus.Cancelled, reason, "sup"), (cancelled.Status, cancelled.Reason, cancelled.ClosedBy));
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.DeliveryCancel);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
    }

    [Fact]
    public void Reprint_prints_the_slip_marked_copy()
    {
        Make();
        Open().ReprintCommand.Execute(null);
        Assert.True(f.Output.Deliveries[^1].Copy);
    }

    [Fact]
    public void Scanning_a_delivery_slip_on_an_empty_bill_opens_it()
    {
        var d = Make();
        sale.Scan(d.ClientId);
        Assert.Equal(d.ClientId, Assert.IsType<DeliveriesViewModel>(f.Navigator.Current).SelectedId);
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public void Scanning_a_slip_with_items_on_the_bill_asks_to_finish_first()
    {
        var d = Make();
        sale.Scan("111");
        sale.Scan(d.ClientId);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Equal("Finish, hold or void the current bill first, then scan the delivery slip", sale.Message);
        Assert.Single(sale.Lines);
    }

    [Fact]
    public void Scanning_a_paid_delivery_slip_says_so()
    {
        var d = Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);
        f.Navigator.Show(sale);

        sale.Scan(d.ClientId);

        Assert.Equal($"Delivery {d.ClientId} is already paid", sale.Message);
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public async Task The_z_report_lists_deliveries_paid_and_still_out()
    {
        var paid = Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);
        var stillOut = Make();

        sale.CloseShift();
        var close = Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
        close.UseTotalInstead.Text = "200";
        close.CardTotal.Text = "13.58";
        f.Dialogs.Pins.Enqueue("9999");
        await close.CloseAsync();

        var summary = Assert.Single(f.Output.ShiftReports).Deliveries!;
        Assert.Equal((1, M("13.58")), (summary.PaidCount, summary.PaidTotal));
        Assert.Equal(stillOut.ClientId, Assert.Single(summary.StillOut).ClientId);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(stillOut.ClientId)!.Status);   // stays for the next shift
    }

    [Fact]
    public void The_shift_can_close_with_deliveries_out()
    {
        Make();
        sale.CloseShift();
        Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
    }
}
```

- [ ] **Step 3: Run to verify they fail** — `--filter "FullyQualifiedName~DeliveryFlowTests"`. Expected: build errors.

- [ ] **Step 4: Implement**

`Abstractions.cs` — `TillContext`, after `TaxTemplates`:

```csharp
    /// <summary>Delivery bills kept on this till until paid.</summary>
    public required DeliveryStore Deliveries { get; init; }
```

`SaleViewModel.cs`:
- fields/properties: `private int deliveryCount;` `public int DeliveryCount { get => deliveryCount; private set => SetProperty(ref deliveryCount, value); }`
- commands (constructor): `MakeDeliveryCommand = new RelayCommand(MakeDelivery); OpenDeliveriesCommand = new RelayCommand(() => OpenDeliveries());` and `RefreshDeliveryCount();` after `RefreshHeldCount();`; properties `public RelayCommand MakeDeliveryCommand { get; }`, `public RelayCommand OpenDeliveriesCommand { get; }`.
- at the start of `Scan(string code)`:
```csharp
        if (ClientIds.IsTillId(code) && OpenDeliverySlip(code)) return;
```
- add:
```csharp
    /// <summary>F9: the bill as a delivery (not paid): saved on the till only, its slip printed, the screen cleared.</summary>
    public void MakeDelivery()
    {
        if (Cart.Lines.Count == 0) { Error("Scan the delivery's items first."); return; }
        if (session.Shift is not { } shift || session.Cashier is not { } cashier) { Error("No open shift — log in again."); return; }
        if (!ctx.Dialogs.Confirm("Delivery", "Make this bill a delivery? It prints a NOT PAID delivery invoice; take the payment " +
                "from Deliveries when the driver is back.")) return;

        Delivery delivery;
        try
        {
            var now = ctx.Clock.Now;
            delivery = Deliveries.Make(Cart, ClientIds.Receipt(ctx.TillNumber, now, ctx.Receipts.NextSequence()), now, shift.ClientId,
                cashier.Id, cashier.Name, ctx.CounterOf(session).DisplayName);
            ctx.Deliveries.Add(delivery);
        }
        catch (Exception ex)
        {
            Error($"Could not save the delivery: {ex.Message}");
            return;
        }
        Cart.Clear();
        Changed();
        RefreshDeliveryCount();
        try
        {
            ctx.Output.PrintDelivery(delivery, copy: false);
            Info($"Delivery {delivery.ClientId} saved: collect {Format.Money(delivery.CashToCollect)} cash or {Format.Money(delivery.CardToCollect)} card");
        }
        catch (Exception ex)
        {
            Error($"Delivery {delivery.ClientId} saved, but the slip did not print ({ex.Message}). Reprint it from Deliveries.");
        }
    }

    /// <summary>The Deliveries screen (on an empty bill); <paramref name="select"/> opens that delivery.</summary>
    public void OpenDeliveries(string? select = null)
    {
        if (Cart.Lines.Count > 0) { Error("Finish, hold or void the current bill first"); return; }
        ctx.Navigator.Show(new DeliveriesViewModel(ctx, session, gate, this, select));
    }

    /// <summary>A scanned delivery slip: opens it (true), or explains why not (true); false = not a delivery number.</summary>
    private bool OpenDeliverySlip(string code)
    {
        Delivery? d;
        try
        {
            d = ctx.Deliveries.Get(code);
        }
        catch (Exception)
        {
            return false;
        }
        if (d is null) return false;
        if (d.Status != DeliveryStatus.Open)
        {
            Error($"Delivery {code} is already {(d.Status == DeliveryStatus.Paid ? "paid" : "cancelled")}");
            return true;
        }
        if (Cart.Lines.Count > 0) { Error("Finish, hold or void the current bill first, then scan the delivery slip"); return true; }
        OpenDeliveries(code);
        return true;
    }

    public void RefreshDeliveryCount()
    {
        try
        {
            DeliveryCount = ctx.Deliveries.Open().Count;
        }
        catch (Exception)
        {
            // Keep the last count; the badge is only a hint.
        }
    }
```
- in `SaleCompleted`, after `Refresh();` add `RefreshDeliveryCount();`.

Create `DeliveriesViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>One open delivery in the list. <see cref="Old"/>: out for more than 7 days (shown in red).</summary>
public sealed record DeliveryRow(string ClientId, string Made, string Cashier, string Amount, string Age, bool Old);

/// <summary>The Deliveries screen: the open deliveries (oldest first) and the selected one's lines. Pay opens the normal payment
/// screen (exact cash prefilled); the paid bill keeps the delivery's number, is a sale of the current shift and counter, and is
/// saved together with the delivery's status. Removing / reducing lines and cancelling need a supervisor and are logged.</summary>
public sealed class DeliveriesViewModel : ObservableObject, IPaymentHost
{
    public const int OldAfterDays = 7;
    public static readonly IReadOnlyList<string> CancelReasons = ["Refused", "Not delivered", "Other"];

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly SaleViewModel sale;
    private Delivery? selected;
    private string? selectedId;
    private string message = "";
    private bool messageIsError;

    public DeliveriesViewModel(TillContext ctx, SessionState session, SupervisorGate gate, SaleViewModel sale, string? select = null)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.sale = sale;
        Cart = new Cart(ctx.SaleContextFor(session));
        PayCashCommand = new RelayCommand(() => Pay(TenderKind.Cash));
        PayCardCommand = new RelayCommand(() => Pay(TenderKind.Card));
        PaySplitCommand = new RelayCommand(() => Pay(TenderKind.Split));
        ReduceCommand = new AsyncRelayCommand<Guid>(ReduceLineAsync);
        RemoveCommand = new AsyncRelayCommand<Guid>(RemoveLineAsync);
        CancelCommand = new AsyncRelayCommand<string>(r => CancelAsync(r ?? "Other"));
        ReprintCommand = new RelayCommand(Reprint);
        BackCommand = new RelayCommand(Back);
        Reload(select);
    }

    public ObservableCollection<DeliveryRow> Rows { get; } = [];
    public ObservableCollection<SaleLine> SelectedLines { get; } = [];
    public Cart Cart { get; private set; }
    public MoneySettings Money => Cart.Context.Money;
    public bool PrefillExactCash => true;
    public bool HasSelection => selected is not null;
    public string CashToCollect => selected is null ? "" : Format.Money(selected.CashToCollect);
    public string CardToCollect => selected is null ? "" : Format.Money(selected.CardToCollect);
    public string ItemCount => Cart.Lines.Count.ToString(CultureInfo.InvariantCulture);
    public string Discount => Format.Money(Cart.DiscountSaved());
    public string Vat => Cart.Lines.Count == 0 ? "0.00" : Format.Money(Cart.Totals().TotalTaxes);
    IReadOnlyList<SaleLine> IPaymentHost.Lines => SelectedLines;
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    /// <summary>The selected delivery's number; setting it loads that delivery's lines (at its stored prices).</summary>
    public string? SelectedId
    {
        get => selectedId;
        set
        {
            if (!SetProperty(ref selectedId, value)) return;
            Load(value);
        }
    }

    public RelayCommand PayCashCommand { get; }
    public RelayCommand PayCardCommand { get; }
    public RelayCommand PaySplitCommand { get; }
    public AsyncRelayCommand<Guid> ReduceCommand { get; }
    public AsyncRelayCommand<Guid> RemoveCommand { get; }
    public AsyncRelayCommand<string> CancelCommand { get; }
    public RelayCommand ReprintCommand { get; }
    public RelayCommand BackCommand { get; }

    public async Task ReduceLineAsync(Guid id)
    {
        if (selected is null || Cart.Lines.FirstOrDefault(l => l.Id == id) is not { } line) return;
        if (line.FromScaleLabel || line.Qty <= 1m) { await RemoveLineAsync(id); return; }
        if (await gate.ApproveAsync(ApprovalAction.DeliveryChange, $"Delivery {selected.ClientId}: reduce {line.Item.ItemName}",
                selected.ClientId, line.Item.ItemCode, line.Rate) is null) return;
        Cart.Decrement(id);
        SaveChange();
    }

    /// <summary>Removes a line (supervisor); removing the last line cancels the delivery ("All items removed").</summary>
    public async Task RemoveLineAsync(Guid id)
    {
        if (selected is null || Cart.Lines.FirstOrDefault(l => l.Id == id) is not { } line) return;
        var supervisor = await gate.ApproveBySupervisorAsync(ApprovalAction.DeliveryChange,
            $"Delivery {selected.ClientId}: remove {line.Item.ItemName}", selected.ClientId, line.Item.ItemCode, line.Qty * line.Rate);
        if (supervisor is null) return;
        Cart.Remove(id);
        if (Cart.Lines.Count == 0)
        {
            Close(ctx.Deliveries.Cancel(selected.ClientId, ctx.Clock.Now, supervisor.Id, "All items removed"),
                $"Delivery {selected.ClientId} cancelled (all items removed)");
            return;
        }
        SaveChange();
    }

    public async Task CancelAsync(string reason)
    {
        if (selected is null) return;
        var supervisor = await gate.ApproveBySupervisorAsync(ApprovalAction.DeliveryCancel, $"Cancel delivery {selected.ClientId}: {reason}",
            selected.ClientId, null, selected.Bill.GrandTotal);
        if (supervisor is null) return;
        Close(ctx.Deliveries.Cancel(selected.ClientId, ctx.Clock.Now, supervisor.Id, reason), $"Delivery {selected.ClientId} cancelled ({reason})");
    }

    public Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift)
    {
        if (selected is not { } d) throw new InvalidOperationException("No delivery is selected.");
        var counter = ctx.CounterOf(session);
        var receipt = new SaleRecorder(ctx.Receipts, ctx.TillNumber, counter.Modes, () => ctx.Clock.Now, counter.DisplayName)
            .Build(Cart, plan, cashier.Id, shift.ClientId, cashier.User, cashier.Name, d.ClientId);
        if (!ctx.Deliveries.Pay(receipt, receipt.CreatedAt, cashier.Id))
            throw new InvalidOperationException($"Delivery {d.ClientId} was already paid or cancelled.");
        Cart.Clear();
        return receipt;
    }

    public void Completed(Receipt receipt, string? printError) => sale.SaleCompleted(receipt, printError);

    public void BackFromPayment() => ctx.Navigator.Show(this);

    private void Pay(TenderKind kind)
    {
        if (selected is null) { Error("Choose a delivery first."); return; }
        ctx.Navigator.Show(new PaymentViewModel(ctx, session, this, kind));
    }

    private void Reprint()
    {
        if (selected is null) return;
        try
        {
            ctx.Output.PrintDelivery(selected, copy: true);
            Info($"Delivery slip {selected.ClientId} printed again");
        }
        catch (Exception ex)
        {
            Error($"The slip did not print ({ex.Message}).");
        }
    }

    private void Back()
    {
        sale.RefreshDeliveryCount();
        ctx.Navigator.Show(sale);
    }

    private void SaveChange()
    {
        if (selected is null) return;
        var changed = Deliveries.Rebill(selected, Cart);
        if (!ctx.Deliveries.Update(changed)) { Error($"Delivery {selected.ClientId} is no longer open."); Reload(null); return; }
        selected = changed;
        Info($"Delivery {changed.ClientId} changed: collect {Format.Money(changed.CashToCollect)} cash or {Format.Money(changed.CardToCollect)} card");
        Reload(changed.ClientId);
    }

    private void Close(bool done, string text)
    {
        if (done) Info(text);
        else Error("That delivery is no longer open.");
        Reload(null);
        sale.RefreshDeliveryCount();
    }

    private void Reload(string? select)
    {
        Rows.Clear();
        var now = ctx.Clock.Now;
        foreach (var d in ctx.Deliveries.Open())
        {
            var age = now - d.CreatedAt;
            Rows.Add(new DeliveryRow(d.ClientId, d.CreatedAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture), d.CashierName ?? d.Cashier,
                Format.Money(d.CardToCollect), Age(age), age > TimeSpan.FromDays(OldAfterDays)));
        }
        var target = select is not null && Rows.Any(r => r.ClientId == select) ? select : Rows.FirstOrDefault()?.ClientId;
        selectedId = null;                                  // force a reload even when the same id stays selected
        SelectedId = target;
        if (target is null) Load(null);
    }

    private void Load(string? id)
    {
        selected = id is null ? null : ctx.Deliveries.Get(id);
        Cart = new Cart(ctx.SaleContextFor(session));
        if (selected is not null) Cart.RestoreFixed(selected.Bill.Lines);
        SelectedLines.Clear();
        foreach (var line in SaleViewModel.LinesOf(Cart)) SelectedLines.Add(line);
        OnPropertyChanged(nameof(Cart));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CashToCollect));
        OnPropertyChanged(nameof(CardToCollect));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Vat));
        OnPropertyChanged(nameof(Discount));
    }

    private static string Age(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} d {age.Hours} h"
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} h {age.Minutes} min"
        : $"{Math.Max(0, (int)age.TotalMinutes)} min";

    private void Info(string text) { Message = text; MessageIsError = false; }
    private void Error(string text) { Message = text; MessageIsError = true; }
}
```

`CloseShiftViewModel.cs` — in `Closed(...)`, compute the summary before printing and pass it:

```csharp
        DeliverySummary? deliveries = null;
        try
        {
            var paid = ctx.Receipts.ByShift(opening.ClientId).Where(r => r.IsDelivery && r.Kind == ReceiptKind.Sale).ToList();
            deliveries = new DeliverySummary(paid.Count, paid.Sum(r => r.GrandTotal), ctx.Deliveries.Open());
        }
        catch (Exception)
        {
            // The report prints without the delivery lines rather than not at all.
        }
        ...
        ctx.Output.PrintShiftReport(opening, result, session.Cashier?.Name ?? opening.Cashier, approvedBy, null, deliveries);
```
(`using TillPOS.Core.Sales;`.)

- [ ] **Step 5: Run all tests** — Expected: PASS. Any other construction of `TillContext` in tests (search `new TillContext(`) must set `Deliveries`.

- [ ] **Step 6: Commit**

```bash
git add tillpos/src/TillPOS.Presentation tillpos/tests/TillPOS.Tests/Presentation
git commit -m "feat(delivery): make, list, change, cancel and pay deliveries; Z report lists paid and still-out deliveries"
```

---

### Task 6: App wiring, screens, version 0.4.4, live check on dev

**Files:**
- Create: `tillpos/src/TillPOS.App/Views/DeliveriesView.xaml`, `DeliveriesView.xaml.cs`
- Modify: `tillpos/src/TillPOS.App/MainWindow.xaml` (DataTemplate)
- Modify: `tillpos/src/TillPOS.App/Views/SaleView.xaml` (F9 key, two buttons)
- Modify: `tillpos/src/TillPOS.App/AppHost.cs` (`Deliveries = new DeliveryStore(db, receipts)`)
- Modify: `tillpos/src/TillPOS.App/TillPOS.App.csproj`, `tillpos/tools/publish-field.ps1` (0.4.3 → 0.4.4)
- Modify: `tillpos/tools/field/START HERE.txt` (DELIVERIES section)

- [ ] **Step 1: AppHost** — in the `new TillContext(...) { ... }` initializer add `Deliveries = new DeliveryStore(db, receipts),` (the `receipts` local is the `ReceiptStore` already passed positionally).

- [ ] **Step 2: MainWindow.xaml** — add after the ReturnViewModel template:

```xml
    <DataTemplate DataType="{x:Type vm:DeliveriesViewModel}"><views:DeliveriesView /></DataTemplate>
```

- [ ] **Step 3: SaleView.xaml** — KeyBindings: add `<KeyBinding Key="F9" Command="{Binding MakeDeliveryCommand}" />`. In the pad `Grid`, add a fifth `<RowDefinition />` and:

```xml
          <Button Grid.Row="4" Grid.Column="0" Style="{StaticResource PadButton}" local:ButtonIcon.Glyph="&#xE806;" Content="Delivery (F9)" Command="{Binding MakeDeliveryCommand}" />
          <Button Grid.Row="4" Grid.Column="1" Style="{StaticResource PadButton}" local:ButtonIcon.Glyph="&#xE7BF;" Command="{Binding OpenDeliveriesCommand}">
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="Deliveries" VerticalAlignment="Center" />
              <!-- How many deliveries are out (not paid yet); hidden when none. -->
              <Border Background="{StaticResource Warn}" CornerRadius="11" MinWidth="24" Height="22" Padding="6,0" Margin="8,0,0,0"
                      VerticalAlignment="Center" Visibility="{Binding DeliveryCount, Converter={StaticResource CountToVisibility}}">
                <TextBlock Text="{Binding DeliveryCount}" Foreground="White" FontSize="14" FontWeight="Bold"
                           HorizontalAlignment="Center" VerticalAlignment="Center" />
              </Border>
            </StackPanel>
          </Button>
```

- [ ] **Step 4: DeliveriesView** — `DeliveriesView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.DeliveriesView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:TillPOS.App">
  <UserControl.InputBindings>
    <KeyBinding Key="Escape" Command="{Binding BackCommand}" />
    <KeyBinding Key="F12" Command="{Binding PayCashCommand}" />
    <KeyBinding Key="F11" Command="{Binding PayCardCommand}" />
  </UserControl.InputBindings>
  <Grid MaxWidth="1480">
    <Grid.ColumnDefinitions><ColumnDefinition Width="460" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
    <!-- Open deliveries, oldest first; red after 7 days out. -->
    <Border Style="{StaticResource Card}" Margin="0,0,16,0" Padding="12,10">
      <DockPanel>
        <TextBlock DockPanel.Dock="Top" Text="DELIVERIES NOT PAID YET" Style="{StaticResource SectionTitle}" Margin="4,0,0,6" />
        <Button DockPanel.Dock="Bottom" Focusable="False" Style="{StaticResource KeyButton}" Height="56" Margin="0,8,0,0"
                local:ButtonIcon.Glyph="&#xE72B;" Content="Back to the sale (Esc)" Command="{Binding BackCommand}" />
        <Grid>
          <ListBox ItemsSource="{Binding Rows}" SelectedValuePath="ClientId" SelectedValue="{Binding SelectedId}" HorizontalContentAlignment="Stretch">
            <ListBox.ItemTemplate>
              <DataTemplate>
                <Grid Margin="4,6">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
                  <StackPanel>
                    <TextBlock Text="{Binding ClientId}" FontWeight="SemiBold" FontFamily="{StaticResource CodeFont}" />
                    <TextBlock FontSize="13" Foreground="{StaticResource Muted}">
                      <Run Text="{Binding Made, Mode=OneWay}" /><Run Text=" · " /><Run Text="{Binding Cashier, Mode=OneWay}" /><Run Text=" · out " /><Run Text="{Binding Age, Mode=OneWay}" />
                    </TextBlock>
                  </StackPanel>
                  <TextBlock Grid.Column="1" Text="{Binding Amount}" FontSize="18" VerticalAlignment="Center">
                    <TextBlock.Style>
                      <Style TargetType="TextBlock" BasedOn="{StaticResource Money}">
                        <Style.Triggers>
                          <DataTrigger Binding="{Binding Old}" Value="True"><Setter Property="Foreground" Value="{StaticResource Danger}" /></DataTrigger>
                        </Style.Triggers>
                      </Style>
                    </TextBlock.Style>
                  </TextBlock>
                </Grid>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
          <TextBlock Text="No deliveries are out." HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{StaticResource Muted}"
                     IsHitTestVisible="False" Visibility="{Binding Rows.Count, Converter={StaticResource EmptyToVisibility}}" />
        </Grid>
      </DockPanel>
    </Border>
    <!-- The selected delivery: its lines (reduce / remove need a supervisor), amounts to collect, pay / reprint / cancel. -->
    <DockPanel Grid.Column="1" Visibility="{Binding HasSelection, Converter={StaticResource BoolToVisibility}}">
      <Border DockPanel.Dock="Top" Style="{StaticResource InkCard}" Padding="20,12">
        <UniformGrid Columns="2">
          <StackPanel>
            <TextBlock Text="COLLECT IN CASH (AED)" Foreground="{StaticResource OnInkMuted}" FontSize="13" FontWeight="SemiBold" />
            <TextBlock Text="{Binding CashToCollect}" Foreground="White" FontSize="38" FontWeight="Bold" Style="{StaticResource Money}" />
          </StackPanel>
          <StackPanel>
            <TextBlock Text="OR BY CARD (AED)" Foreground="{StaticResource OnInkMuted}" FontSize="13" FontWeight="SemiBold" />
            <TextBlock Text="{Binding CardToCollect}" Foreground="White" FontSize="38" FontWeight="Bold" Style="{StaticResource Money}" />
          </StackPanel>
        </UniformGrid>
      </Border>
      <StackPanel DockPanel.Dock="Bottom" Margin="0,8,0,0">
        <TextBlock Text="{Binding Message}" TextWrapping="Wrap" MinHeight="22" Margin="4,0,4,6">
          <TextBlock.Style>
            <Style TargetType="TextBlock">
              <Setter Property="Foreground" Value="{StaticResource Accent}" />
              <Style.Triggers>
                <DataTrigger Binding="{Binding MessageIsError}" Value="True"><Setter Property="Foreground" Value="{StaticResource Danger}" /></DataTrigger>
              </Style.Triggers>
            </Style>
          </TextBlock.Style>
        </TextBlock>
        <UniformGrid Columns="3">
          <Button Focusable="False" Style="{StaticResource PrimaryButton}" Height="64" Margin="4" Content="Pay cash (F12)" Command="{Binding PayCashCommand}" />
          <Button Focusable="False" Style="{StaticResource KeyButton}" Height="64" Margin="4" local:ButtonIcon.Glyph="&#xE8C7;" Content="Pay card (F11)" Command="{Binding PayCardCommand}" />
          <Button Focusable="False" Style="{StaticResource KeyButton}" Height="64" Margin="4" Content="Split: card + cash" Command="{Binding PaySplitCommand}" />
        </UniformGrid>
        <UniformGrid Columns="4" Margin="0,4,0,0">
          <Button Focusable="False" Style="{StaticResource SecondaryButton}" Height="52" Margin="4" local:ButtonIcon.Glyph="&#xE749;" Content="Reprint slip" Command="{Binding ReprintCommand}" />
          <Button Focusable="False" Style="{StaticResource DangerButton}" Height="52" Margin="4" Content="Cancel: refused" Command="{Binding CancelCommand}" CommandParameter="Refused" />
          <Button Focusable="False" Style="{StaticResource DangerButton}" Height="52" Margin="4" Content="Cancel: not delivered" Command="{Binding CancelCommand}" CommandParameter="Not delivered" />
          <Button Focusable="False" Style="{StaticResource DangerButton}" Height="52" Margin="4" Content="Cancel: other" Command="{Binding CancelCommand}" CommandParameter="Other" />
        </UniformGrid>
      </StackPanel>
      <Border Style="{StaticResource Card}" Margin="0,8,0,0" Padding="16,8">
        <ScrollViewer Focusable="False" VerticalScrollBarVisibility="Auto">
          <ItemsControl ItemsSource="{Binding SelectedLines}" Focusable="False">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Border BorderBrush="{StaticResource Line}" BorderThickness="0,0,0,1" Padding="0,6">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
                    <StackPanel>
                      <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                      <TextBlock FontSize="13" Foreground="{StaticResource Muted}"><Run Text="{Binding Qty, Mode=OneWay}" /><Run Text=" × " /><Run Text="{Binding Price, Mode=OneWay}" /></TextBlock>
                    </StackPanel>
                    <TextBlock Grid.Column="1" Text="{Binding Amount}" FontSize="17" VerticalAlignment="Center" Margin="0,0,12,0" Style="{StaticResource Money}" />
                    <Button Grid.Column="2" Focusable="False" Style="{StaticResource IconButton}" Content="&#xE738;" ToolTip="Reduce (supervisor)"
                            Command="{Binding DataContext.ReduceCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding Id}" />
                    <Button Grid.Column="3" Focusable="False" Style="{StaticResource DangerIconButton}" Content="&#xE711;" ToolTip="Remove (supervisor)"
                            Command="{Binding DataContext.RemoveCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding Id}" />
                  </Grid>
                </Border>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>
      </Border>
    </DockPanel>
  </Grid>
</UserControl>
```

`DeliveriesView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace TillPOS.App.Views;

/// <summary>The Deliveries screen (see DeliveriesViewModel). Every button is non-focusable; Esc goes back to the sale.</summary>
public partial class DeliveriesView : UserControl
{
    public DeliveriesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }
}
```

(Resource keys used — `Card`, `InkCard`, `SectionTitle`, `KeyButton`, `PrimaryButton`, `SecondaryButton`, `DangerButton`, `IconButton`, `DangerIconButton`, `Money`, `CodeFont`, `BoolToVisibility`, `EmptyToVisibility`, `CountToVisibility`, brushes — all exist in `Theme.xaml` / are used by SaleView and PaymentView. `Focusable="True"` on the root UserControl if Esc does not reach it.)

- [ ] **Step 5: START HERE + version** — in `tools/field/START HERE.txt`, after "CLOSING THE SHIFT", add:

```text
DELIVERIES
Scan the order's items, then press "Delivery (F9)" and confirm. A
DELIVERY INVOICE - NOT PAID prints with the amount to collect (cash
rounded, or the exact card amount). Give it to the driver; the bill waits
under "Deliveries" (the badge shows how many are out).
When the driver is back, press "Deliveries" (or scan the slip's barcode),
choose the delivery and press Pay cash / Pay card / Split. Cash is filled in
with the exact amount (no change). It then prints the normal invoice
marked "DELIVERY - PAID" and counts in the current shift.
If the customer kept only some items, reduce or remove lines there first
(supervisor PIN). If the customer refused it, press Cancel (supervisor PIN).
Deliveries still out are listed on the Z report and stay for the next shift.
```

Change `0.4.3` → `0.4.4` in `TillPOS.App.csproj` (Version and InformationalVersion) and in `tools/publish-field.ps1` (its default and usage lines).

- [ ] **Step 6: Build and test** — `& 'C:\Program Files\dotnet\dotnet.exe' test -m:1` (all pass), then `powershell -ExecutionPolicy Bypass -File tools\publish-field.ps1 -SingleExe` (from `tillpos/`).

- [ ] **Step 7: Live check on the dev ERPNext** (scratch till, developer key — never `till-dev`; never production):
  1. Open a shift; make a delivery of 2 items → slip file `DELIVERY-….txt` shows NOT PAID, both amounts, barcode; nothing uploads (Sync status: 0 waiting).
  2. Make a second delivery; reduce a line (supervisor) → new amounts; cancel it (supervisor) → gone from the list; approvals upload.
  3. Pay the first by cash from the list (prefilled, change 0.00) → invoice marked DELIVERY - PAID; uploads; in ERPNext the POS Invoice is dated at the payment time, in this shift, paid.
  4. Make a third delivery; close the shift → Z report shows "Deliveries paid (1)" and the third under DELIVERIES STILL OUT; the closing merges in ERPNext with nothing outstanding.
  5. Open a new shift; pay the third by card → uploads in the new shift.

- [ ] **Step 8: Commit**

```bash
git add tillpos
git commit -m "feat(app): Delivery (F9) and Deliveries screen; version 0.4.4"
```
