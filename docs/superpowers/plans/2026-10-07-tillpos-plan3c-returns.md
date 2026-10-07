# TillPOS Plan 3c: Returns (F6) with Cash Refund and Stock Back

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A customer can return items:
- against a receipt (whole bill or chosen items and quantities);
- without a receipt (supervisor PIN).

The till:
- pays the refund in cash from the drawer;
- prints a TAX CREDIT NOTE;
- records the return so the stock goes back into the shop warehouse when bills upload to ERPNext (Plan 2b: a return POS Invoice with `is_return=1`, `update_stock=1`, `return_against`).

**Architecture:**
- **Core:** `ReturnBuilder` already builds and stores returns (it has "remaining returnable" checks and supervisor rules). This plan adds a non-saving preview and the receipt-age rule.
- **Printing:** receipts get a scannable CODE128 barcode of the invoice number so a returned item's receipt can be scanned. The credit note shows the refund paid.
- **Presentation:** a new `ReturnViewModel` screen.
- **App:** the WPF view and F6.

**User decisions (2026-10-07):**
- **Refund method:** always cash (drawer opens). This is the existing `PaymentCalculator.PlanRefund(total, TenderKind.Cash)`: AED 0.25 rounding with ERPNext's rule, even when the original bill was paid by card.
- **No receipt:** allowed. It always needs a supervisor PIN (the existing rule) and refunds at today's price.
- **Stock:** returned items always go back to stock. There is no per-line condition choice. Locally nothing extra is stored: the return receipt is the record, and upload does the rest.
- **Time limit:** receipts older than **7 days** (by calendar date, till local time) can be returned only with a supervisor PIN.

## Global Constraints
- Plan 3a/3a.1/3b constraints hold (net10.0 libraries, net10.0-windows app only, 0 warnings, `0.00#` invariant money, no ERPNext writes, 48/32-column paper, Title fits half width, non-focusable pad buttons).
- **Supervisor approvals go through `SupervisorGate`** and are logged. Approval actions:
  - existing `ReturnWithoutReceipt` and `ReturnOverLimit`;
  - new `ReturnOldReceipt`, appended last.
  - One return can need several reasons (for example over limit and old receipt). Ask **once**, with a reason listing all of them, and log one approval row per reason with the same supervisor.
- **Money:** a return never changes the original sale receipt. Never refund more than was paid for a line; the existing `Returnable` check covers this. The return must be saved (FULL-sync commit) **before** the drawer opens and the credit note prints. A printer failure never loses the return.
- **Returns need an open shift and a logged-in cashier. The sale screen must be empty** ("Finish, hold or void the current bill first").
- **Hotkey:** F6 opens Returns from the sale screen. Esc goes back (before confirming). After confirming there is no way back except finishing the refund.
- **Version:** 0.3.5 / 0.3.5-test.

## Review Focus
1. **Double return:** returning the same line twice, or across two separate returns, can never exceed the quantity sold; the second attempt shows what is left. Two quick Confirm presses save one credit note.
2. **Approval bypass:**
   - a no-receipt return without the PIN is impossible;
   - the cumulative refund over AED 50 across several small returns on one receipt still asks;
   - an 8-day-old receipt asks;
   - a cashier PIN is refused and logged.
3. **Scale-label line:** returning a weighed line (e.g. 0.740 Kg) is allowed as the whole remaining weight or a smaller weight. Piece lines accept whole numbers only.
4. **Refund rounding:** the refund total −5.923 pays −6.00 or −5.75 according to the same rule as cash sales. The printed credit note and the drawer amount agree.
5. **Scanner on the Return screen:**
   - a receipt barcode finds the receipt;
   - an item barcode on the "with receipt" stage selects or increments that item's line instead of being added to the sale;
   - on the "without receipt" stage it adds the item to the return list.

---

### Task 1: Core + Printing

**Files:**
- `tillpos/src/TillPOS.Core/Sales/ReturnBuilder.cs`
- `tillpos/src/TillPOS.Core/Security/Security.cs`
- `tillpos/src/TillPOS.Data/ReceiptStore.cs`
- `tillpos/src/TillPOS.Printing/EscPos.cs`
- `tillpos/src/TillPOS.Printing/ReceiptRenderer.cs`
- tests

