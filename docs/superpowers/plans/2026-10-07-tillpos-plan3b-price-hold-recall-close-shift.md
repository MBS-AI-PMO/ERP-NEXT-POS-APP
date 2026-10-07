# TillPOS Plan 3b: Price Check, Hold/Recall, Void Shortcut, Close Shift, Log Out, Reprint

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Finish the cashier's daily features:
- price check (F4);
- hold the current bill (F5) and recall it later (F7);
- a keyboard shortcut for the existing on-screen bill void (F8);
- close shift with a blind cash count, a variance check and a printed shift (Z) report;
- log out without closing the shift;
- reprint the last receipt as a COPY (Ctrl+P).

**Architecture:**
- Behaviour lives in UI-free view models in `TillPOS.Presentation` (unit-tested). Printed layouts live in `TillPOS.Printing` (unit-tested). `TillPOS.App` holds only WPF views and dialogs.
- Existing foundations: `HeldCartStore` (Hold/List/Take), `Cart.Snapshot/Restore`, `ShiftCalculator.Close` (blind count, expected vs counted per mode), `ShiftStore.Open/Current/Close`, `SupervisorGate`, the `PrintLine` layout and ESC/POS pipeline, and `IReceiptOutput`.

**Spec:** `docs/superpowers/specs/2026-10-05-tillpos-offline-pos-design.md` §8 (screens), §13b (shifts, approvals). Master Spec hotkeys: F4 price check, F5 hold, F7 recall.

**User decisions (2026-10-07):**
- "Void Bill" means the on-screen bill only. It already exists behind a supervisor PIN; add the F8 shortcut and nothing else. Returns of paid bills are not in this plan.
- Close Shift is a blind count, then the result. The cashier enters the counted cash (and the card-machine total) without seeing the expected amounts. The till then shows expected, counted and difference per payment mode, plus bill count, totals and VAT, and prints a shift (Z) report. A cash difference over **AED 5.00** (absolute) needs a supervisor PIN before the shift can close.

## Global Constraints
- Everything in Plan 3a/3a.1's constraints still holds (net10.0 libraries, net10.0-windows app only, 0 warnings, `0.00#` money with invariant culture, no ERPNext writes, paper 48/32 columns, nothing wider than the paper, Title lines fit half the width).
- **Supervisor approvals** use `SupervisorGate`; every approval and every failed PIN is logged. New `ApprovalAction` values are appended last: `HeldBillDelete`, `ShiftVariance`.
- **Hotkeys on the sale screen:** F2 search, F3 qty, **F4 price check**, **F5 hold**, **F7 recall**, **F8 void bill**, F11 card, F12 cash, Delete remove line, **Ctrl+P reprint last**, Esc back to the scan box. Every new action also has an on-screen button, non-focusable like the other pad buttons.
- **Money path untouched:** none of these features may save, alter or lose a completed bill. Reprint never opens the drawer.
- **Version:** 0.3.4 / InformationalVersion 0.3.4-test (csproj and the publish-field.ps1 default).

## Review Focus
1. **Recall onto a non-empty bill:** recalling while items are on the screen must be refused. The current bill is never overwritten or merged.
2. **Close shift with work outstanding:** closing with items on the bill, or with held bills, is refused with a clear message. Held bills never disappear silently.
3. **Blind count:** the expected cash is never shown, or computable from the screen, before the counted amount is confirmed.
4. **Variance gate:** a cash difference of exactly 5.00 closes without a supervisor; 5.01 (over or short) needs one. Card differences are reported but not gated.
5. **Price check:** scanning in the price-check window shows the price and never adds the item to the bill unless "Add to bill" is pressed. A scale label shows weight × price = amount.
6. **Reprint:** Ctrl+P after a restart still finds the last receipt, prints "*** COPY ***", and never kicks the drawer.

---

### Task 1: Price check, hold, recall, reprint (Presentation + Printing)

**Files:**
- Presentation: `PriceCheckViewModel.cs`, `HeldBillsViewModel.cs`, changes to `SaleViewModel.cs`, `Abstractions.cs` (IDialogs, IReceiptOutput) and the tests' `Fakes.cs`.
- Printing: `ReceiptRenderer.cs` (copy flag).
- Core: `Security.cs` (append `HeldBillDelete`, `ShiftVariance`).

