# TillPOS Plan 2a — Offline Sales Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Everything the till does between "scan" and "upload", fully offline: scale-label barcodes and unit fallback, cash/card/split payment with the agreed rounding, completed bills stored in an outbox with client IDs, returns, hold & recall, shifts with blind count, supervisor PINs with an approval log, and the cashier list download.

**Architecture:** Pure rules in `TillPOS.Core` (scale labels, payments, receipts, returns, shifts, security), SQLite stores in `TillPOS.Data` (one migration per store), and one new feed in `TillPOS.Sync`. Nothing in this plan writes to ERPNext; uploading is Plan 2b.

**Tech Stack:** .NET 10 (C#), xUnit, Microsoft.Data.Sqlite, System.Text.Json, System.Security.Cryptography (PBKDF2).

**Spec:** `docs/superpowers/specs/2026-10-05-tillpos-offline-pos-design.md` — especially §0 (decisions 2026-10-06), §13a, §13b.

## Global Constraints

- Phase 1A till only (spec §0). Target framework `net10.0`; `TreatWarningsAsErrors` and nullable on.
- Money is `decimal`; SQLite stores it inside JSON (System.Text.Json writes decimals exactly) or as invariant text. All formatting/parsing uses `CultureInfo.InvariantCulture` — UAE PCs may run an Arabic culture with a non-Gregorian calendar.
- Live currency precision is **3**; tests that model live data use `new MoneySettings(3, RoundingMethod.Bankers, 0.25m)`.
- **Follow the data** (spec §0 decision 1): never correct ERPNext data; a barcode's unit that is not set up on its item is sold in the item's stock unit and flagged.
- **Scale labels** (decision 2): EAN-13 starting `2` = `2` + item code (6) + grams (5) + check digit; look up full code, then first 7 digits, then digits 2–7.
- **Rounding** (decision 4): card exact; cash rounded to the currency's smallest fraction (AED 0.25) with ERPNext's rule (`Rounder.RoundToSmallestFraction`); split = card exact + cash remainder rounded.
- **Supervisor approval** (spec §13b.5): line removal, bill void, return without receipt, refund above AED 50, no-sale drawer open.
- Client IDs: receipts `TILL{n}-{yyyyMMddHHmmss}-{000000}`, shifts `TILL{n}-SHIFT-{yyyyMMddHHmmss}`.
- No writes to ERPNext anywhere in this plan.

## Review Focus

1. **A label whose 7-digit and 6-digit keys both exist as barcodes** → the 7-digit key wins, deterministically (Task 1 test `Seven_digit_key_wins_over_six_digit_key`).
2. **Pressing + on a weighed line** → refused with a clear message, quantity unchanged (Task 1 test `Weighed_lines_cannot_be_incremented`).
3. **Completing a sale with a payment calculated for an older total** (cashier scanned another item after opening payment) → refused, nothing saved, cart kept (Task 3 test `Stale_payment_plan_is_refused_and_nothing_is_saved`).
4. **Returning the same line twice across two return receipts** → the second return can only take what is left (Task 5 test `Second_return_can_only_take_what_is_left`).
5. **Two cashiers with the same PIN** → nobody is logged in by that PIN (Task 8 test `Duplicate_pin_logs_nobody_in`).

---

## File Structure

```
tillpos/src/TillPOS.Core/
  Sales/ScaleLabel.cs            scale-label parsing (EAN-13 '2' + code + grams + check)
  Sales/Cart.cs                  (modify) label/unit-fallback scanning, weighed lines, hold snapshot/restore
  Sales/Receipt.cs               Receipt, ReceiptLine, ReceiptPayment, TenderModes, ClientIds, IReceiptStore
  Sales/SaleRecorder.cs          cart + payment plan → stored receipt
  Sales/ReturnBuilder.cs         returns against a receipt / without receipt
  Sales/HeldCart.cs              HeldLine, HeldCart records
  Payments/PaymentCalculator.cs  Tender, PaymentPlan, cash/card/split/refund rules
  Shifts/ShiftCalculator.cs      ShiftOpening, ShiftClosing, expected-vs-counted per payment mode
  Security/Security.cs           Cashier, ApprovalAction, ApprovalRecord, PinHasher, Authenticator, ApprovalRequiredException
tillpos/src/TillPOS.Data/
  Migrations.cs                  (modify) append v2–v5
  ReceiptStore.cs                receipts + outbox status
  HeldCartStore.cs               parked bills
  ShiftStore.cs                  shifts
  SecurityStores.cs              CashierStore, ApprovalStore
tillpos/src/TillPOS.Sync/Feeds/CashierFeed.cs   POS Cashier download
tillpos/tests/TillPOS.Tests/
  Fakes/InMemoryReceiptStore.cs
  Core/ScaleLabelTests.cs, Core/CartScanTests.cs, Core/PaymentCalculatorTests.cs, Core/SaleRecorderTests.cs,
  Core/ReturnBuilderTests.cs, Core/ShiftCalculatorTests.cs, Core/SecurityTests.cs
  Data/ReceiptStoreTests.cs, Data/HeldCartStoreTests.cs, Data/ShiftStoreTests.cs, Data/SecurityStoresTests.cs
  Sync/CashierFeedTests.cs
```

---

### Task 1: Scale labels, unit fallback and weighed lines in the cart

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/ScaleLabel.cs`
- Modify: `tillpos/src/TillPOS.Core/Sales/Cart.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/ScaleLabelTests.cs`, `tillpos/tests/TillPOS.Tests/Core/CartScanTests.cs`

**Interfaces:**
- Consumes: `ICatalog`, `ItemBarcode`, existing `Cart`/`CartLine`/`AddOutcome`.
- Produces:
  - `sealed record ScaleLabel(string Code, decimal WeightKg)` with `IReadOnlyList<string> LookupKeys`, `static bool IsScaleLabelShape(string)`, `static ScaleLabel? TryParse(string)`, `static bool HasValidCheckDigit(string)`
  - `CartLine` gains `string? Barcode`, `bool IsWeighed`, `string? UomFallbackFrom`
  - `AddOutcome` gains `InvalidScaleLabel` (appended last)
  - `Cart.Increment/Decrement` throw `InvalidOperationException` for weighed lines

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/ScaleLabelTests.cs`:

```csharp
using TillPOS.Core.Sales;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ScaleLabelTests
{
    /// <summary>Appends the EAN-13 check digit to 12 digits.</summary>
    public static string Ean(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (10 - sum % 10) % 10;
    }

    [Fact]
    public void Parses_the_cucumber_label_from_the_shop()
    {
        Assert.Equal("2000089007400", Ean("200008900740"));
        var label = ScaleLabel.TryParse("2000089007400")!;
        Assert.Equal(M("0.740"), label.WeightKg);
        Assert.Equal(new[] { "2000089", "000089" }, label.LookupKeys);
    }

    [Theory]
    [InlineData("2000089007401")]   // wrong check digit
    [InlineData("200008900000")]    // 12 digits
    [InlineData("1000089007400")]   // not starting with 2
    [InlineData("20000890074A0")]   // not all digits
    public void Rejects_codes_that_are_not_valid_scale_labels(string code) => Assert.Null(ScaleLabel.TryParse(code));

    [Fact]
    public void Zero_weight_is_not_a_valid_label() => Assert.Null(ScaleLabel.TryParse(Ean("200008900000")));

    [Fact]
    public void Shape_check_does_not_look_at_the_check_digit()
    {
        Assert.True(ScaleLabel.IsScaleLabelShape("2000089007401"));
        Assert.False(ScaleLabel.IsScaleLabelShape("6291105656948"));
    }
}
```

`tillpos/tests/TillPOS.Tests/Core/CartScanTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.Core.ScaleLabelTests;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class CartScanTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m),
        "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6)));

    private void Item(string code, string name, string stockUom, string price, params (string Barcode, string? Uom)[] barcodes)
    {
        catalog.Items.Add(new Item(code, name, "Food", null, stockUom, false, true));
        catalog.Prices.Add(new ItemPrice("P-" + code, code, stockUom, M(price), null, null));
        foreach (var (barcode, uom) in barcodes) catalog.Barcodes.Add(new ItemBarcode(barcode, code, uom));
    }

    [Fact]
    public void Cucumber_label_resolves_by_the_six_digit_code_and_uses_the_weight()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        var result = cart.AddBarcode("2000089007400");

        Assert.Equal(AddOutcome.Added, result.Outcome);
        var line = Assert.Single(cart.Lines);
        Assert.Equal("000089", line.Item.ItemCode);
        Assert.Equal(M("0.740"), line.Qty);
        Assert.True(line.IsWeighed);
        Assert.Equal("2000089007400", line.Barcode);
        Assert.Equal(M("2.590"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Label_resolves_by_the_seven_digit_barcode()
    {
        Item("000088", "TOMATO", "Kg", "4.00", ("2000088", "Kg"));
        var cart = NewCart();

        cart.AddBarcode(Ean("200008800450"));

        Assert.Equal(M("0.450"), Assert.Single(cart.Lines).Qty);
    }

    [Fact]
    public void Seven_digit_key_wins_over_six_digit_key()
    {
        Item("SEVEN", "Seven", "Kg", "1.00", ("2000077", "Kg"));
        Item("SIX", "Six", "Kg", "1.00", ("000077", "Kg"));
        var cart = NewCart();

        cart.AddBarcode(Ean("200007700100"));

        Assert.Equal("SEVEN", Assert.Single(cart.Lines).Item.ItemCode);
    }

    [Fact]
    public void A_13_digit_barcode_stored_on_an_item_is_a_normal_scan()
    {
        var fixedCode = Ean("212345678901");
        Item("FIXED", "Fixed pack", "PCS", "5.00", (fixedCode, null));
        var cart = NewCart();

        cart.AddBarcode(fixedCode);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Qty);
        Assert.False(line.IsWeighed);
    }

    [Fact]
    public void Two_labels_of_the_same_item_stay_separate_lines()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        cart.AddBarcode("2000089007400");
        cart.AddBarcode(Ean("200008900500"));

        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void Label_with_a_wrong_check_digit_is_a_misscan()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        Assert.Equal(AddOutcome.InvalidScaleLabel, cart.AddBarcode("2000089007401").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Label_for_an_unknown_item_is_an_unknown_barcode() =>
        Assert.Equal(AddOutcome.UnknownBarcode, NewCart().AddBarcode("2000089007400").Outcome);

    [Fact]
    public void Barcode_unit_missing_on_the_item_is_sold_in_the_stock_unit()
    {
        Item("003497", "YELLOW DAMIATY CHEESE", "PCS", "16.00", ("003497", "Kg"));
        var cart = NewCart();

        Assert.Equal(AddOutcome.Added, cart.AddBarcode("003497").Outcome);

        var line = Assert.Single(cart.Lines);
        Assert.Equal("PCS", line.Uom);
        Assert.Equal("Kg", line.UomFallbackFrom);
        Assert.Equal(M("16.000"), line.Rate);
    }

    [Fact]
    public void Weighed_lines_cannot_be_incremented()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();
        var id = cart.AddBarcode("2000089007400").Line!.Id;

        Assert.Throws<InvalidOperationException>(() => cart.Increment(id));
        Assert.Throws<InvalidOperationException>(() => cart.Decrement(id));
        Assert.Equal(M("0.740"), cart.Lines[0].Qty);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ScaleLabelTests|CartScanTests"`
Expected: build FAIL — `ScaleLabel` not found; `CartLine` has no `IsWeighed`.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Sales/ScaleLabel.cs`:

```csharp
using System.Globalization;

namespace TillPOS.Core.Sales;

/// <summary>A scale-printed EAN-13 label: '2' + item code (6) + weight in grams (5) + check digit,
/// e.g. 2000089007400 = item code 000089, 0.740 kg.</summary>
public sealed record ScaleLabel(string Code, decimal WeightKg)
{
    /// <summary>Barcodes looked up in the database, in order: the first 7 digits (2000089), then digits 2–7 (000089).</summary>
    public IReadOnlyList<string> LookupKeys => [Code[..7], Code[1..7]];

    public static bool IsScaleLabelShape(string code) =>
        code.Length == 13 && code[0] == '2' && code.All(char.IsAsciiDigit);

    /// <summary>Null when the code is not a scale label, its check digit is wrong, or the weight is zero.</summary>
    public static ScaleLabel? TryParse(string code)
    {
        if (!IsScaleLabelShape(code) || !HasValidCheckDigit(code)) return null;
        var grams = int.Parse(code.AsSpan(7, 5), CultureInfo.InvariantCulture);
        return grams == 0 ? null : new ScaleLabel(code, grams / 1000m);
    }

    public static bool HasValidCheckDigit(string ean13)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (ean13[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == ean13[12] - '0';
    }
}
```

In `tillpos/src/TillPOS.Core/Sales/Cart.cs`:

1. Replace the `CartLine` class with:

```csharp
public sealed class CartLine
{
    internal CartLine(Item item, string uom, decimal conversionFactor, decimal priceListRate, AppliedRule? rule, decimal rate,
        string? itemTaxTemplate, string? barcode, decimal? weightKg, string? uomFallbackFrom)
    {
        Item = item;
        Uom = uom;
        ConversionFactor = conversionFactor;
        PriceListRate = priceListRate;
        Rule = rule;
        Rate = rate;
        ItemTaxTemplate = itemTaxTemplate;
        Barcode = barcode;
        IsWeighed = weightKg is not null;
        Qty = weightKg ?? 1m;
        UomFallbackFrom = uomFallbackFrom;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Item Item { get; }
    public string Uom { get; }
    public decimal ConversionFactor { get; }
    public decimal Qty { get; internal set; }
    public decimal PriceListRate { get; }
    public AppliedRule? Rule { get; }
    /// <summary>Unit rate after the offer; fixed when the line is added (cashiers cannot change it).</summary>
    public decimal Rate { get; }
    public string? ItemTaxTemplate { get; }
    /// <summary>The code that was scanned (a scale label keeps the full 13 digits).</summary>
    public string? Barcode { get; }
    /// <summary>Quantity came from a scale label; it is never merged and + / − do not apply.</summary>
    public bool IsWeighed { get; }
    /// <summary>Set when the scanned barcode's unit is not set up on the item, so the line was sold in the stock unit.</summary>
    public string? UomFallbackFrom { get; }
}
```

2. Change the enum to:

```csharp
public enum AddOutcome { Added, UnknownBarcode, UnknownItem, ItemNotSellable, UnknownUom, NoPrice, UnsupportedTax, InvalidScaleLabel }
```

3. Replace `AddBarcode` and `AddItem` with:

```csharp
    public AddResult AddBarcode(string barcode)
    {
        var code = barcode.Trim();
        if (ctx.Catalog.FindBarcode(code) is { } exact) return AddScanned(exact, code, null);
        if (!ScaleLabel.IsScaleLabelShape(code)) return new AddResult(AddOutcome.UnknownBarcode, null);

        var label = ScaleLabel.TryParse(code);
        if (label is null) return new AddResult(AddOutcome.InvalidScaleLabel, null);
        foreach (var key in label.LookupKeys)
            if (ctx.Catalog.FindBarcode(key) is { } match) return AddScanned(match, code, label.WeightKg);
        return new AddResult(AddOutcome.UnknownBarcode, null);
    }

    public AddResult AddItem(string itemCode, string? uom = null) => Add(itemCode, uom, null, null, null);

    private AddResult AddScanned(ItemBarcode found, string scanned, decimal? weightKg)
    {
        var uom = found.Uom;
        string? fallbackFrom = null;
        var item = ctx.Catalog.FindItem(found.ItemCode);
        if (item is not null && !string.IsNullOrEmpty(uom) && ctx.Catalog.ConversionFactor(item.ItemCode, uom) is null)
        {
            fallbackFrom = uom; // follow the data: the barcode's unit isn't set up on the item, so sell in the stock unit
            uom = item.StockUom;
        }
        return Add(found.ItemCode, uom, scanned, weightKg, fallbackFrom);
    }

    private AddResult Add(string itemCode, string? uom, string? barcode, decimal? weightKg, string? uomFallbackFrom)
    {
        var item = ctx.Catalog.FindItem(itemCode);
        if (item is null) return new AddResult(AddOutcome.UnknownItem, null);
        if (item.Disabled || !item.IsSalesItem) return new AddResult(AddOutcome.ItemNotSellable, null);

        var lineUom = string.IsNullOrEmpty(uom) ? item.StockUom : uom;
        if (weightKg is null)
        {
            var existing = lines.FirstOrDefault(l => !l.IsWeighed && l.Item.ItemCode == item.ItemCode && l.Uom == lineUom);
            if (existing is not null)
            {
                existing.Qty += 1m;
                return new AddResult(AddOutcome.Added, existing);
            }
        }

        var cf = ctx.Catalog.ConversionFactor(item.ItemCode, lineUom);
        if (cf is null) return new AddResult(AddOutcome.UnknownUom, null);

        var date = ctx.Today();
        var priceListRate = prices.PriceListRate(item, lineUom, cf.Value, date);
        if (priceListRate is null) return new AddResult(AddOutcome.NoPrice, null);

        var plr = Rounder.Round(priceListRate.Value, ctx.Money);
        var rule = rules.Select(item, plr, cf.Value, date);
        var rate = LineMath.RateAfterRule(plr, cf.Value, rule, ctx.Money);
        var itemTaxTemplate = itemTaxes.TemplateFor(item, date);
        if (itemTaxTemplate is not null && ctx.Catalog.FindItemTaxTemplate(itemTaxTemplate) is null)
            return new AddResult(AddOutcome.UnsupportedTax, null);
        var line = new CartLine(item, lineUom, cf.Value, plr, rule, rate, itemTaxTemplate, barcode, weightKg, uomFallbackFrom);
        lines.Add(line);
        return new AddResult(AddOutcome.Added, line);
    }
```

4. Replace `Increment` and `Decrement` with:

```csharp
    public void Increment(Guid lineId) => Countable(lineId).Qty += 1m;

    public void Decrement(Guid lineId)
    {
        var line = Countable(lineId);
        if (line.Qty > 1m) line.Qty -= 1m;
    }

    private CartLine Countable(Guid lineId)
    {
        var line = Find(lineId);
        if (line.IsWeighed) throw new InvalidOperationException("Weighed lines take their quantity from the scale label.");
        return line;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ScaleLabelTests|CartScanTests|CartTests"`
Expected: PASS (new 16 + existing 9 CartTests). Then `dotnet test` — all pass, 0 warnings.

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): scale-label barcodes, unit fallback and weighed lines"
```

---

### Task 2: Payment calculation (cash, card, split, refunds)

**Files:**
- Create: `tillpos/src/TillPOS.Core/Payments/PaymentCalculator.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/PaymentCalculatorTests.cs`

**Interfaces:**
- Consumes: `MoneySettings`, `Rounder.RoundToSmallestFraction`, `Rounder.Round`.
- Produces (namespace `TillPOS.Core.Payments`):
  - `enum TenderKind { Cash, Card, Split }`
  - `record Tender(TenderKind Kind, decimal CardAmount, decimal CashTendered)` with `Cash(decimal)`, `Card()`, `Split(decimal card, decimal cash)`
  - `record PaymentPlan(TenderKind Kind, decimal GrandTotal, bool UsesErpRoundedTotal, decimal AmountDue, decimal CardAmount, decimal CashDue, decimal CashTendered, decimal Change, decimal Shortfall, decimal RoundingDifference)` with `bool IsComplete`
  - `sealed class PaymentCalculator(MoneySettings money)` with `PaymentPlan Plan(decimal grandTotal, Tender tender)` and `PaymentPlan PlanRefund(decimal grandTotal, TenderKind kind)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/PaymentCalculatorTests.cs`:

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PaymentCalculatorTests
{
    private readonly PaymentCalculator calc = new(new MoneySettings(3, RoundingMethod.Bankers, 0.25m));

    [Fact]
    public void Cash_is_rounded_to_a_quarter_with_change()
    {
        var p = calc.Plan(M("14.37"), Tender.Cash(20m));
        Assert.True(p.UsesErpRoundedTotal);
        Assert.Equal(M("14.25"), p.AmountDue);
        Assert.Equal(M("14.25"), p.CashDue);
        Assert.Equal(M("5.75"), p.Change);
        Assert.Equal(M("-0.12"), p.RoundingDifference);
        Assert.True(p.IsComplete);
    }

    [Theory]
    [InlineData("9.994", "10.000")]   // real live bill
    [InlineData("9.875", "9.750")]    // exact half of the fraction rounds down, as in ERPNext
    [InlineData("9.876", "10.000")]
    public void Cash_uses_erpnext_rounding_rule(string grand, string due) =>
        Assert.Equal(M(due), calc.Plan(M(grand), Tender.Cash(100m)).AmountDue);

    [Fact]
    public void Too_little_cash_leaves_a_shortfall()
    {
        var p = calc.Plan(M("14.37"), Tender.Cash(10m));
        Assert.False(p.IsComplete);
        Assert.Equal(M("4.25"), p.Shortfall);
        Assert.Equal(0m, p.Change);
    }

    [Fact]
    public void Card_is_exact_and_not_rounded()
    {
        var p = calc.Plan(M("14.37"), Tender.Card());
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(M("14.37"), p.AmountDue);
        Assert.Equal(M("14.37"), p.CardAmount);
        Assert.Equal(0m, p.CashDue);
        Assert.Equal(0m, p.RoundingDifference);
    }

    [Fact]
    public void Split_charges_card_exactly_and_rounds_the_cash_remainder()
    {
        var p = calc.Plan(M("14.37"), Tender.Split(10m, 5m));
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(10m, p.CardAmount);
        Assert.Equal(M("4.25"), p.CashDue);
        Assert.Equal(M("14.25"), p.AmountDue);
        Assert.Equal(M("0.75"), p.Change);
        Assert.Equal(M("-0.12"), p.RoundingDifference);
    }

    [Fact]
    public void Split_with_a_fractional_card_amount()
    {
        var p = calc.Plan(M("14.37"), Tender.Split(M("10.10"), 5m));
        Assert.Equal(M("4.25"), p.CashDue);
        Assert.Equal(M("14.35"), p.AmountDue);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("14.37")]
    [InlineData("20")]
    public void Split_card_part_must_be_between_zero_and_the_total(string card) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => calc.Plan(M("14.37"), Tender.Split(M(card), 5m)));

    [Fact]
    public void Cash_refund_is_rounded_like_erpnext()
    {
        var p = calc.PlanRefund(M("-10.13"), TenderKind.Cash);
        Assert.Equal(M("-10.25"), p.AmountDue);
        Assert.Equal(M("-10.25"), p.CashTendered);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public void Card_refund_is_exact()
    {
        var p = calc.PlanRefund(M("-10.13"), TenderKind.Card);
        Assert.Equal(M("-10.13"), p.AmountDue);
        Assert.Equal(M("-10.13"), p.CardAmount);
    }

    [Fact]
    public void Refund_needs_a_negative_total() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => calc.PlanRefund(M("5"), TenderKind.Cash));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PaymentCalculatorTests`
Expected: build FAIL — namespace `TillPOS.Core.Payments` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Payments/PaymentCalculator.cs`:

```csharp
using TillPOS.Core.Money;

namespace TillPOS.Core.Payments;

public enum TenderKind { Cash, Card, Split }

public sealed record Tender(TenderKind Kind, decimal CardAmount, decimal CashTendered)
{
    public static Tender Cash(decimal tendered) => new(TenderKind.Cash, 0m, tendered);
    public static Tender Card() => new(TenderKind.Card, 0m, 0m);
    public static Tender Split(decimal cardAmount, decimal cashTendered) => new(TenderKind.Split, cardAmount, cashTendered);
}

/// <summary>How a bill is paid. GrandTotal is ERPNext's exact total; AmountDue is what the customer pays.
/// UsesErpRoundedTotal = cash-only bill (ERPNext rounded total applies); card and split bills are not rounded as a whole.
/// RoundingDifference = cash due − the exact cash portion (negative when rounding went down).</summary>
public sealed record PaymentPlan(
    TenderKind Kind,
    decimal GrandTotal,
    bool UsesErpRoundedTotal,
    decimal AmountDue,
    decimal CardAmount,
    decimal CashDue,
    decimal CashTendered,
    decimal Change,
    decimal Shortfall,
    decimal RoundingDifference)
{
    public bool IsComplete => Shortfall == 0m;
}

/// <summary>Spec §0 decision 4: card exact; cash rounded to the currency's smallest fraction with ERPNext's rule;
/// split = card exact + cash remainder rounded.</summary>
public sealed class PaymentCalculator(MoneySettings money)
{
    public PaymentPlan Plan(decimal grandTotal, Tender tender) => tender.Kind switch
    {
        TenderKind.Cash => WithCash(grandTotal, TenderKind.Cash, 0m, grandTotal, tender.CashTendered),
        TenderKind.Card => CardOnly(grandTotal),
        TenderKind.Split => Split(grandTotal, tender),
        _ => throw new ArgumentOutOfRangeException(nameof(tender)),
    };

    /// <summary>Refunds: cash rounded like a sale, card exact; the refund is paid in full (nothing tendered).</summary>
    public PaymentPlan PlanRefund(decimal grandTotal, TenderKind kind)
    {
        if (grandTotal >= 0m) throw new ArgumentOutOfRangeException(nameof(grandTotal), "A refund total is negative.");
        return kind switch
        {
            TenderKind.Cash => WithCash(grandTotal, TenderKind.Cash, 0m, grandTotal, Rounder.RoundToSmallestFraction(grandTotal, money)),
            TenderKind.Card => CardOnly(grandTotal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Refunds are paid in cash or to the card."),
        };
    }

    private static PaymentPlan CardOnly(decimal grandTotal) =>
        new(TenderKind.Card, grandTotal, false, grandTotal, grandTotal, 0m, 0m, 0m, 0m, 0m);

    private PaymentPlan Split(decimal grandTotal, Tender tender)
    {
        if (tender.CardAmount <= 0m || tender.CardAmount >= grandTotal)
            throw new ArgumentOutOfRangeException(nameof(tender), "The card part must be more than zero and less than the bill total.");
        return WithCash(grandTotal, TenderKind.Split, tender.CardAmount, grandTotal - tender.CardAmount, tender.CashTendered);
    }

    private PaymentPlan WithCash(decimal grandTotal, TenderKind kind, decimal card, decimal exactCash, decimal tendered)
    {
        var cashDue = Rounder.RoundToSmallestFraction(exactCash, money);
        var shortfall = Math.Max(0m, cashDue - tendered);
        var change = Math.Max(0m, tendered - cashDue);
        return new PaymentPlan(kind, grandTotal, kind == TenderKind.Cash, card + cashDue, card, cashDue, tendered, change,
            shortfall, Rounder.Round(cashDue - exactCash, money));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PaymentCalculatorTests`
Expected: PASS (14 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): cash/card/split payment calculation with agreed rounding"
```

---

### Task 3: Receipts, client IDs and the sale recorder

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/Receipt.cs`, `tillpos/src/TillPOS.Core/Sales/SaleRecorder.cs`
- Create: `tillpos/tests/TillPOS.Tests/Fakes/InMemoryReceiptStore.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/SaleRecorderTests.cs`

**Interfaces:**
- Consumes: `Cart`, `CartLine`, `BillTotals`, `PaymentPlan`.
- Produces (namespace `TillPOS.Core.Sales`):
  - `enum ReceiptKind { Sale, Return }`
  - `record ReceiptLine(int LineNo, string ItemCode, string ItemName, string? Barcode, string Uom, decimal ConversionFactor, decimal Qty, decimal PriceListRate, decimal Rate, decimal Amount, string? PricingRule, string? ItemTaxTemplate, bool IsWeighed, string? UomFallbackFrom)`
  - `record ReceiptPayment(string ModeOfPayment, decimal Amount)`
  - `record Receipt(string ClientId, ReceiptKind Kind, string? ReturnAgainst, string ShiftClientId, string Cashier, DateTimeOffset CreatedAt, IReadOnlyList<ReceiptLine> Lines, decimal Total, decimal NetTotal, decimal TotalTaxes, decimal GrandTotal, bool UsesErpRoundedTotal, decimal RoundedTotal, decimal RoundingAdjustment, IReadOnlyList<ReceiptPayment> Payments, decimal Change, decimal RoundingDifference, string? ApprovedBy)`
  - `record TenderModes(string Cash, string Card)`
  - `static class ClientIds` — `Receipt(int till, DateTimeOffset at, long sequence)`, `Shift(int till, DateTimeOffset at)`
  - `interface IReceiptStore` — `long NextSequence()`, `void Save(Receipt)`, `Receipt? Get(string)`, `IReadOnlyList<Receipt> ReturnsAgainst(string)`, `IReadOnlyList<Receipt> ByShift(string)`
  - `sealed class SaleRecorder(IReceiptStore store, int tillNumber, TenderModes modes, Func<DateTimeOffset> now)` — `Receipt CompleteSale(Cart cart, PaymentPlan plan, string cashier, string shiftClientId)`; `internal static` helpers `ToLines`, `Payments`

- [ ] **Step 1: Write the fake and the failing tests**

`tillpos/tests/TillPOS.Tests/Fakes/InMemoryReceiptStore.cs`:

```csharp
using TillPOS.Core.Sales;

namespace TillPOS.Tests.Fakes;

public sealed class InMemoryReceiptStore : IReceiptStore
{
    private long sequence;
    public List<Receipt> Saved { get; } = [];

    public long NextSequence() => ++sequence;
    public void Save(Receipt receipt)
    {
        if (Saved.Any(r => r.ClientId == receipt.ClientId)) throw new InvalidOperationException("duplicate client id");
        Saved.Add(receipt);
    }
    public Receipt? Get(string clientId) => Saved.FirstOrDefault(r => r.ClientId == clientId);
    public IReadOnlyList<Receipt> ReturnsAgainst(string clientId) => Saved.Where(r => r.ReturnAgainst == clientId).ToList();
    public IReadOnlyList<Receipt> ByShift(string shiftClientId) => Saved.Where(r => r.ShiftClientId == shiftClientId).ToList();
}
```

`tillpos/tests/TillPOS.Tests/Core/SaleRecorderTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class SaleRecorderTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly InMemoryReceiptStore store = new();
    private readonly PaymentCalculator payments = new(Money);

    public SaleRecorderTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
    }

    private Cart NewCart() => new(new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6)));
    private SaleRecorder Recorder() => new(store, 2, Modes, () => At);

    [Fact]
    public void Cash_sale_is_stored_with_client_id_rounding_and_change_and_cart_is_cleared()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("2000089007400");
        var total = cart.Totals().GrandTotal;                      // 6.79 + 2.59 = 9.38
        var plan = payments.Plan(total, Tender.Cash(20m));

        var r = Recorder().CompleteSale(cart, plan, "p.simran@quickgroc.com", "TILL2-SHIFT-20261006080000");

        Assert.Equal("TILL2-20261006153000-000001", r.ClientId);
        Assert.Equal(ReceiptKind.Sale, r.Kind);
        Assert.Equal(M("9.380"), r.GrandTotal);
        Assert.True(r.UsesErpRoundedTotal);
        Assert.Equal(M("9.500"), r.RoundedTotal);                  // remainder .13 > .125 → up
        Assert.Equal(M("0.120"), r.RoundingAdjustment);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", 20m) }, r.Payments);
        Assert.Equal(M("10.50"), r.Change);
        Assert.Equal(2, r.Lines.Count);
        Assert.True(r.Lines[1].IsWeighed);
        Assert.Equal("2000089007400", r.Lines[1].Barcode);
        Assert.Equal(M("2.590"), r.Lines[1].Amount);
        Assert.Same(r, store.Get(r.ClientId));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Card_sale_is_exact_and_not_rounded()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Card());

        var r = Recorder().CompleteSale(cart, plan, "cashier", "S1");

        Assert.False(r.UsesErpRoundedTotal);
        Assert.Equal(0m, r.RoundedTotal);
        Assert.Equal(0m, r.RoundingAdjustment);
        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("6.790")) }, r.Payments);
    }

    [Fact]
    public void Split_sale_records_both_payments()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("111");                                    // 13.58
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Split(10m, 5m));

        var r = Recorder().CompleteSale(cart, plan, "cashier", "S1");

        Assert.Equal(new[] { new ReceiptPayment("Credit Card", 10m), new ReceiptPayment("Cash Counter 2", 5m) }, r.Payments);
        Assert.Equal(M("3.50"), plan.CashDue);                     // 3.58 → 3.50
        Assert.Equal(M("1.50"), r.Change);
    }

    [Fact]
    public void Sequence_increases_per_sale()
    {
        var recorder = Recorder();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var cart = NewCart();
            cart.AddBarcode("111");
            recorder.CompleteSale(cart, payments.Plan(cart.Totals().GrandTotal, Tender.Card()), "c", "S1");
        }
        Assert.Equal(new[] { "TILL2-20261006153000-000001", "TILL2-20261006153000-000002" }, store.Saved.Select(r => r.ClientId));
    }

    [Fact]
    public void Incomplete_payment_is_refused_and_cart_is_kept()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Cash(1m));

        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(cart, plan, "c", "S1"));
        Assert.Empty(store.Saved);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public void Stale_payment_plan_is_refused_and_nothing_is_saved()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Cash(50m));
        cart.AddBarcode("111");                                    // total changed after the plan

        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(cart, plan, "c", "S1"));
        Assert.Empty(store.Saved);
        Assert.Equal(2m, cart.Lines[0].Qty);
    }

    [Fact]
    public void Empty_bill_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(NewCart(), payments.Plan(0m, Tender.Card()), "c", "S1"));

    [Fact]
    public void Client_ids_use_the_gregorian_calendar_whatever_the_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("TILL2-20261006153000-000007", ClientIds.Receipt(2, At, 7));
            Assert.Equal("TILL2-SHIFT-20261006153000", ClientIds.Shift(2, At));
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SaleRecorderTests`
Expected: build FAIL — `Receipt`, `SaleRecorder` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Sales/Receipt.cs`:

