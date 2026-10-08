# ERPNext setup for TillPOS (admin checklist)

Do these in ERPNext through the web UI. Do them first on the **dev sandbox** (`https://dev.quickgroc.com/`) for the sync test, then the same on production before cutover. Nothing here changes how POS Awesome works.

## 1. A user and API key per till PC

For each till PC (Till 1, Till 2, …):

1. **User:** `till1@quickgroc.com` (name "Till 1"), with **Enabled** and **API Access** ticked.
2. **Roles:** `TillPOS Device` (created in step 2 below) plus `Accounts User`, `Sales User` and `Stock User` (ERPNext grants create/submit on POS Invoice and stock postings through these; found during the sandbox test — System Manager alone cannot create a POS Invoice). No System Manager.
3. **API key:** in the user's settings, choose **Generate Keys**. Copy the API key and secret into that till's `settings.json` (`ApiKey`, `ApiSecret`). The till encrypts the secret on first start.
4. **Default values:** Company "AL AIN MARKETING L.L.C".

For the **sandbox test**, the developer user `babar@quickgroc.com` can be used instead. It currently lacks read on POS Profile, POS Invoice and Mode of Payment, so give it `TillPOS Device` too.

## 2. Role "TillPOS Device"

Role Permissions Manager → add the role with:

| DocType | Read | Create | Submit | Notes |
|---|---|---|---|---|
| Item, Item Barcode, Item Price, Item Group, Brand, UOM, UOM Conversion Detail, Item Tax Template, Pricing Rule | ✓ | | | catalog |
| POS Profile, Sales Taxes and Charges Template, Company, Currency, Address, Mode of Payment, Deleted Document | ✓ | | | settings |
| POS Cashier | ✓ (level 0 **and** level 1) | | | PINs are at level 1 |
| POS Invoice (+ POS Invoice Item, Sales Taxes and Charges, Sales Invoice Payment) | ✓ | ✓ | ✓ | bills and returns |
| POS Opening Shift (+ POS Opening Shift Detail) | ✓ | ✓ | ✓ | |
| POS Closing Shift (+ its child tables) | ✓ | ✓ | ✓ | |
| TillPOS Approval | ✓ | ✓ | | step 5 |
| Customer | ✓ | | | the walk-in customer |

## 3. One POS Profile per counter

The till already lists counters from its settings ("Al Ain Counter 1", "Al Ain Counter 2", "Test Counter"). Each profile needs:

- **Company**, **Warehouse** (Stores - AAML), **Selling Price List** "Standard Selling", **Taxes and Charges** "UAE VAT 5% - AAML".
- **Payments:** the counter's cash mode (e.g. "Cash Counter 1" → account "POS Cash Drawer 1 - AAML") set as **default**, plus "Credit Card".
- **Disable Rounded Total:** the till ignores this setting, as POS Awesome does: cash and split bills are rounded to AED 0.25 and card bills are charged the exact amount, whatever the profile says (all three profiles have it ticked today).
- **Write Off Limit:** 0.05, with the write-off account and cost centre set, so a rounding difference of a few fils never blocks an upload.
- **Account for Change Amount:** the counter's cash account.
- **Customer:** the walk-in customer.
- **Applicable for Users:** the till users that may use this counter.

## 4. Custom fields (Customize Form)

| DocType | Field | Type | Options |
|---|---|---|---|
| POS Invoice | `custom_till` | Data | Read only. The till number, e.g. `TILL1`. |
| POS Opening Shift | `custom_offline_id` | Data | Read only, **Unique**. |
| POS Closing Shift | `custom_offline_id` | Data | Read only, **Unique**. |

POS Invoices use POS Awesome's existing `posa_client_request_id` for the till's bill number. Please confirm on the sandbox that it is **filterable** (the till looks bills up by it); if ERPNext refuses, add a Unique index to it.

## 5. DocType "TillPOS Approval" (new, custom)

New DocType, module Selling, **Custom** ticked, **not** submittable, naming `TPA-.#####`:

| Field | Type | Options |
|---|---|---|
| `action` | Data | LineVoid, BillVoid, ReturnWithoutReceipt, ReturnOverLimit, ReturnOldReceipt, NoSaleDrawerOpen, HeldBillDelete, ShiftVariance, ShiftCount, SettingsChange, UploadModeChange, FailedSupervisorPin |
| `cashier` | Data | till cashier id |
| `supervisor` | Data | blank for a failed PIN |
| `shift` | Link → POS Opening Shift | |
| `invoice` | Link → POS Invoice | optional |
| `item_code` | Link → Item | optional |
| `amount` | Currency | |
| `reason` | Small Text | |
| `at` | Datetime | till time |
| `till` | Data | |
| `custom_offline_id` | Data | **Unique** |

Permissions: System Manager full; `TillPOS Device` read and create.

## 6. Cashiers: DocType "POS Cashier" (new, custom)

Fields: `cashier_name` (Data), `user` (Link → User; the cashier's ERPNext user, written to `posa_cashier`), `pin` (Data, **Permission Level 1**, 4–6 digits, unique), `is_supervisor` (Check), `enabled` (Check). Level 1 is readable by System Manager and `TillPOS Device` only, so other users can't see PINs. The till stores only salted hashes.

Add every cashier and supervisor. The test PINs in the field builds stop working as soon as this list syncs.

## 7. Stock, company, tax and rounding

- **Stock Settings → Allow Negative Stock = on** (agreed), so an offline bill is never rejected for stock.
- **Mode of Payment "Rounding"** (type General; account: the company's **Round Off - AAML**). `erpnext-setup.mjs` creates it when run with an Accounts Manager or System Manager key. A card bill is charged the exact amount, but every bill goes to ERPNext with its rounded total (POS Awesome's closing only balances that way). When the rounded total is a few fils higher than the card amount, the till adds those fils as a "Rounding" payment row, because ERPNext refuses a POS Invoice paid below its rounded total. When it is lower, the fils are booked as change, as POS Awesome does today. The Rounding mode does not need to be on the POS Profiles, so it never appears in POS Awesome.
- **Time zone:** the till sends every date and time in UAE time (UTC+4), whatever the till PC's own time zone is.
- **Company → Tax ID** = the shop's TRN. **Company address** with "Is Your Company Address" ticked and linked on each POS Profile (Company Address). The till then prints the real TRN and the real QR code instead of the sample.

## 8. Before the live sync test (sandbox)

1. Steps 2, 4, 5 and 7 done on the sandbox; the developer user has `TillPOS Device`.
2. On one till, Settings (supervisor PIN) → Upload mode **DryRun**, pointed at the sandbox URL and key. Sell a few bills. Check `C:\ProgramData\TillPOS\outbox-preview\` and the header "waiting / failed" count; fix anything reported.
3. Switch that till to **Live** (sandbox only). Sell: a cash bill, a card bill, a split bill, a weighed item, a return with receipt, a return without receipt. Close the shift.
4. In the sandbox: open the POS Invoices, the POS Opening/Closing Shift and TillPOS Approval list; compare totals with the receipts.

Production stays on **Off** until the owner starts the cutover (Plan 2b Task 10).