**Price check**
- `PriceCheckViewModel(TillContext ctx)` has `Lookup(string code)`, `SearchText` (≥2 characters searches through `ctx.Search`), `SearchResults` and `Select(string itemCode)`.
- The result properties are:
  - `HasResult`, `Name`, `ItemCode`, `Barcode`, `UnitText` (e.g. "per Kg", "per PCS"), `PriceText` (list price incl. VAT);
  - `OfferText` (e.g. "Offer: 2.50 (Rule label)" when the effective rate is below the list price), `ScaleText` ("0.740 Kg × 3.50 = 2.59" for a scale label);
  - `Message` (e.g. "Unknown barcode 123", "No price for this item").
- Implement it by adding the code or item to a throw-away `Cart` built from `ctx.NewSaleContext()`. That reuses the exact pricing, tax, UOM-fallback and scale-label logic. Never touch the sale's cart.
- `AddToBillCode` (string?) holds what "Add to bill" would add: the scanned code, or the item code for a search pick.
- `SaleViewModel.PriceCheckAsync()` calls the new `IDialogs.ShowPriceCheck(PriceCheckViewModel vm)`, which returns `string?` (a code or item code to add, or null). When it returns a value, it calls `Scan(code)`, or `AddFromSearch(itemCode)` when the value came from a search pick. Tell them apart by a prefix or a small result record of your choice.

**Hold (F5)**
- `SaleViewModel.Hold()`:
  - Refused with a message when the bill is empty.
  - Refused when 20 bills are already held ("20 bills are already on hold — recall or delete one first").
  - Otherwise `ctx.Held.Hold(Cart, label, now)`, where the label is `"{HH:mm} · {cashier name} · {n} items · {total}"`. Then clear the autosave, refresh, and show "Bill put on hold".
  - Add `HeldCartStore Held` to `TillContext` (new last parameter), and update AppHost and the test fixture.
- `SaleViewModel.HeldCount` is refreshed after hold and recall (for a badge such as "Recall (2)").

**Recall (F7)**
- `HeldBillsViewModel(TillContext ctx, SessionState session, SupervisorGate gate)` has `Bills` (Id, Label, HeldAt text) and `Selected`.
- `SaleViewModel.RecallAsync()` refuses when the current bill has lines ("Finish or hold the current bill first"), and says "No bills on hold" when there are none. Otherwise it calls `IDialogs.ShowHeldBills(HeldBillsViewModel vm)`, which returns the chosen Id or null. Then:
  - `ctx.Held.Take(id)`; if it returns null, show "That bill was already recalled".
  - `Cart.Restore(lines)`; when some lines fail, show "{n} item(s) on the held bill can no longer be sold" as an error. The rest are restored.
  - Save the autosave and refresh.
- **Delete held bill:** `HeldBillsViewModel.DeleteSelectedAsync()` asks the supervisor through the gate with `ApprovalAction.HeldBillDelete`, reason "Delete held bill {label}" and the amount parsed from nothing (0), then calls `Take(id)` and discards it.

**Void (F8):** no logic change. The existing `VoidBillCommand` gets the F8 binding in Task 3.

**Reprint last (Ctrl+P)**
- After every completed sale, `SaleViewModel.SaleCompleted` stores the receipt id in kv `"last_receipt"`.
- `ReprintLastCommand` loads it with `ctx.Receipts.Get(id)` and calls `ctx.Output.Print(receipt, openDrawer: false, copy: true)`. The message is "Reprinted {id}"; on failure it is an error, and the exception is caught. With no last receipt the message is "No receipt to reprint yet".
- `IReceiptOutput.Print` gains a `bool copy = false` parameter, and FakeOutput records it.
- `ReceiptRenderer.Layout` gains a `bool copy = false` parameter. When true it adds a centred Bold line `*** COPY ***` right after the title block. Pass it through `TextLines` and `EscPosBytes` too.
- The receipt popup's "Print again" also prints as a copy.

**Tests (Presentation, Printing):**
- **Price check:**
  - lookup by barcode shows the price, and the bill stays unchanged;
  - a scale label shows the ScaleText;
  - an unknown code gives a message;
  - search and select work;
  - "Add to bill" adds through the sale.