```csharp
using System.Globalization;

namespace TillPOS.Core.Sales;

public enum ReceiptKind { Sale, Return }

public sealed record ReceiptLine(
    int LineNo,
    string ItemCode,
    string ItemName,
    string? Barcode,
    string Uom,
    decimal ConversionFactor,
    decimal Qty,
    decimal PriceListRate,
    decimal Rate,
    decimal Amount,
    string? PricingRule,
    string? ItemTaxTemplate,
    bool IsWeighed,
    string? UomFallbackFrom);

public sealed record ReceiptPayment(string ModeOfPayment, decimal Amount);

/// <summary>A completed bill (sale or return) as stored on the till and later uploaded.
/// Cash payment rows hold the cash tendered; Change is what was handed back (ERPNext/POS Awesome convention).</summary>
public sealed record Receipt(
    string ClientId,
    ReceiptKind Kind,
    string? ReturnAgainst,
    string ShiftClientId,
    string Cashier,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Total,
    decimal NetTotal,
    decimal TotalTaxes,
    decimal GrandTotal,
    bool UsesErpRoundedTotal,
    decimal RoundedTotal,
    decimal RoundingAdjustment,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal Change,
    decimal RoundingDifference,
    string? ApprovedBy);

/// <summary>The POS Profile payment modes the till uses for cash and card.</summary>
public sealed record TenderModes(string Cash, string Card);

public static class ClientIds
{
    public static string Receipt(int till, DateTimeOffset at, long sequence) =>
        $"TILL{till.ToString(CultureInfo.InvariantCulture)}-{Stamp(at)}-{sequence.ToString("000000", CultureInfo.InvariantCulture)}";

    public static string Shift(int till, DateTimeOffset at) =>
        $"TILL{till.ToString(CultureInfo.InvariantCulture)}-SHIFT-{Stamp(at)}";

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}

public interface IReceiptStore
{
    long NextSequence();
    void Save(Receipt receipt);
    Receipt? Get(string clientId);
    IReadOnlyList<Receipt> ReturnsAgainst(string clientId);
    IReadOnlyList<Receipt> ByShift(string shiftClientId);
}
```

