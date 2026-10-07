# TillPOS Plan 3d: Counter Selection at Login

**Goal:** A cashier picks the counter they're working at when they open a shift. The shop has several counters in ERPNext, each a POS Profile ("Al Ain Counter 1", "Al Ain Counter 2", "Test Counter") with its own cash drawer account ("POS Cash Drawer 1 - AAML", "POS Cash Drawer 2 - AAML") and payment modes. A cashier may work half a day at one counter and then move to another, so the counter belongs to the **shift**, not to the PC or the cashier.

**User request (2026-10-07):** "while login, selection of counter required... sometimes cashier half day at 1 counter and other time on another. We also have counters/tills in ERPNext."

**Design:**
- **Settings** gain a `Counters` list: `[{ "PosProfile": "Al Ain Counter 1", "CashMode": "Cash Counter 1", "CardMode": "Credit Card", "Label": "Counter 1" }, …]`. When the list is empty, the existing single `PosProfile` / `CashMode` / `CardMode` settings act as the only counter, so current installs keep working. The settings screen (supervisor) lets the admin add, edit and remove counters.
- **Catalog sync** downloads POS settings for **every** configured counter (`pos_settings:{profile}` in kv), plus the company-wide data it already pulls. The catalog (items, prices, taxes) is shared; only the profile-level settings differ (warehouse, payment modes, rounded total, write-off limit).
- **Shift carries the counter:** `ShiftOpening` gets `Counter` (the POS Profile name). The opening float is counted for that counter's cash mode. `TILL{n}` client IDs stay per PC; the counter is a field.
- **Login flow:**
  1. PIN as today.
  2. If a shift is **open on this PC**, the cashier joins it; the counter is shown ("Counter 2 · shift open since 08:00") and cannot be changed until the shift is closed.
  3. If **no shift is open**, the Open Shift screen shows the counter choice first (big buttons, one per counter; preselect the last used), then the float.
- **While selling:** header shows the counter next to the till ("Till 2 · Counter 1"). Receipts print the counter name under the till line. `SaleContext`, `TenderModes` (cash/card mode names), rounding (`DisableRoundedTotal`), warehouse and write-off limit all come from the shift's counter.
- **Close shift:** the Z report names the counter; expected cash is per that counter's cash mode.
- **Upload (Plan 2b):** `pos_profile` on POS Opening Shift / Closing Shift / POS Invoice = the shift's counter. Nothing else changes.

**Constraints:** all earlier Global Constraints hold (0 warnings, tests green, no ERPNext writes, 1366×768, non-focusable pad buttons). Existing shift rows without a counter are read as the default counter.

**Tasks**
1. **Core/Data:** `ShiftOpening.Counter` (positional, after `Cashier`; JSON compatible: missing → `""`), `ShiftStore` migration-free (JSON), `CatalogStore.LoadPosSettings(profile)` + `SavePosSettings(profile, …)` with the old single-key call mapped to the default counter; `TenderModes` derived from a counter's settings (`CounterSettings` record: PosProfile, Label, CashMode, CardMode). Tests.
2. **Sync:** `PosProfileFeed` loops over the configured counters (SyncContext gets `IReadOnlyList<string> Profiles`), storing one `pos_settings:{profile}` each; the default profile also stays under the legacy key. Tests with FakeErp.
3. **Presentation:** `TillContext` gains `Counters` (list of CounterSettings) and `Func<string, SaleContext> NewSaleContextFor(profile)`; `SessionState.Counter`; `OpenShiftViewModel` shows counters (`Counters`, `SelectedCounter`, `SelectCounterCommand`) and stores the choice on the shift; `LoginViewModel` sets `SessionState.Counter` from the open shift; `SaleViewModel`/`PaymentViewModel`/`ReturnViewModel`/`CloseShiftViewModel` take modes and sale context from the session's counter. `ShellViewModel.CounterName` for the header. Tests: open shift with counter B uses B's cash mode on the float and the receipts; joining an open shift fixes the counter; a receipt's cash payment uses the counter's cash mode name.
4. **Printing:** "Counter : {label}" line under "Till" on receipts and the Z report.
5. **App:** settings schema + Settings screen editor (simple list with add/remove rows: profile, label, cash mode, card mode); Open Shift view counter buttons; header; `AppHost` wiring (settings → counters → SyncContext profiles). Version 0.3.6.
6. **Acceptance:** two counters configured ("Test Counter" plus a copy with the Counter-2 cash mode name), open shift at Counter 2, sell cash, Z report shows Counter 2 and its cash mode.
