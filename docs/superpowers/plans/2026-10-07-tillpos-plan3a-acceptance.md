# TillPOS Plan 3a — manual acceptance results

**Date:** 2026-10-07 · **Machine:** developer laptop (Windows 11) · **ERPNext:** live `erp.quickgroc.com`, read-only (catalog download only; bills stayed in the local outbox) · **Profile:** Test Counter · **Till number:** 9 · **Printer:** none (receipts written as text files).

The checks were driven through Windows UI Automation (button clicks, keyboard and scanner-speed input sent to the real app), not by hand.

| # | Check | Result | Notes |
|---|---|---|---|
| M1 | First start | ✅ Pass | Catalog downloaded and the PIN screen appeared in about 20 s. The header shows the company name and Till 9. |
| M2 | Log in, open shift | ✅ Pass | A wrong PIN shows "Wrong PIN." PIN 1234 logs in as Test Cashier. A float of 200 typed on the keyboard opens the shift. |
| M3 | Scan a real barcode | ✅ Pass | `6291030202043` (Al Rawabi skim 500 ml) scanned twice merged into one line, qty 2 × 3.333. The total includes VAT. |
| M4 | Scan while the search box has focus | ✅ Pass | Typed "rawabi" (results shown), then scanned `6291030202029`. The item was added and the search box kept "rawabi" with no digits. |
| M5 | Cucumber scale label `2000089007400` | ✅ Pass | 0.740 Kg × 3.50 = 2.59 |
| M6 | + on the scale-label line | ✅ Pass | Refused: "Lines from a scale label take their quantity from the label." |
| M7 | ✕ with the cashier PIN, then the supervisor PIN | ✅ Pass | 1234 gives "That is not a supervisor PIN." and the line stays. 9876 removes it. The approval log has a FailedSupervisorPin row and a LineVoid row with cashier, supervisor, shift and item. |
| M8 | Kill the process mid-bill and restart | ✅ Pass | "Unfinished bill restored." Same 2 lines, total 14.019. The shift was still open. |
| M9 | Cash payment with quick cash 50 | ✅ Pass | Bill total 14.019; amount due 14.00 (rounded to AED 0.25); change 36.00. The receipt file shows TAX INVOICE, lines, VAT 0.668, rounding −0.019 and change. **There was no TRN or address on the receipt**, see below. |
| M10 | Card payment | ✅ Pass | Charged exactly 11.429 with no rounding. |
| M11 | Header "waiting" count | ✅ Pass | Showed "2 waiting" after two bills. |
| M12 | Network off for 2 minutes | ⏳ Not run | Turning the laptop's network off would also cut the automation session. **Run this by hand on a till.** |
| M13 | Scan into the payment cash box | ❌ → ✅ | **Bug found:** with the caret at the start of the box, the scan's digits were inserted before "50", giving 6291050. The scan's Enter was swallowed, so the sale did not complete. **Fixed in 70dfabb:** the box is now restored to its text from before the scan. Re-run: the box keeps "50". |
| M14 | Enter twice quickly on the payment screen | ✅ Pass | One receipt saved and no error. |

No `errors.log` was written during the run.

## Findings for the shop / ERPNext setup

1. **TRN and shop address are missing on receipts.**
   - The till user gets no Tax ID from the Company "AL AIN MARKETING L.L.C".
   - No Address is marked "Is Your Company Address", and the Test Counter POS Profile has no Company Address.
   - Without a TRN the receipt is not a valid UAE tax invoice and has no QR code.
   - **Fix one of these:** fill in Company → Tax ID and set a company address on the POS Profile in ERPNext, or put `"Trn"` and `"ShopAddress"` in the till's settings.json. The settings are only used when ERPNext has none.
2. **"1 sync problem" in the header is expected for now.** The `POS Cashier` doctype does not exist on live yet (HTTP 404), so the till uses the `LocalTestCashiers` from settings. It goes away once POS Cashier is created (spec §4).
3. **Prices such as 3.333 / 11.429** are what ERPNext returns for "Standard Selling", and the engine matched 40/40 live invoices earlier. If the shelf prices are meant to be 3.50 / 12.00, the Item Prices in ERPNext hold ex-VAT amounts while the POS Profile is VAT-inclusive. This is a data question for the shop, not a till change.

## Still to check by hand on a real till

- M12: network off, then on again. The header should go Offline, then back to Online, and selling should keep working.
- Typing the PIN on a physical keyboard on the login screen. On-screen buttons work; the automation could not exercise keyboard focus reliably on that screen.
- A real thermal printer: set `"PrinterName"` to the Windows printer name. Check the QR code prints once a TRN exists, and that the drawer opens on cash only.