`tillpos/src/TillPOS.Core/Sales/SaleRecorder.cs`:

```csharp
using TillPOS.Core.Payments;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

/// <summary>Turns a paid cart into a stored receipt (the outbox entry Plan 2b uploads) and clears the cart.</summary>
public sealed class SaleRecorder(IReceiptStore store, int tillNumber, TenderModes modes, Func<DateTimeOffset> now)
{
    public Receipt CompleteSale(Cart cart, PaymentPlan plan, string cashier, string shiftClientId)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var totals = cart.Totals();
        if (plan.GrandTotal != totals.GrandTotal)
            throw new InvalidOperationException("The payment was calculated for a different total; calculate it again.");
        if (!plan.IsComplete) throw new InvalidOperationException($"Still to pay: {plan.Shortfall}.");

        var at = now();
        var receipt = new Receipt(
            ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Sale, null, shiftClientId, cashier, at,
            ToLines(cart, totals), totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : 0m,
            Payments(plan, modes), plan.Change, plan.RoundingDifference, null);
        store.Save(receipt);
        cart.Clear();
        return receipt;
    }

    internal static IReadOnlyList<ReceiptLine> ToLines(Cart cart, BillTotals totals) =>
        cart.Lines.Select((l, i) => new ReceiptLine(i + 1, l.Item.ItemCode, l.Item.ItemName, l.Barcode, l.Uom, l.ConversionFactor,
            l.Qty, l.PriceListRate, l.Rate, totals.Lines[i].Amount, l.Rule?.RuleName, l.ItemTaxTemplate, l.IsWeighed,
            l.UomFallbackFrom)).ToList();

    internal static IReadOnlyList<ReceiptPayment> Payments(PaymentPlan plan, TenderModes modes)
    {
        var rows = new List<ReceiptPayment>();
        if (plan.CardAmount != 0m) rows.Add(new ReceiptPayment(modes.Card, plan.CardAmount));
        if (plan.CashTendered != 0m) rows.Add(new ReceiptPayment(modes.Cash, plan.CashTendered));
        return rows;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SaleRecorderTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): receipts with client ids and the sale recorder"
```