- **Hold:**
  - an empty bill is refused;
  - hold clears the bill and writes the label;
  - the 21st hold is refused;
  - the autosave is cleared.
- **Recall:**
  - refused on a non-empty bill (Review Focus 1);
  - restores the lines;
  - reports lines that can no longer be sold;
  - "already recalled" when Take returns null;
  - delete needs a supervisor (a cashier PIN is refused and logged, a supervisor deletes and `HeldBillDelete` is logged).
- **Reprint:**
  - prints the last receipt with copy=true and no drawer;
  - works with a new SaleViewModel, as after a restart (Review Focus 6);
  - no receipt gives the message.
- **Renderer:** `*** COPY ***` appears only when copy is set.

### Task 2: Close shift, shift report, log out (Presentation + Printing)

**Files:**
- Presentation: `CloseShiftViewModel.cs`, changes to `SaleViewModel.cs` (`CloseShift`, `LogOut`) and `Abstractions.cs`.
- Printing: `ShiftReportRenderer.cs`.
- App interface: `IReceiptOutput.PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy)`.

**Starting a close (`SaleViewModel.CloseShift()`)**
- Refused with "Finish, hold or void the current bill first" when the bill has lines.
- Refused with "{n} bill(s) are on hold — recall or delete them before closing the shift" when held bills exist.
- Otherwise it navigates to `new CloseShiftViewModel(ctx, session, gate, back: () => navigator.Show(sale), done: () => navigator.Show(login))`. Wiring: SaleViewModel gets a `Func<object> newLogin` (constructor, last parameter). Update AppHost and the tests.

**`CloseShiftViewModel` stage 1: Count (blind)**
- `Denominations`: AED notes and coins 500, 200, 100, 50, 20, 10, 5, 1, 0.50, 0.25. Each has a `NumericEntry` count (whole numbers only; reject fractions) and a line total.
- `CashTotal` = sum of count × denomination. `UseTotalInstead`: a single `NumericEntry` for counted cash. If it is filled, it overrides the denominations.
- `CardTotal`: a `NumericEntry` for the card machine settlement total (blank = 0).
- Commands: `ConfirmCountCommand`, `BackCommand`.
- **Nothing in stage 1 may expose expected amounts** (Review Focus 3). Do not compute them until ConfirmCount.

**Stage 2: Result**
- On ConfirmCount, run `ShiftCalculator.Close(opening, ctx.Receipts.ByShift(opening.ClientId), counted, ctx.Modes, now, money)` with `counted = { [Modes.Cash] = cash, [Modes.Card] = card }`.
- Expose `Rows` (mode, expected, counted, difference, as text), `SalesCount`, `GrandTotalText`, `VatText`, `CashDifference`.
- `NeedsSupervisor` = |cash difference| > 5.00m (5.00 exactly is allowed).
- `CloseCommand`:
  - When `NeedsSupervisor`, it first calls `gate.ApproveAsync(ApprovalAction.ShiftVariance, $"Cash difference {diff}", null, null, diff)`; a refusal aborts.
  - Then `ctx.Shifts.Close(closing)`.
  - It prints `ctx.Output.PrintShiftReport(...)`; a printer failure is reported as a message but the shift is still closed.
  - It clears `session.Shift` and `session.Cashier`, then calls `done()`, which goes to the login screen.
- `RecountCommand` goes back to stage 1, keeping the entries. There is no Back after a successful close.

**Log out (`SaleViewModel.LogOut()`)**
- Refused when the bill has lines ("Finish, hold or void the current bill first").
- Otherwise it clears `session.Cashier`, keeps the shift open, and goes to the login screen.
- The next cashier logs in onto the same open shift. That is the current LoginViewModel behaviour; verify it.

**`ShiftReportRenderer.Layout(ShiftOpening, ShiftClosing, ReceiptHeader, string cashierName, string? approvedBy, PaperWidth)`** returns `IReadOnlyList<PrintLine>`, laid out as:

```
             AL AIN MARKETING L.L.C   (Bold)
                SHIFT REPORT (Z)      (Title)
Shift   : TILL1-SHIFT-20261007080000
Till    : Till 1
Cashier : Test Cashier
Opened  : 07/10/2026 08:00
Closed  : 07/10/2026 16:05
------------------------------------------------
Bills (sales)                                 42
Total incl. VAT                         1,234.50
VAT                                        58.79
------------------------------------------------
Mode            Expected   Counted   Difference
Cash Counter 2    850.25    850.00        -0.25
Credit Card       584.25    584.25         0.00
------------------------------------------------
Variance approved by: supervisor1      (only when approvedBy)
Signature: ____________________
```

- Use the existing column helpers, or write equivalents. At 58 mm, each mode takes two lines: the mode name, then expected / counted / difference.
- The ESC/POS bytes are produced through the same styled-line printing as receipts. Extract a shared helper from `ReceiptRenderer.EscPosBytes` if needed (keep its behaviour). There is no drawer kick.

**Tests:**
- **Close shift:** refused with lines on the bill, and refused with held bills (Review Focus 2).
- **Blind stage:** no expected value is exposed. Assert by reflection that no public property of the stage-1 state contains the expected cash, or structure the type so stage-1 properties can't. At least assert `Rows` is empty before ConfirmCount.
- **Denominations:** 2×500 + 3×0.25 = 1000.75; fractional counts rejected; the total override wins.
- **Variance gate (Review Focus 4):**
  - exactly 5.00 short closes without a PIN request;
  - 5.01 short asks for a PIN; a refusal leaves the shift open; a supervisor approval closes it and logs `ShiftVariance`;
  - an over amount behaves the same.
- **Close:** the closing is stored; the report is printed with approvedBy; the session is cleared; the login screen is shown; a printer failure still closes.
- **Log out:** keeps the shift; refused with lines.
- **Shift report:** every line is within the width at 48 and 32 columns; it contains the mode rows and totals; the approved-by line appears only with an approver.

### Task 3: WPF views, dialogs, buttons, hotkeys; version 0.3.4 (App)

- **`Dialogs/PriceCheckDialog`:**
  - a large result card (name, price large, unit, offer in warn colour, scale text);
  - a scan box (focused), with scanner bursts detected through `ScanBuffer`; a scan looks the item up and never closes the dialog;
  - a search box with a results list;
  - buttons "Add to bill" (enabled when there is a result) and "Close (Esc)".
- **`Dialogs/HeldBillsDialog`:** a list of held bills (label, time); "Recall (Enter)", "Delete (supervisor)", "Close (Esc)". A double-click recalls.
- **`Views/CloseShiftView`:**
  - stage 1: the denominations grid, the cash-total override, the card-machine total, the live counted total, "Confirm count" and Back;
  - stage 2: the results table with the difference in red when it is non-zero, a supervisor notice when needed, "Close shift & print report" and "Recount".
  - Register the DataTemplate.
- **Sale screen buttons and hotkeys:**
  - Add buttons for Price check (F4), Hold (F5), Recall (F7, showing the held count) and Void bill (F8, the existing command), plus a small row "Reprint last (Ctrl+P)", "Log out" and "Close shift". All are non-focusable.
  - Hotkeys go through the SaleView PreviewKeyDown or InputBindings, matching how F2/F3/F11/F12 are wired today.
  - Keep the layout usable at 1366×768. If the right column gets crowded, use a two-column button grid.
- **`WpfDialogs`** implements `ShowPriceCheck` and `ShowHeldBills`. `ReceiptOutput` implements `PrintShiftReport` (raw printer, or a text file named `SHIFT-{clientId}.txt` in the receipts folder when there is no printer) and the copy flag.
- **Version:** 0.3.4 / 0.3.4-test in the csproj, and the default in `publish-field.ps1`.

### Task 4: Controller acceptance + test exe
Run the single exe against live ERPNext, read-only, with a scratch settings path. Check:
- price check: barcode and scale label;
- hold two bills, recall one;
- try recall onto a non-empty bill (refused);
- delete a held bill with the supervisor PIN;
- F8 void;
- Ctrl+P after a restart;
- log out and log back in onto the same shift;
- close shift refused with a held bill;
- blind count: a 5.01 difference needs the supervisor; then close, and check the report file.

Record the results here, then build the 0.3.4 test exe.
