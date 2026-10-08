# TillPOS — Delivery bills (Design Spec)

Status: approved by the shop owner on 2026-10-08 (with the "Deliveries paid this shift" Z report line; no "Print list").
Parent spec: `2026-10-05-tillpos-offline-pos-design.md` (Phase 1A till).

## 1. Goal

The shop delivers orders. The cashier rings up the order and prints a **delivery invoice** that goes out with the items, unpaid.
The bill waits in a **Delivery list** on the till. When the driver comes back with the money, the cashier takes the payment
(exact, no change) and the bill becomes a normal paid sale. A customer who refuses the delivery, or keeps only part of it, is
handled from the list.

## 2. Decisions (made by the shop owner, 2026-10-08)

| # | Decision |
|---|---|
| D1 | ERPNext learns about a delivery **only once it is paid**. Until then it lives on the till only. |
| D2 | **No customer details** are recorded: the delivery is just the bill. |
| D3 | A shift **may close with deliveries still out**; the Z report lists them; they are paid in whichever shift is open later. |
| D4 | From the list a delivery can be **paid**, **changed** (lines removed or reduced) or **cancelled**. Changing and cancelling need a supervisor PIN and are logged. |
| D5 | Payment is **exact**: cash is filled in with the amount due (no change); card is the exact amount; split works as for any bill. |

Rounding is as for every bill: cash and split rounded to AED 0.25, card exact (see the 0.4.2 rules).

## 3. Flows

### 3.1 Making a delivery
- Sale screen: new **Delivery (F9)** action beside Pay cash / Card. Refused with an empty bill.
- The bill gets its till bill number now (`TILLn-yyyyMMddHHmmss-nnnnnn`, the next number in the sequence) and is saved as an
  open delivery: its lines exactly as on screen (item, unit, quantity, price-list rate, charged rate, offer, barcode, scale
  flag), the counter and shift it was made in, the cashier, and the time.
- **Prices are fixed at this moment**: paying later never re-prices the lines, even if the price list changed.
- Prints a **DELIVERY INVOICE — NOT PAID** slip (section 5). No drawer kick. The sale screen is cleared for the next customer
  (its autosave is cleared too).
- Nothing is uploaded.

### 3.2 The Delivery list
- New **Deliveries** action on the sale screen (with a count badge when any are out), and the header shows "N deliveries out".
- The list shows each open delivery: bill number, time made, cashier, amount to collect, and how long it has been out
  (e.g. "2 h 10 min"), oldest first.
- Scanning a delivery slip's barcode on the sale screen or in the list opens that delivery.
- Actions on a delivery:
  - **Pay** — opens the normal payment screen showing the delivery's items (Cash / Card / Split). In Cash the cash box is
    pre-filled with the exact amount due, so Change shows 0.00.
  - **Change items** (supervisor PIN) — remove lines or reduce quantities the customer did not keep; the amount to collect is
    recalculated; nothing can be added (a new item is a new bill). Removing the last line is a cancel.
  - **Reprint slip** — prints the delivery slip again, marked COPY.
  - **Cancel** (supervisor PIN, reason required: Refused / Not delivered / Other) — the delivery is closed as cancelled; nothing
    ever reaches ERPNext for it.
- A delivery can only be paid while a shift is open (any counter of this till).

### 3.3 Getting paid
- Paying turns the delivery into a normal **sale receipt** in the shift open at that moment:
  - it keeps its bill number (from 3.1);
  - its time is the **payment time** (it is in the paying shift's period, as ERPNext requires);
  - its counter, POS Profile, warehouse and payment modes are the **paying shift's** (the money is in that drawer);
  - its lines and rates are the delivery's fixed ones.
- It prints a normal tax invoice with a "DELIVERY — PAID" line under the title, and opens the drawer if cash was taken.
- It then uploads like any sale (POS Invoice dated at the payment time). Returns of it work like any bill, counted from the
  payment date.

### 3.4 Shift close
- Allowed with deliveries out. The Z report gets a section: "Deliveries still out: N, AED X" with each bill number and
  amount. They are not part of the drawer's expected cash.
- Deliveries paid during the shift are ordinary sales of that shift (counted in its bills, cash and card). The Z report also
  shows them on their own line, "Deliveries paid this shift: N, AED X" (already included in the sales figures above it).
  Printed only when N > 0.

## 4. Data

New SQLite table `delivery` (migration), one row per delivery:

| Column | Meaning |
|---|---|
| `client_id` | the bill number (primary key) |
| `status` | `Open`, `Paid`, `Cancelled` |
| `created_at` | when made |
| `shift_client_id`, `counter`, `cashier` | where and by whom it was made |
| `lines_json` | the fixed lines (as 3.1) |
| `grand_total` | the amount at creation (for the list; recalculated after a change) |
| `paid_receipt` | the receipt client id once paid (same as `client_id`) |
| `closed_at`, `closed_by`, `reason` | when paid or cancelled, by whom, and a cancel's reason |

- The bill number's sequence is the receipt sequence (`IReceiptStore.NextSequence`), so a delivery number and a receipt number
  never collide.
- Paying writes the receipt and marks the delivery `Paid` in one transaction; a crash between them cannot leave a paid bill
  that is still listed, or a paid delivery with no receipt.
- Survives restarts (it is in the till database, not memory).

New approval actions (logged and uploaded like the others): `DeliveryChange`, `DeliveryCancel`.

## 5. Printing

**Delivery slip** (same printer and paper widths as receipts):
- shop header; title **DELIVERY INVOICE** with **NOT PAID** under it in bold;
- bill number, date and time made, cashier, till and counter;
- item lines, VAT, total;
- **AMOUNT TO COLLECT**: "Cash AED x.xx (rounded) / Card AED x.xxx (exact)" — both shown, since the driver may get either;
- barcode of the bill number (to open it from the list);
- no QR code (it is not a paid tax invoice yet), no payment lines.

**Paid invoice**: the normal tax invoice (QR included) with "DELIVERY — PAID" under the title; its date is the payment time.

**Z report**: the "Deliveries paid this shift" line and the "Deliveries still out" section (3.4).

## 6. Not in scope

Customer name/phone/address; delivery charges; driver names; paying a delivery at another till (the list is per till);
showing unpaid deliveries in ERPNext; partial payments (a delivery is paid in full or changed first).

## 7. Error handling

- Printer failure on the slip: the delivery is still saved; the error is shown and the slip can be reprinted from the list.
- Pay screen backed out: the delivery stays open, unchanged.
- No open shift: Pay is refused with "Open a shift first"; the list can still be viewed.
- A delivery older than 7 days shows in red in the list (it is never removed automatically).

## 8. Testing

- Store: create, list (oldest first), change lines, cancel, pay (receipt + status in one transaction), restart survival,
  shared sequence with receipts.
- Payment: exact cash pre-fill (no change), card exact, split; rounding as for sales; paying at another counter uses that
  counter's modes and profile; prices not re-priced after a price change.
- Supervisor gates and approval logging for change and cancel; refusal leaves the delivery as it was.
- Printing: slip layout at 80 mm and 58 mm (NOT PAID, both collect amounts, barcode, no QR); paid invoice marker; Z report
  section.
- Shift close with deliveries out; paying in the next shift; Z report numbers.
- Upload: a paid delivery uploads as a normal POS Invoice dated at payment, in the paying shift; an open or cancelled one never
  uploads.
- Live check on the dev ERPNext before handing over.