---

### Task 4: Receipt store (outbox) in SQLite

**Files:**
- Modify: `tillpos/src/TillPOS.Data/Migrations.cs` (append v2)
- Create: `tillpos/src/TillPOS.Data/ReceiptStore.cs`
- Test: `tillpos/tests/TillPOS.Tests/Data/ReceiptStoreTests.cs`

**Interfaces:**
- Consumes: `TillDb`, `SqlExt`, `Receipt`, `IReceiptStore`.
- Produces (namespace `TillPOS.Data`):
  - `enum ReceiptSyncStatus { Pending, Synced, Failed }`
  - `record ReceiptSyncInfo(ReceiptSyncStatus Status, string? ErpName, string? LastError, int Attempts)`
  - `sealed class ReceiptStore(TillDb db) : IReceiptStore` — plus `IReadOnlyList<Receipt> ListPending(int limit)`, `IReadOnlyList<Receipt> ListFailed()`, `void MarkSynced(string clientId, string erpName)`, `void MarkFailed(string clientId, string error)`, `void Retry(string clientId)`, `ReceiptSyncInfo SyncInfo(string clientId)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Data/ReceiptStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using TillPOS.Core.Sales;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class ReceiptStoreTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly ReceiptStore store;

    public ReceiptStoreTests() => store = new ReceiptStore(temp.Db);

    public void Dispose() => temp.Dispose();

    private static Receipt Sale(string id, string shift = "S1", int minute = 0, string? returnAgainst = null, decimal qty = 1m) => new(
        id, returnAgainst is null ? ReceiptKind.Sale : ReceiptKind.Return, returnAgainst, shift, "cashier",
        new DateTimeOffset(2026, 10, 6, 15, minute, 0, TimeSpan.FromHours(4)),
        [new ReceiptLine(1, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, qty * M("0.740"), M("3.50"), M("3.50"),
            qty * M("2.590"), null, null, true, null)],
        qty * M("2.590"), qty * M("2.467"), qty * M("0.123"), qty * M("2.590"), true, qty * M("2.500"), qty * M("-0.090"),
        [new ReceiptPayment("Cash Counter 2", 5m)], M("2.50"), M("-0.090"), null);

    [Fact]
    public void Receipt_round_trips_with_exact_decimals()
    {
        var r = Sale("TILL2-1");
        store.Save(r);
        var back = store.Get("TILL2-1")!;
        Assert.Equal(r with { Lines = back.Lines, Payments = back.Payments }, back);
        Assert.Equal(r.Lines, back.Lines);
        Assert.Equal(r.Payments, back.Payments);
        Assert.Equal(M("0.740"), back.Lines[0].Qty);
    }

    [Fact]
    public void Saving_the_same_client_id_twice_fails()
    {
        store.Save(Sale("TILL2-1"));
        Assert.Throws<SqliteException>(() => store.Save(Sale("TILL2-1")));
    }

    [Fact]
    public void Sequence_survives_a_new_store_instance()
    {
        Assert.Equal(1, store.NextSequence());
        Assert.Equal(2, store.NextSequence());
        Assert.Equal(3, new ReceiptStore(temp.Db).NextSequence());
    }

    [Fact]
    public void Pending_receipts_come_oldest_first_and_leave_the_outbox_when_synced()
    {
        store.Save(Sale("B", minute: 2));
        store.Save(Sale("A", minute: 1));

        Assert.Equal(new[] { "A", "B" }, store.ListPending(10).Select(r => r.ClientId));

        store.MarkSynced("A", "ACC-PSINV-2026-06001");
        Assert.Equal(new[] { "B" }, store.ListPending(10).Select(r => r.ClientId));
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Synced, "ACC-PSINV-2026-06001", null, 1), store.SyncInfo("A"));
    }

    [Fact]
    public void Failed_receipts_wait_for_a_retry()
    {
        store.Save(Sale("A"));
        store.MarkFailed("A", "Item 000089 is disabled");

        Assert.Empty(store.ListPending(10));
        Assert.Equal("A", Assert.Single(store.ListFailed()).ClientId);
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Failed, null, "Item 000089 is disabled", 1), store.SyncInfo("A"));

        store.Retry("A");
        Assert.Equal("A", Assert.Single(store.ListPending(10)).ClientId);
    }

    [Fact]
    public void Returns_and_shift_receipts_are_found()
    {
        store.Save(Sale("SALE", "S1"));
        store.Save(Sale("RET", "S2", returnAgainst: "SALE", qty: -1m));

        Assert.Equal("RET", Assert.Single(store.ReturnsAgainst("SALE")).ClientId);
        Assert.Equal("SALE", Assert.Single(store.ByShift("S1")).ClientId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReceiptStoreTests`
Expected: build FAIL — `ReceiptStore` not found.

- [ ] **Step 3: Implement**

Append to `Migrations.All` in `tillpos/src/TillPOS.Data/Migrations.cs` (after the existing v1 entry, inside the array):

```csharp
        """
        CREATE TABLE receipt (
            client_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            return_against TEXT,
            shift_client_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            json TEXT NOT NULL,
            sync_status TEXT NOT NULL DEFAULT 'Pending',
            erp_name TEXT,
            last_error TEXT,
            attempts INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_receipt_status ON receipt(sync_status, created_at);
        CREATE INDEX ix_receipt_shift ON receipt(shift_client_id);
        CREATE INDEX ix_receipt_return_against ON receipt(return_against);
        """,
```