**`ReturnBuilder`**
- `public ReturnPreview Preview(Receipt original, IReadOnlyList<ReturnLineRequest> requests)` and `public ReturnPreview PreviewWithoutReceipt(Cart cart)`.
  - They run the same validation as `Build`/`BuildWithoutReceipt` (they throw the same exceptions for bad quantities) but **save nothing and don't touch the cart**.
  - `record ReturnPreview(decimal GrandTotal, decimal TotalTaxes, decimal RefundDue, decimal RoundingDifference, IReadOnlyList<ApprovalAction> Needs)`.
    - `RefundDue` is the rounded cash refund, a negative number, taken from `PlanRefund(…, Cash)`.
    - `Needs` lists the approvals required: `ReturnWithoutReceipt` (always for no receipt), `ReturnOverLimit` (cumulative > limit), and `ReturnOldReceipt` (see below).
  - Refactor so Preview and Build share one computation (no duplicated logic).
- **Receipt age:** add a constructor parameter `int maxAgeDays = 7`, appended last. A receipt is "old" when `DateOnly.FromDateTime(original.CreatedAt.LocalDateTime) < today − maxAgeDays`, where "today" comes from `ctx.Today()` (SaleContext has `Func<DateOnly> Today`; check the name).
  - `Build(... approvedBy ...)` throws `ApprovalRequiredException` for an old receipt without an approver, just like the limit.
  - `Build` and `BuildWithoutReceipt` get an optional `string? cashierName` parameter that sets `Receipt.CashierName`.
- **`ReceiptStore.RecentSales(int limit)`:** the newest-first `Kind == Sale` receipts, for the "recent bills" list. Also return the already-returned amount per receipt (sum of `ReturnsAgainst`), or let the view model ask `ReturnsAgainst`.
- **Security:** append `ApprovalAction.ReturnOldReceipt`.

**Printing**
- **`EscPos.Barcode128(string data)`:** CODE128 via `GS k 73 n {B…}` (code set B), height `GS h 60`, width `GS w 2`, human-readable text below with `GS H 2`. Data is printable ASCII only; throw otherwise.
- **`LineStyle.Barcode`:** a `PrintLine` whose Text is the barcode data.
  - `Layout` emits it centred after the invoice-number block, **for sales only**. The text file shows the plain number.
  - `EscPosBytes` prints it with `Barcode128`.
  - The preview popup shows the number in large monospace (no barcode image needed).
- **Credit notes** (`Kind == Return`):
  - the payment line reads "Refund paid (cash)" with the amount;
  - a `Reason: {reason}` line when one is set;
  - a "Return of: {original id}" line (it already exists), or "Return without receipt" when `ReturnAgainst` is null;
  - "Approved by: {ApprovedBy}" when set.
- **Tests:**
  - Preview equals Build totals and saves nothing;
  - the Needs combinations: limit, old receipt, both, none, and no-receipt;
  - an old receipt throws without an approver and passes with one;
  - CashierName is set on returns;
  - RecentSales returns sales only, newest first;
  - the CODE128 byte sequence for a known value, and a non-ASCII value is refused;
  - Layout puts a Barcode line on sales and none on returns;
  - credit-note wording ("TAX CREDIT NOTE", "Refund paid (cash)", the reason, "Return without receipt").

### Task 2: Presentation (`ReturnViewModel`)

`ReturnViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Action back)` is a screen with three stages.

**1. Find**
- `FindText` takes a typed or scanned receipt number (`FindCommand`).
- `RecentBills` lists today's and recent sales from `RecentSales(50)`: number, time, total, and "partly returned" when that applies. `OpenBillCommand(id)` opens one.
- `NoReceiptCommand` switches to stage 3b.
- An unknown number shows the message "Receipt {x} not found on this till. Ask a supervisor for a return without receipt." (Receipts from other tills arrive with Plan 2b.)
- Opening a receipt that is not a sale, or that has nothing left to return, shows a message.

**2. Choose (with receipt)**
- `Lines`: item, sold qty, already returned, returnable, rate, and a `ReturnQty` entry (`NumericEntry`; whole-only for piece lines). Use +/−/All commands per line.
- `ReturnAllCommand` sets every line to its returnable quantity.
- `Reason`: one of "Changed mind", "Damaged", "Expired", "Wrong item", "Other". Required before confirming.
- A live `Preview` (refund total, VAT, rounded refund due) and `NeedsText` such as "Needs supervisor: over AED 50 on this receipt; receipt older than 7 days". The preview catches validation errors and shows them as a message.
- Scanner `Scan(code)`: if the code matches a line's barcode or item code, increment that line by 1 (pieces) or set the full returnable amount (scale line); otherwise "That item is not on this receipt".