`tillpos/src/TillPOS.Data/ReceiptStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

public enum ReceiptSyncStatus { Pending, Synced, Failed }

public sealed record ReceiptSyncInfo(ReceiptSyncStatus Status, string? ErpName, string? LastError, int Attempts);

/// <summary>Completed bills and their upload state (the outbox). Pending = waiting for upload,
/// Failed = ERPNext refused it (waits for a supervisor retry), Synced = uploaded.</summary>
public sealed class ReceiptStore(TillDb db) : IReceiptStore
{
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public long NextSequence()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, "INSERT INTO kv (key, value) VALUES ('receipt_seq', '0') ON CONFLICT(key) DO NOTHING");
        c.Exec(tx, "UPDATE kv SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key = 'receipt_seq'");
        var value = Convert.ToInt64(c.Scalar(tx, "SELECT value FROM kv WHERE key = 'receipt_seq'"), CultureInfo.InvariantCulture);
        tx.Commit();
        return value;
    }

    public void Save(Receipt receipt)
    {
        using var c = db.Open();
        c.Exec(null, """
            INSERT INTO receipt (client_id, kind, return_against, shift_client_id, created_at, json)
            VALUES (@id, @k, @ra, @s, @at, @j)
            """,
            ("@id", receipt.ClientId), ("@k", receipt.Kind.ToString()), ("@ra", receipt.ReturnAgainst), ("@s", receipt.ShiftClientId),
            ("@at", receipt.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("@j", JsonSerializer.Serialize(receipt, Json)));
    }

    public Receipt? Get(string clientId) => Query("WHERE client_id = @p", clientId).FirstOrDefault();
    public IReadOnlyList<Receipt> ReturnsAgainst(string clientId) => Query("WHERE return_against = @p ORDER BY created_at", clientId);
    public IReadOnlyList<Receipt> ByShift(string shiftClientId) => Query("WHERE shift_client_id = @p ORDER BY created_at", shiftClientId);

    public IReadOnlyList<Receipt> ListPending(int limit)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM receipt WHERE sync_status = 'Pending' ORDER BY created_at, client_id LIMIT @l",
            r => Deserialize(r.GetString(0)), ("@l", limit));
    }

    public IReadOnlyList<Receipt> ListFailed() => Query("WHERE sync_status = 'Failed' ORDER BY created_at", null);

    public void MarkSynced(string clientId, string erpName) =>
        Update("UPDATE receipt SET sync_status = 'Synced', erp_name = @n, last_error = NULL, attempts = attempts + 1 WHERE client_id = @id",
            clientId, ("@n", erpName));

    public void MarkFailed(string clientId, string error) =>
        Update("UPDATE receipt SET sync_status = 'Failed', last_error = @e, attempts = attempts + 1 WHERE client_id = @id",
            clientId, ("@e", error));

    public void Retry(string clientId) =>
        Update("UPDATE receipt SET sync_status = 'Pending' WHERE client_id = @id AND sync_status = 'Failed'", clientId);

    public ReceiptSyncInfo SyncInfo(string clientId)
    {
        using var c = db.Open();
        return c.Query("SELECT sync_status, erp_name, last_error, attempts FROM receipt WHERE client_id = @id",
                r => new ReceiptSyncInfo(Enum.Parse<ReceiptSyncStatus>(r.GetString(0)), SqlExt.Str(r, 1), SqlExt.Str(r, 2), r.GetInt32(3)),
                ("@id", clientId))
            .FirstOrDefault() ?? throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private IReadOnlyList<Receipt> Query(string where, string? p)
    {
        using var c = db.Open();
        return p is null
            ? c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)))
            : c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)), ("@p", p));
    }

    private void Update(string sql, string clientId, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        var changed = c.Exec(null, sql, [("@id", clientId), .. extra]);
        if (changed == 0 && !sql.Contains("sync_status = 'Failed'", StringComparison.Ordinal))
            throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private static Receipt Deserialize(string json) =>
        JsonSerializer.Deserialize<Receipt>(json, Json) ?? throw new InvalidDataException("Stored receipt is empty.");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ReceiptStoreTests|CatalogStoreTests"`
Expected: PASS (6 new + existing). Full `dotnet test` passes.

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(data): receipt outbox store"
```

---

### Task 5: Returns

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/ReturnBuilder.cs`
- Create: `tillpos/src/TillPOS.Core/Security/Security.cs` (only `ApprovalRequiredException` in this task; Task 8 adds the rest to the same file)
- Test: `tillpos/tests/TillPOS.Tests/Core/ReturnBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2 `PaymentCalculator`, Task 3 `Receipt`, `IReceiptStore`, `SaleRecorder.ToLines/Payments`, `TaxCalculator`.
- Produces:
  - namespace `TillPOS.Core.Security`: `sealed class ApprovalRequiredException(string reason) : Exception`
  - namespace `TillPOS.Core.Sales`: `record ReturnLineRequest(int LineNo, decimal Qty)`; `sealed class ReturnBuilder(IReceiptStore store, SaleContext ctx, int tillNumber, TenderModes modes, Func<DateTimeOffset> now, decimal approvalLimit = 50m)` with `decimal Returnable(Receipt original, int lineNo)`, `Receipt Build(Receipt original, IReadOnlyList<ReturnLineRequest> requests, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy)`, `Receipt BuildWithoutReceipt(Cart cart, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/ReturnBuilderTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ReturnBuilderTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly InMemoryReceiptStore store = new();
    private readonly SaleContext ctx;

    public ReturnBuilderTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("TV", "Television", "Household", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-TV", "TV", "PCS", M("899"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("999", "TV", null));
        ctx = new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6));
    }

    private Receipt SellMilk(int count)
    {
        var cart = new Cart(ctx);
        for (var i = 0; i < count; i++) cart.AddBarcode("111");
        var plan = new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card());
        return new SaleRecorder(store, 2, Modes, () => At.AddHours(-1)).CompleteSale(cart, plan, "cashier", "S1");
    }

    private ReturnBuilder Builder() => new(store, ctx, 2, Modes, () => At);

    [Fact]
    public void Returns_part_of_a_line_with_a_rounded_cash_refund()
    {
        var sale = SellMilk(2);

        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "cashier", "S2", null);

        Assert.Equal(ReceiptKind.Return, ret.Kind);
        Assert.Equal(sale.ClientId, ret.ReturnAgainst);
        Assert.Equal(-1m, Assert.Single(ret.Lines).Qty);
        Assert.Equal(M("-6.790"), ret.GrandTotal);
        Assert.Equal(M("-6.750"), ret.RoundedTotal);              // -6.79 % 0.25 → 0.21 > .125 → toward +∞ … ERPNext rule
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", M("-6.750")) }, ret.Payments);
        Assert.Equal(1m, Builder().Returnable(sale, 1));
    }

    [Fact]
    public void Second_return_can_only_take_what_is_left()
    {
        var sale = SellMilk(2);
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);

        Assert.Throws<InvalidOperationException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 2m)], TenderKind.Card, "c", "S2", null));
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.Equal(0m, Builder().Returnable(sale, 1));
    }

    [Fact]
    public void Card_refund_is_exact()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.False(ret.UsesErpRoundedTotal);
        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("-6.790")) }, ret.Payments);
    }

    [Fact]
    public void Refund_above_the_limit_needs_a_supervisor()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("999");
        var sale = new SaleRecorder(store, 2, Modes, () => At).CompleteSale(cart,
            new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card()), "c", "S1");

        Assert.Throws<ApprovalRequiredException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null));
        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", "SUP-1");
        Assert.Equal("SUP-1", ret.ApprovedBy);
    }

    [Fact]
    public void Return_without_receipt_always_needs_a_supervisor()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("111");

        Assert.Throws<ApprovalRequiredException>(() => Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", null));
        Assert.Single(cart.Lines);

        var ret = Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1");
        Assert.Null(ret.ReturnAgainst);
        Assert.Equal(-1m, ret.Lines[0].Qty);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Invalid_requests_are_refused()
    {
        var sale = SellMilk(1);
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [new ReturnLineRequest(1, 0m)], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [new ReturnLineRequest(9, 1m)], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, M("0.5")), new ReturnLineRequest(1, M("0.5"))], TenderKind.Card, "c", "S2", null));
    }

    [Fact]
    public void A_return_cannot_be_returned()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.Throws<InvalidOperationException>(() => Builder().Build(ret, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null));
    }
}
```

> Note for the implementer: `-6.79` rounded with Frappe's remainder rule: `-6.79 % 0.25` (Python sign = divisor) = `0.21`; `0.21 > 0.125` → value + (0.25 − 0.21) = `-6.75`. The test pins this ERPNext behaviour.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReturnBuilderTests`
Expected: build FAIL — `ReturnBuilder`, `ApprovalRequiredException` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Security/Security.cs` (this task's part):

```csharp
namespace TillPOS.Core.Security;

/// <summary>The action needs a supervisor's PIN (spec §13b.5); the caller shows the supervisor challenge and retries.</summary>
public sealed class ApprovalRequiredException(string reason) : Exception(reason);
```

`tillpos/src/TillPOS.Core/Sales/ReturnBuilder.cs`:

```csharp
using TillPOS.Core.Payments;
using TillPOS.Core.Security;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

public sealed record ReturnLineRequest(int LineNo, decimal Qty);

/// <summary>Builds and stores return receipts. Lines keep the original sale's line number and rate; quantities are negative.
/// A return without a receipt, or a refund above the limit, needs a supervisor.</summary>
public sealed class ReturnBuilder(IReceiptStore store, SaleContext ctx, int tillNumber, TenderModes modes, Func<DateTimeOffset> now,
    decimal approvalLimit = 50m)
{
    public decimal Returnable(Receipt original, int lineNo)
    {
        var sold = original.Lines.Single(l => l.LineNo == lineNo);
        var returned = store.ReturnsAgainst(original.ClientId).SelectMany(r => r.Lines).Where(l => l.LineNo == lineNo).Sum(l => -l.Qty);
        return sold.Qty - returned;
    }

    public Receipt Build(Receipt original, IReadOnlyList<ReturnLineRequest> requests, TenderKind refundKind, string cashier,
        string shiftClientId, string? approvedBy)
    {
        if (original.Kind != ReceiptKind.Sale) throw new InvalidOperationException("Only a sale can be returned.");
        if (requests.Count == 0 || requests.Any(r => r.Qty <= 0m))
            throw new ArgumentException("Choose at least one line and a quantity above zero.", nameof(requests));
        if (requests.Select(r => r.LineNo).Distinct().Count() != requests.Count)
            throw new ArgumentException("Each line can appear only once.", nameof(requests));

        var lines = new List<ReceiptLine>();
        foreach (var request in requests)
        {
            var sold = original.Lines.SingleOrDefault(l => l.LineNo == request.LineNo)
                ?? throw new ArgumentException($"Line {request.LineNo} is not on receipt {original.ClientId}.", nameof(requests));
            var left = Returnable(original, request.LineNo);
            if (request.Qty > left) throw new InvalidOperationException($"Only {left} of {sold.ItemName} can still be returned.");
            lines.Add(sold with { Qty = -request.Qty, Amount = 0m });
        }
        return Finish(lines, original.ClientId, refundKind, cashier, shiftClientId, approvedBy, alwaysNeedsApproval: false);
    }

    public Receipt BuildWithoutReceipt(Cart cart, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("Scan the items being returned first.");
        var lines = SaleRecorder.ToLines(cart, cart.Totals()).Select(l => l with { Qty = -l.Qty, Amount = 0m }).ToList();
        var receipt = Finish(lines, null, refundKind, cashier, shiftClientId, approvedBy, alwaysNeedsApproval: true);
        cart.Clear();
        return receipt;
    }

    private Receipt Finish(List<ReceiptLine> lines, string? returnAgainst, TenderKind refundKind, string cashier, string shiftClientId,
        string? approvedBy, bool alwaysNeedsApproval)
    {
        var totals = new TaxCalculator(ctx.Money, ctx.Catalog.FindItemTaxTemplate)
            .Calculate(lines.Select(l => new TaxLineInput(l.Qty, l.Rate, l.ItemTaxTemplate)).ToList(), ctx.TaxTemplate);
        lines = lines.Select((l, i) => l with { Amount = totals.Lines[i].Amount }).ToList();

        if (approvedBy is null)
        {
            if (alwaysNeedsApproval) throw new ApprovalRequiredException("A return without a receipt needs a supervisor.");
            if (-totals.GrandTotal > approvalLimit) throw new ApprovalRequiredException($"A refund above {approvalLimit} needs a supervisor.");
        }

        var plan = new PaymentCalculator(ctx.Money).PlanRefund(totals.GrandTotal, refundKind);
        var at = now();
        var receipt = new Receipt(
            ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Return, returnAgainst, shiftClientId, cashier, at,
            lines, totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : 0m,
            SaleRecorder.Payments(plan, modes), 0m, plan.RoundingDifference, approvedBy);
        store.Save(receipt);
        return receipt;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReturnBuilderTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): returns with and without receipt, supervisor limits"
```

---

### Task 6: Hold and recall bills

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/HeldCart.cs`
- Modify: `tillpos/src/TillPOS.Core/Sales/Cart.cs` (add `Snapshot`, `Restore`)
- Modify: `tillpos/src/TillPOS.Data/Migrations.cs` (append v3)
- Create: `tillpos/src/TillPOS.Data/HeldCartStore.cs`
- Test: `tillpos/tests/TillPOS.Tests/Data/HeldCartStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `Cart.Add` (private) and `CartLine.IsWeighed/Barcode`.
- Produces:
  - namespace `TillPOS.Core.Sales`: `record HeldLine(string ItemCode, string Uom, decimal Qty, string? Barcode, bool IsWeighed)`, `record HeldCart(string Id, string Label, DateTimeOffset HeldAt, IReadOnlyList<HeldLine> Lines)`; `Cart.Snapshot()`, `Cart.Restore(IEnumerable<HeldLine>)` returning `IReadOnlyList<(HeldLine Line, AddOutcome Outcome)>` of lines that could not be restored
  - namespace `TillPOS.Data`: `sealed class HeldCartStore(TillDb db)` — `HeldCart Hold(Cart cart, string label, DateTimeOffset at)`, `IReadOnlyList<HeldCart> List()`, `HeldCart? Take(string id)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Data/HeldCartStoreTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class HeldCartStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly HeldCartStore store;
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    public HeldCartStoreTests()
    {
        store = new HeldCartStore(temp.Db);
        catalog.Items.Add(new Item("MILK", "Milk", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
    }

    public void Dispose() => temp.Dispose();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m),
        "Standard Selling", "Stores - AAML", null, null, () => new DateOnly(2026, 10, 6)));

    [Fact]
    public void Hold_parks_the_bill_and_recall_restores_quantities_and_weights()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("111");
        cart.AddBarcode("2000089007400");

        var held = store.Hold(cart, "Customer in blue", At);

        Assert.Empty(cart.Lines);
        Assert.Equal("Customer in blue", Assert.Single(store.List()).Label);

        var taken = store.Take(held.Id)!;
        var fresh = NewCart();
        Assert.Empty(fresh.Restore(taken.Lines));
        Assert.Equal(2m, fresh.Lines[0].Qty);
        Assert.Equal(M("0.740"), fresh.Lines[1].Qty);
        Assert.True(fresh.Lines[1].IsWeighed);
        Assert.Empty(store.List());
        Assert.Null(store.Take(held.Id));
    }

    [Fact]
    public void Items_that_can_no_longer_be_sold_are_reported_on_recall()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var held = store.Hold(cart, "x", At);
        catalog.Prices.Clear();

        var failed = NewCart().Restore(store.Take(held.Id)!.Lines);

        Assert.Equal(AddOutcome.NoPrice, Assert.Single(failed).Outcome);
    }

    [Fact]
    public void Restore_needs_an_empty_cart()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        Assert.Throws<InvalidOperationException>(() => cart.Restore([new HeldLine("MILK", "PCS", 1m, null, false)]));
    }

    [Fact]
    public void An_empty_bill_cannot_be_held() =>
        Assert.Throws<InvalidOperationException>(() => store.Hold(NewCart(), "x", At));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter HeldCartStoreTests`
Expected: build FAIL — `HeldCartStore`, `Cart.Restore` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Sales/HeldCart.cs`:

```csharp
namespace TillPOS.Core.Sales;

public sealed record HeldLine(string ItemCode, string Uom, decimal Qty, string? Barcode, bool IsWeighed);

/// <summary>A parked bill (hold F5 / recall F7). Lines are re-priced when recalled.</summary>
public sealed record HeldCart(string Id, string Label, DateTimeOffset HeldAt, IReadOnlyList<HeldLine> Lines);
```

Add to `Cart` in `tillpos/src/TillPOS.Core/Sales/Cart.cs`:

```csharp
    public IReadOnlyList<HeldLine> Snapshot() =>
        lines.Select(l => new HeldLine(l.Item.ItemCode, l.Uom, l.Qty, l.Barcode, l.IsWeighed)).ToList();

    /// <summary>Re-adds held lines at today's prices into an empty cart; returns the lines that can no longer be sold.</summary>
    public IReadOnlyList<(HeldLine Line, AddOutcome Outcome)> Restore(IEnumerable<HeldLine> held)
    {
        if (lines.Count > 0) throw new InvalidOperationException("Finish or hold the current bill before recalling another.");
        var failed = new List<(HeldLine, AddOutcome)>();
        foreach (var h in held)
        {
            var result = Add(h.ItemCode, h.Uom, h.Barcode, h.IsWeighed ? h.Qty : null, null);
            if (result.Outcome != AddOutcome.Added) failed.Add((h, result.Outcome));
            else if (!h.IsWeighed) result.Line!.Qty = h.Qty;
        }
        return failed;
    }
```

Append to `Migrations.All`:

```csharp
        """
        CREATE TABLE held_cart (id TEXT PRIMARY KEY, label TEXT NOT NULL, held_at TEXT NOT NULL, json TEXT NOT NULL);
        """,
```

`tillpos/src/TillPOS.Data/HeldCartStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

public sealed class HeldCartStore(TillDb db)
{
    public HeldCart Hold(Cart cart, string label, DateTimeOffset at)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("There is nothing to hold.");
        var held = new HeldCart(Guid.NewGuid().ToString("N"), label, at, cart.Snapshot());
        using var c = db.Open();
        c.Exec(null, "INSERT INTO held_cart (id, label, held_at, json) VALUES (@id, @l, @at, @j)",
            ("@id", held.Id), ("@l", label), ("@at", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(held)));
        cart.Clear();
        return held;
    }

    public IReadOnlyList<HeldCart> List()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM held_cart ORDER BY held_at", r => JsonSerializer.Deserialize<HeldCart>(r.GetString(0))!);
    }

    /// <summary>Removes and returns a held bill, or null if another action already took it.</summary>
    public HeldCart? Take(string id)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var json = c.Scalar(tx, "SELECT json FROM held_cart WHERE id = @id", ("@id", id)) as string;
        if (json is null) return null;
        c.Exec(tx, "DELETE FROM held_cart WHERE id = @id", ("@id", id));
        tx.Commit();
        return JsonSerializer.Deserialize<HeldCart>(json);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter HeldCartStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat: hold and recall bills"
```

---

### Task 7: Shifts with blind count

**Files:**
- Create: `tillpos/src/TillPOS.Core/Shifts/ShiftCalculator.cs`
- Modify: `tillpos/src/TillPOS.Data/Migrations.cs` (append v4)
- Create: `tillpos/src/TillPOS.Data/ShiftStore.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/ShiftCalculatorTests.cs`, `tillpos/tests/TillPOS.Tests/Data/ShiftStoreTests.cs`

**Interfaces:**
- Consumes: `Receipt`, `ReceiptPayment`, `TenderModes`, `MoneySettings`, `Rounder`.
- Produces:
  - namespace `TillPOS.Core.Shifts`: `record ShiftOpening(string ClientId, string Cashier, DateTimeOffset OpenedAt, IReadOnlyList<ReceiptPayment> OpeningAmounts)`, `record ShiftModeSummary(string ModeOfPayment, decimal Opening, decimal Expected, decimal Counted, decimal Difference)`, `record ShiftClosing(string ShiftClientId, DateTimeOffset ClosedAt, IReadOnlyList<ShiftModeSummary> Modes, int Sales, int Returns, decimal GrandTotal, decimal NetTotal, decimal TotalTaxes)`, `static class ShiftCalculator` with `ShiftClosing Close(ShiftOpening opening, IReadOnlyList<Receipt> receipts, IReadOnlyDictionary<string, decimal> counted, TenderModes modes, DateTimeOffset closedAt, MoneySettings money)`
  - namespace `TillPOS.Data`: `sealed class ShiftStore(TillDb db)` — `void Open(ShiftOpening)`, `ShiftOpening? Current()`, `void Close(ShiftClosing)`, `(ShiftOpening Opening, ShiftClosing? Closing)? Get(string clientId)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/ShiftCalculatorTests.cs`:

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ShiftCalculatorTests
{
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 22, 0, 0, TimeSpan.FromHours(4));
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);

    private static Receipt R(ReceiptKind kind, decimal grand, decimal net, decimal change, params ReceiptPayment[] payments) => new(
        Guid.NewGuid().ToString(), kind, null, "S1", "c", At, [], grand, net, grand - net, grand, false, 0m, 0m, payments, change, 0m, null);

    [Fact]
    public void Expected_cash_is_opening_plus_cash_taken_minus_change_and_refunds()
    {
        var opening = new ShiftOpening("S1", "c", At.AddHours(-12), [new ReceiptPayment("Cash Counter 2", 200m)]);
        var receipts = new[]
        {
            R(ReceiptKind.Sale, M("14.37"), M("13.686"), M("5.75"), new ReceiptPayment("Cash Counter 2", 20m)),
            R(ReceiptKind.Sale, M("30.00"), M("28.571"), 0m, new ReceiptPayment("Credit Card", 30m)),
            R(ReceiptKind.Return, M("-10.13"), M("-9.648"), 0m, new ReceiptPayment("Cash Counter 2", M("-10.25"))),
        };

        var closing = ShiftCalculator.Close(opening, receipts,
            new Dictionary<string, decimal> { ["Cash Counter 2"] = M("203.50"), ["Credit Card"] = 30m }, Modes, At, Money);

        var cash = closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2");
        Assert.Equal(200m, cash.Opening);
        Assert.Equal(M("204.000"), cash.Expected);                 // 200 + 20 − 5.75 − 10.25
        Assert.Equal(M("-0.500"), cash.Difference);
        var card = closing.Modes.Single(m => m.ModeOfPayment == "Credit Card");
        Assert.Equal(30m, card.Expected);
        Assert.Equal(0m, card.Difference);
        Assert.Equal(2, closing.Sales);
        Assert.Equal(1, closing.Returns);
        Assert.Equal(M("34.240"), closing.GrandTotal);
    }

    [Fact]
    public void Uncounted_modes_count_as_zero()
    {
        var opening = new ShiftOpening("S1", "c", At, []);
        var closing = ShiftCalculator.Close(opening, [R(ReceiptKind.Sale, 10m, M("9.524"), 0m, new ReceiptPayment("Credit Card", 10m))],
            new Dictionary<string, decimal>(), Modes, At, Money);

        Assert.Equal(-10m, closing.Modes.Single(m => m.ModeOfPayment == "Credit Card").Difference);
        Assert.Contains(closing.Modes, m => m.ModeOfPayment == "Cash Counter 2" && m.Expected == 0m);
    }
}
```

`tillpos/tests/TillPOS.Tests/Data/ShiftStoreTests.cs`:

```csharp
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