**3b. Without receipt**
- A return cart built from `ctx.NewSaleContext()`, a new Cart. `Scan(code)` adds to it, reusing Cart.AddBarcode, including scale labels.
- Lines can be removed with − / ✕ (no supervisor needed here; it's a return list).
- The same reason, preview and needs (always includes ReturnWithoutReceipt).

**Confirm**
- `ConfirmCommand` is refused when nothing is chosen or there is no reason.
- If `Needs` is not empty: one `gate.ApproveAsync(firstNeed, $"Return: {all reasons joined}", originalId, null, refundDue)`. On approval, log the remaining needs as extra approval rows with the same supervisor (`ctx.Approvals.Add`). On refusal, nothing is saved.
- Then `Build`/`BuildWithoutReceipt(… TenderKind.Cash, cashier.Id, shift.ClientId, approvedBy, reason, cashier.User, cashier.Name)`, which saves.
- Then `ctx.Output.Print(receipt, openDrawer: true)` inside try/catch, then `ctx.Dialogs.ShowReceipt(...)` (the existing popup; its reprint prints a COPY without the drawer), then `back()` to the sale screen with the message "Refund {amount} — credit note {id}".
- **Double-confirm guard:** a `completed` flag and CanExecute.
- Store `last_receipt` like sales do, so Ctrl+P reprints the credit note.

**Entry point**
- `SaleViewModel.Return()` (F6) is refused when the bill has lines; otherwise it navigates to a new ReturnViewModel. `back` returns to the same SaleViewModel.

**Tests (Presentation):**
- find by number, unknown number, recent list;
- choose partial/All and the whole-unit rule;
- scale line partial;
- a scanned item increments its line; a foreign item gives a message;
- reason required;
- needs combinations drive a single PIN prompt and log one row per reason;
- a cashier PIN is refused and nothing is saved;
- a second return on the same receipt shows only what is left, and the cumulative limit asks;
- no-receipt flow always asks;
- refund printed with `openDrawer: true` and the popup shown;
- a printer failure still saves;
- double Confirm saves once;
- F6 is refused with lines on the bill;
- Ctrl+P after a return reprints the credit note as a COPY.

### Task 3: App (WPF)
- **`Views/ReturnView.xaml(.cs)`**, registered in MainWindow:
  - Find stage: a large receipt-number box (focused on load), the recent bills list (time, number, total, a "partly returned" tag) and a "Return without receipt (supervisor)" button.
  - Choose stage: a lines table with sold / returned / can return / return qty with − + All, a reason selector (non-focusable buttons or a ComboBox), the refund panel (refund due large, VAT, "Needs supervisor: …" in warn colour), "Confirm refund (Enter)" and "Back (Esc)".
  - No-receipt stage: a scan box, the return list and the same panel.
  - Use the `Keyboarding` focus helper, select-all on tap, a ScrollViewer fallback, and fit 1366×768.
- **Scanner routing:** MainWindow currently routes scans only on the sale and login screens. Add the Return screen: route to `ReturnViewModel.Scan(code)` (find stage: as a receipt number; choose and no-receipt stages: as an item).
- **Sale screen:** a "Return (F6)" button and the F6 KeyBinding, placed in the button grid.
- **`ReceiptDialog`:** show a Barcode line as the number in large monospace.
- **Version:** 0.3.5 / 0.3.5-test (csproj and the publish-field.ps1 default).

### Task 4: Controller acceptance + exe
Run live, read-only, against a fresh scratch database. Sell 3 items with cash, then:
- F6 and scan the receipt number (from the receipt file);
- return 1 item with reason "Damaged";
- refund cash: the drawer flag is set and the credit note is printed;
- return the rest; return again (nothing left);
- a no-receipt return (supervisor);
- a return over AED 50 (supervisor);
- an old receipt, by changing the clock or using a test receipt (supervisor);
- a close-shift Z report showing returns and cash expected = float + sales − refunds.

Then build the 0.3.5 exe.