public sealed class ShiftStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ShiftStore store;

    public ShiftStoreTests() => store = new ShiftStore(temp.Db);

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Open_then_close_a_shift()
    {
        var opening = new ShiftOpening("TILL2-SHIFT-20261006080000", "c", At, [new ReceiptPayment("Cash Counter 2", 200m)]);
        store.Open(opening);

        Assert.Equal(opening.ClientId, store.Current()!.ClientId);
        Assert.Equal(200m, store.Current()!.OpeningAmounts[0].Amount);

        var closing = new ShiftClosing(opening.ClientId, At.AddHours(12), [new ShiftModeSummary("Cash Counter 2", 200m, 210m, 210m, 0m)],
            5, 0, 10m, 9.524m, 0.476m);
        store.Close(closing);

        Assert.Null(store.Current());
        var (o, c) = store.Get(opening.ClientId)!.Value;
        Assert.Equal(opening.ClientId, o.ClientId);
        Assert.Equal(210m, c!.Modes[0].Counted);
    }

    [Fact]
    public void Only_one_shift_can_be_open()
    {
        store.Open(new ShiftOpening("A", "c", At, []));
        Assert.Throws<InvalidOperationException>(() => store.Open(new ShiftOpening("B", "c", At, [])));
    }

    [Fact]
    public void Closing_an_unknown_or_closed_shift_fails()
    {
        Assert.Throws<InvalidOperationException>(() =>
            store.Close(new ShiftClosing("X", At, [], 0, 0, 0m, 0m, 0m)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ShiftCalculatorTests|ShiftStoreTests"`
Expected: build FAIL — `TillPOS.Core.Shifts` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Shifts/ShiftCalculator.cs`:

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Sales;

namespace TillPOS.Core.Shifts;

public sealed record ShiftOpening(string ClientId, string Cashier, DateTimeOffset OpenedAt, IReadOnlyList<ReceiptPayment> OpeningAmounts);

public sealed record ShiftModeSummary(string ModeOfPayment, decimal Opening, decimal Expected, decimal Counted, decimal Difference);

public sealed record ShiftClosing(
    string ShiftClientId,
    DateTimeOffset ClosedAt,
    IReadOnlyList<ShiftModeSummary> Modes,
    int Sales,
    int Returns,
    decimal GrandTotal,
    decimal NetTotal,
    decimal TotalTaxes);

/// <summary>Blind count: the cashier enters counted amounts first; expected amounts and differences are worked out here
/// (expected = opening + payments taken − change given; refunds are negative payments).</summary>
public static class ShiftCalculator
{
    public static ShiftClosing Close(ShiftOpening opening, IReadOnlyList<Receipt> receipts, IReadOnlyDictionary<string, decimal> counted,
        TenderModes modes, DateTimeOffset closedAt, MoneySettings money)
    {
        var names = new List<string> { modes.Cash, modes.Card };
        foreach (var name in opening.OpeningAmounts.Select(p => p.ModeOfPayment)
                     .Concat(receipts.SelectMany(r => r.Payments).Select(p => p.ModeOfPayment))
                     .Concat(counted.Keys))
            if (!names.Contains(name)) names.Add(name);

        var summaries = names.Select(name =>
        {
            var start = opening.OpeningAmounts.Where(p => p.ModeOfPayment == name).Sum(p => p.Amount);
            var taken = receipts.SelectMany(r => r.Payments).Where(p => p.ModeOfPayment == name).Sum(p => p.Amount);
            var change = name == modes.Cash ? receipts.Sum(r => r.Change) : 0m;
            var expected = Rounder.Round(start + taken - change, money);
            var count = counted.TryGetValue(name, out var c) ? c : 0m;
            return new ShiftModeSummary(name, start, expected, count, Rounder.Round(count - expected, money));
        }).ToList();

        return new ShiftClosing(opening.ClientId, closedAt, summaries,
            receipts.Count(r => r.Kind == ReceiptKind.Sale), receipts.Count(r => r.Kind == ReceiptKind.Return),
            Rounder.Round(receipts.Sum(r => r.GrandTotal), money), Rounder.Round(receipts.Sum(r => r.NetTotal), money),
            Rounder.Round(receipts.Sum(r => r.TotalTaxes), money));
    }
}
```

Append to `Migrations.All`:

```csharp
        """
        CREATE TABLE shift (
            client_id TEXT PRIMARY KEY,
            opened_at TEXT NOT NULL,
            closed_at TEXT,
            opening_json TEXT NOT NULL,
            closing_json TEXT,
            sync_status TEXT NOT NULL DEFAULT 'Pending',
            erp_opening TEXT,
            erp_closing TEXT,
            last_error TEXT);
        """,
```

`tillpos/src/TillPOS.Data/ShiftStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Shifts;

namespace TillPOS.Data;

public sealed class ShiftStore(TillDb db)
{
    public void Open(ShiftOpening opening)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (c.Scalar(tx, "SELECT client_id FROM shift WHERE closed_at IS NULL") is string open)
            throw new InvalidOperationException($"Shift {open} is still open; close it first.");
        c.Exec(tx, "INSERT INTO shift (client_id, opened_at, opening_json) VALUES (@id, @at, @j)",
            ("@id", opening.ClientId), ("@at", opening.OpenedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(opening)));
        tx.Commit();
    }

    public ShiftOpening? Current()
    {
        using var c = db.Open();
        return c.Query("SELECT opening_json FROM shift WHERE closed_at IS NULL",
            r => JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!).FirstOrDefault();
    }

    public void Close(ShiftClosing closing)
    {
        using var c = db.Open();
        var changed = c.Exec(null, "UPDATE shift SET closed_at = @at, closing_json = @j WHERE client_id = @id AND closed_at IS NULL",
            ("@id", closing.ShiftClientId), ("@at", closing.ClosedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(closing)));
        if (changed == 0) throw new InvalidOperationException($"Shift {closing.ShiftClientId} is not open.");
    }

    public (ShiftOpening Opening, ShiftClosing? Closing)? Get(string clientId)
    {
        using var c = db.Open();
        var rows = c.Query("SELECT opening_json, closing_json FROM shift WHERE client_id = @id",
            r => (JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!,
                  r.IsDBNull(1) ? null : JsonSerializer.Deserialize<ShiftClosing>(r.GetString(1))),
            ("@id", clientId));
        return rows.Count == 0 ? null : rows[0];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ShiftCalculatorTests|ShiftStoreTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat: shifts with blind count"
```

---

### Task 8: Supervisor PINs, login and the approval log

**Files:**
- Modify: `tillpos/src/TillPOS.Core/Security/Security.cs` (add to Task 5's file)
- Modify: `tillpos/src/TillPOS.Data/Migrations.cs` (append v5)
- Create: `tillpos/src/TillPOS.Data/SecurityStores.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/SecurityTests.cs`, `tillpos/tests/TillPOS.Tests/Data/SecurityStoresTests.cs`

**Interfaces:**
- Produces:
  - namespace `TillPOS.Core.Security`: `enum ApprovalAction { LineVoid, BillVoid, ReturnWithoutReceipt, ReturnOverLimit, NoSaleDrawerOpen }`; `record Cashier(string Id, string Name, string? User, string PinHash, bool IsSupervisor, bool Enabled)`; `record ApprovalRecord(string Id, ApprovalAction Action, string SupervisorId, string? ReceiptClientId, string? ItemCode, decimal Amount, string? Reason, DateTimeOffset At)`; `static class PinHasher` (`bool IsValidPin(string)`, `string Hash(string)`, `bool Verify(string pin, string hash)`); `sealed class Authenticator(Func<IReadOnlyList<Cashier>> cashiers)` (`Cashier? Login(string pin)`, `Cashier? Supervisor(string pin)`)
  - namespace `TillPOS.Data`: `sealed class CashierStore(TillDb db)` (`void ReplaceAll(IEnumerable<Cashier>)`, `IReadOnlyList<Cashier> All()`); `sealed class ApprovalStore(TillDb db)` (`void Add(ApprovalRecord)`, `IReadOnlyList<ApprovalRecord> Unsynced()`, `void MarkSynced(IEnumerable<string> ids)`)

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/SecurityTests.cs`:

```csharp
using TillPOS.Core.Security;

namespace TillPOS.Tests.Core;

public class SecurityTests
{
    private static Cashier C(string id, string pin, bool supervisor = false, bool enabled = true) =>
        new(id, id, null, PinHasher.Hash(pin), supervisor, enabled);

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456", true)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    public void Pins_are_4_to_6_digits(string pin, bool valid) => Assert.Equal(valid, PinHasher.IsValidPin(pin));

    [Fact]
    public void Hash_is_salted_and_verifies()
    {
        var a = PinHasher.Hash("4321");
        var b = PinHasher.Hash("4321");
        Assert.NotEqual(a, b);
        Assert.True(PinHasher.Verify("4321", a));
        Assert.False(PinHasher.Verify("4322", a));
        Assert.DoesNotContain("4321", a);
    }

    [Fact]
    public void Login_finds_the_enabled_cashier_with_that_pin()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("ben", "2222"), C("old", "3333", enabled: false)]);
        Assert.Equal("ben", auth.Login("2222")!.Id);
        Assert.Null(auth.Login("3333"));
        Assert.Null(auth.Login("9999"));
    }

    [Fact]
    public void Only_supervisors_pass_the_supervisor_check()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("sup", "5555", supervisor: true)]);
        Assert.Null(auth.Supervisor("1111"));
        Assert.Equal("sup", auth.Supervisor("5555")!.Id);
    }

    [Fact]
    public void Duplicate_pin_logs_nobody_in()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("ben", "1111")]);
        Assert.Null(auth.Login("1111"));
    }

    [Fact]
    public void Invalid_pin_is_not_hashed() => Assert.Throws<ArgumentException>(() => PinHasher.Hash("12"));
}
```

`tillpos/tests/TillPOS.Tests/Data/SecurityStoresTests.cs`:

```csharp
using TillPOS.Core.Security;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

public sealed class SecurityStoresTests : IDisposable
{
    private readonly TempDb temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Cashier_list_is_replaced_as_a_whole()
    {
        var store = new CashierStore(temp.Db);
        store.ReplaceAll([new Cashier("a", "Ana", "a@x", "h1", false, true), new Cashier("b", "Ben", null, "h2", true, true)]);
        store.ReplaceAll([new Cashier("b", "Ben", null, "h2", true, false)]);

        var b = Assert.Single(store.All());
        Assert.Equal("b", b.Id);
        Assert.False(b.Enabled);
    }

    [Fact]
    public void Approvals_are_logged_until_uploaded()
    {
        var store = new ApprovalStore(temp.Db);
        var at = new DateTimeOffset(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
        store.Add(new ApprovalRecord("1", ApprovalAction.LineVoid, "sup", "TILL2-1", "MILK", 6.79m, null, at));
        store.Add(new ApprovalRecord("2", ApprovalAction.NoSaleDrawerOpen, "sup", null, null, 0m, "change for customer", at.AddMinutes(1)));

        Assert.Equal(new[] { "1", "2" }, store.Unsynced().Select(a => a.Id));
        Assert.Equal(ApprovalAction.LineVoid, store.Unsynced()[0].Action);

        store.MarkSynced(["1"]);
        Assert.Equal("2", Assert.Single(store.Unsynced()).Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "SecurityTests|SecurityStoresTests"`
Expected: build FAIL — `PinHasher`, `CashierStore` not found.

- [ ] **Step 3: Implement**

Replace `tillpos/src/TillPOS.Core/Security/Security.cs` with:

```csharp
using System.Globalization;
using System.Security.Cryptography;

namespace TillPOS.Core.Security;

/// <summary>The action needs a supervisor's PIN (spec §13b.5); the caller shows the supervisor challenge and retries.</summary>
public sealed class ApprovalRequiredException(string reason) : Exception(reason);

public enum ApprovalAction { LineVoid, BillVoid, ReturnWithoutReceipt, ReturnOverLimit, NoSaleDrawerOpen }

public sealed record Cashier(string Id, string Name, string? User, string PinHash, bool IsSupervisor, bool Enabled);

public sealed record ApprovalRecord(
    string Id,
    ApprovalAction Action,
    string SupervisorId,
    string? ReceiptClientId,
    string? ItemCode,
    decimal Amount,
    string? Reason,
    DateTimeOffset At);

/// <summary>Salted PBKDF2-SHA256 PIN hashes: "pbkdf2-sha256$iterations$salt$hash" (Base64). PINs are 4–6 digits.</summary>
public static class PinHasher
{
    private const int Iterations = 10_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool IsValidPin(string pin) => pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        if (!IsValidPin(pin)) throw new ArgumentException("A PIN is 4 to 6 digits.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>PIN login against the synced cashier list. A PIN shared by two enabled cashiers logs nobody in.</summary>
public sealed class Authenticator(Func<IReadOnlyList<Cashier>> cashiers)
{
    public Cashier? Login(string pin)
    {
        if (!PinHasher.IsValidPin(pin)) return null;
        var matches = cashiers().Where(c => c.Enabled && PinHasher.Verify(pin, c.PinHash)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public Cashier? Supervisor(string pin) => Login(pin) is { IsSupervisor: true } supervisor ? supervisor : null;
}
```

Append to `Migrations.All`:

```csharp
        """
        CREATE TABLE cashier (id TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE approval_log (id TEXT PRIMARY KEY, at TEXT NOT NULL, json TEXT NOT NULL, synced INTEGER NOT NULL DEFAULT 0);
        """,
```

`tillpos/src/TillPOS.Data/SecurityStores.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Security;

namespace TillPOS.Data;

public sealed class CashierStore(TillDb db)
{
    public void ReplaceAll(IEnumerable<Cashier> cashiers)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, "DELETE FROM cashier");
        foreach (var cashier in cashiers)
            c.Exec(tx, "INSERT INTO cashier (id, json) VALUES (@id, @j)", ("@id", cashier.Id), ("@j", JsonSerializer.Serialize(cashier)));
        tx.Commit();
    }

    public IReadOnlyList<Cashier> All()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM cashier ORDER BY id", r => JsonSerializer.Deserialize<Cashier>(r.GetString(0))!);
    }
}

/// <summary>Supervisor approvals, kept until Plan 2b uploads them.</summary>
public sealed class ApprovalStore(TillDb db)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public void Add(ApprovalRecord record)
    {
        using var c = db.Open();
        c.Exec(null, "INSERT INTO approval_log (id, at, json) VALUES (@id, @at, @j)",
            ("@id", record.Id), ("@at", record.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(record, Json)));
    }

    public IReadOnlyList<ApprovalRecord> Unsynced()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM approval_log WHERE synced = 0 ORDER BY at, id",
            r => JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(0), Json)!);
    }

    public void MarkSynced(IEnumerable<string> ids)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids) c.Exec(tx, "UPDATE approval_log SET synced = 1 WHERE id = @id", ("@id", id));
        tx.Commit();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "SecurityTests|SecurityStoresTests"`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat: cashier PIN login, supervisor check and approval log"
```

---

### Task 9: Cashier list download (POS Cashier feed)

**Files:**
- Create: `tillpos/src/TillPOS.Sync/Feeds/CashierFeed.cs`
- Test: `tillpos/tests/TillPOS.Tests/Sync/CashierFeedTests.cs`

**Interfaces:**
- Consumes: `SyncContext`, `ISyncFeed`, `ErpException`, `JsonFields`, Task 8 `CashierStore`, `Cashier`, `PinHasher`.
- Produces: `sealed class CashierFeed(SyncContext ctx, CashierStore cashiers) : ISyncFeed` (Name `"POS Cashier"`). Not added to `CatalogPuller.CreateDefault` yet — the doctype does not exist on live; Plan 3 wires it into the app.

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Sync/CashierFeedTests.cs`:

```csharp
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class CashierFeedTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CashierStore cashiers;
    private readonly CashierFeed feed;

    public CashierFeedTests()
    {
        cashiers = new CashierStore(temp.Db);
        var store = new CatalogStore(temp.Db);
        feed = new CashierFeed(new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Test Counter"), cashiers);
    }

    public void Dispose() => temp.Dispose();

    private void Row(string id, string? pin, bool supervisor = false, bool enabled = true) => erp.AddRow("POS Cashier", new()
    {
        ["name"] = id, ["cashier_name"] = id.ToUpperInvariant(), ["user"] = $"{id}@quickgroc.com", ["pin"] = pin,
        ["is_supervisor"] = supervisor ? 1 : 0, ["enabled"] = enabled ? 1 : 0,
    });

    [Fact]
    public async Task Downloads_cashiers_with_hashed_pins()
    {
        Row("simran", "1234");
        Row("sup", "987654", supervisor: true);

        Assert.Equal(2, await feed.RunAsync(default));

        var all = cashiers.All();
        Assert.All(all, c => Assert.DoesNotContain("1234", c.PinHash));
        var auth = new Authenticator(cashiers.All);
        Assert.Equal("simran", auth.Login("1234")!.Id);
        Assert.Equal("simran@quickgroc.com", auth.Login("1234")!.User);
        Assert.Equal("sup", auth.Supervisor("987654")!.Id);
    }

    [Fact]
    public async Task Cashiers_with_missing_or_invalid_pins_are_skipped()
    {
        Row("ok", "1234");
        Row("nopin", null);
        Row("short", "12");

        await feed.RunAsync(default);

        Assert.Equal("ok", Assert.Single(cashiers.All()).Id);
    }

    [Fact]
    public async Task Unchanged_pin_keeps_its_stored_hash()
    {
        Row("simran", "1234");
        await feed.RunAsync(default);
        var first = cashiers.All()[0].PinHash;

        await feed.RunAsync(default);

        Assert.Equal(first, cashiers.All()[0].PinHash);
    }

    [Fact]
    public async Task Missing_doctype_gives_a_clear_message_and_keeps_the_local_list()
    {
        cashiers.ReplaceAll([new Cashier("kept", "Kept", null, PinHasher.Hash("1234"), false, true)]);
        erp.Fail = q => q.Doctype == "POS Cashier" ? new ErpException(404, "DocType POS Cashier not found", "DoesNotExistError") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => feed.RunAsync(default));

        Assert.Contains("POS Cashier", ex.Message);
        Assert.Equal("kept", Assert.Single(cashiers.All()).Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CashierFeedTests`
Expected: build FAIL — `CashierFeed` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Sync/Feeds/CashierFeed.cs`:

```csharp
using System.Text.Json;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Full refresh of the POS Cashier list (small). PINs are hashed on the till; an unchanged PIN keeps its hash.
/// Cashiers without a valid 4–6 digit PIN are skipped.</summary>
public sealed class CashierFeed(SyncContext ctx, CashierStore cashiers) : ISyncFeed
{
    public string Name => "POS Cashier";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<JsonElement> rows;
        try
        {
            rows = await ctx.Erp.GetListAsync(new ListQuery("POS Cashier",
                ["name", "cashier_name", "user", "pin", "is_supervisor", "enabled"], [], "name asc", 0, 0), ct);
        }
        catch (ErpException ex) when (ex.StatusCode == 404 || ex.ExcType == "DoesNotExistError")
        {
            throw new InvalidOperationException("The 'POS Cashier' list is not set up in ERPNext yet (spec §4).", ex);
        }

        var existing = cashiers.All().ToDictionary(c => c.Id);
        var list = new List<Cashier>();
        foreach (var row in rows)
        {
            if (row.StrOrNull("pin") is not { } pin || !PinHasher.IsValidPin(pin)) continue;
            var id = row.Str("name");
            var hash = existing.TryGetValue(id, out var old) && PinHasher.Verify(pin, old.PinHash) ? old.PinHash : PinHasher.Hash(pin);
            list.Add(new Cashier(id, row.StrOrNull("cashier_name") ?? id, row.StrOrNull("user"), hash, row.Bool("is_supervisor"), row.Bool("enabled")));
        }
        cashiers.ReplaceAll(list);
        return list.Count;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CashierFeedTests`
Expected: PASS (4 tests). Then the full suite `dotnet test` — all pass, 0 warnings.

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(sync): POS Cashier download with hashed PINs"
```

---

## Self-review notes (completed while writing)

- **Spec coverage (Plan 2a scope, spec §13b):** scale labels + unit fallback (Task 1), payment rounding (Task 2), outbox receipts with client IDs (Tasks 3–4), returns with supervisor rules (Task 5), hold & recall (Task 6), shifts with blind count (Task 7), PINs/supervisors/approval log (Task 8), cashier download (Task 9). Uploading, POS Awesome shift documents, recent-receipt download, split-payment posting and approvals upload are Plan 2b. Price check, FTA QR and all screens are Plan 3.
- **Review Focus** items each have a named test in the owning task.
- Type names checked across tasks: `ReceiptLine`/`Receipt` constructor order is identical in Tasks 3, 4, 5, 7; `TenderModes`, `PaymentPlan.UsesErpRoundedTotal`, `SaleRecorder.ToLines/Payments`, `ApprovalRequiredException` (Task 5 file extended in Task 8).
- Migrations are appended in task order (v2 receipt, v3 held_cart, v4 shift, v5 cashier + approval_log); never edit an earlier entry.
