using System.Globalization;
using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Sync.Upload;

/// <summary>What the till charged, kept beside the invoice payload (ERPNext calculates its own totals; these are compared with
/// them after the insert). RoundedTotal is the grand total when the bill does not use ERPNext's rounded total.</summary>
public sealed record ExpectedTotals(decimal GrandTotal, decimal RoundedTotal, bool UsesRoundedTotal);

/// <summary>A POS Invoice ready to insert, and the totals the till charged.</summary>
public sealed record InvoicePayload(Dictionary<string, object?> Doc, ExpectedTotals Expected);

/// <summary>The POS Invoice of a till receipt (sale or return), in the fields POS Awesome writes. The money is as charged:
/// ignore_pricing_rule = 1 and the till's rates; grand and rounded totals are not sent (ERPNext computes them and the
/// uploader compares them with <see cref="InvoicePayload.Expected"/>). Cash rows hold the cash tendered and change_amount the
/// change, as POS Awesome does. Returns carry negative quantities and payments, and return_against when the original
/// sale is known (a return without a receipt has none).</summary>
public static class PosInvoicePayload
{
    public const string Doctype = "POS Invoice";

    /// <param name="r">The receipt.</param>
    /// <param name="posProfile">The POS Profile of the shift's counter (ShiftOpening.Counter).</param>
    /// <param name="profile">That profile's synced settings.</param>
    /// <param name="openingShiftErpName">The ERPNext name of the uploaded POS Opening Shift.</param>
    /// <param name="cashierUser">The cashier's ERPNext user (posa_cashier), if known.</param>
    /// <param name="till">The till's name (custom_till), e.g. "TILL2".</param>
    /// <param name="returnAgainstErpName">The ERPNext name of the original sale, for a return with a receipt.</param>
    public static InvoicePayload Build(Receipt r, string posProfile, PosSettings profile, string openingShiftErpName, string? cashierUser,
        string till, string? returnAgainstErpName)
    {
        var doc = new Dictionary<string, object?>
        {
            ["doctype"] = Doctype,
            ["is_pos"] = 1,
            ["pos_profile"] = posProfile,
            ["company"] = profile.Company,
            ["customer"] = profile.Customer,
            ["set_posting_time"] = 1,
            ["posting_date"] = ErpFormat.Date(r.CreatedAt),
            ["posting_time"] = ErpFormat.Time(r.CreatedAt),
            ["selling_price_list"] = profile.PriceList,
            ["set_warehouse"] = profile.Warehouse,
            ["ignore_pricing_rule"] = 1,
        };
        if (!string.IsNullOrWhiteSpace(profile.TaxesAndCharges)) doc["taxes_and_charges"] = profile.TaxesAndCharges;
        doc["disable_rounded_total"] = r.UsesErpRoundedTotal ? 0 : 1;
        doc["items"] = r.Lines.Select(l => Item(l, profile.Warehouse)).ToList();
        doc["payments"] = r.Payments
            .Select(p => new Dictionary<string, object?> { ["mode_of_payment"] = p.ModeOfPayment, ["amount"] = p.Amount })
            .ToList();
        doc["change_amount"] = r.Change;
        doc["posa_pos_opening_shift"] = openingShiftErpName;
        if (!string.IsNullOrWhiteSpace(cashierUser)) doc["posa_cashier"] = cashierUser;
        doc["posa_client_request_id"] = r.ClientId;
        doc["custom_till"] = till;
        if (r.Kind == ReceiptKind.Return)
        {
            doc["is_return"] = 1;
            if (!string.IsNullOrWhiteSpace(returnAgainstErpName)) doc["return_against"] = returnAgainstErpName;
        }
        doc["docstatus"] = 1;

        var expected = new ExpectedTotals(r.GrandTotal, r.UsesErpRoundedTotal ? r.RoundedTotal : r.GrandTotal, r.UsesErpRoundedTotal);
        return new InvoicePayload(doc, expected);
    }

    private static Dictionary<string, object?> Item(ReceiptLine l, string warehouse)
    {
        var item = new Dictionary<string, object?>
        {
            ["item_code"] = l.ItemCode,
            ["item_name"] = l.ItemName,
            ["qty"] = l.Qty,
            ["uom"] = l.Uom,
            ["conversion_factor"] = l.ConversionFactor,
            ["price_list_rate"] = l.PriceListRate,
            ["rate"] = l.Rate,
        };
        if (!string.IsNullOrWhiteSpace(l.Barcode)) item["barcode"] = l.Barcode;
        item["warehouse"] = warehouse;
        if (!string.IsNullOrWhiteSpace(l.ItemTaxTemplate)) item["item_tax_template"] = l.ItemTaxTemplate;
        // POS Awesome's row id is a text field; the till's line number is unique within the bill.
        item["posa_row_id"] = l.LineNo.ToString(CultureInfo.InvariantCulture);
        return item;
    }
}

/// <summary>The POS Awesome POS Opening Shift of a till shift, with the opening float per payment mode.</summary>
public static class OpeningShiftPayload
{
    public const string Doctype = "POS Opening Shift";

    public static Dictionary<string, object?> Build(ShiftOpening opening, string posProfile, string company, string erpUser) => new()
    {
        ["doctype"] = Doctype,
        ["period_start_date"] = ErpFormat.DateTime(opening.OpenedAt),
        ["posting_date"] = ErpFormat.Date(opening.OpenedAt),
        ["company"] = company,
        ["pos_profile"] = posProfile,
        ["user"] = erpUser,
        ["balance_details"] = opening.OpeningAmounts
            .Select(p => new Dictionary<string, object?> { ["mode_of_payment"] = p.ModeOfPayment, ["amount"] = p.Amount })
            .ToList(),
        ["custom_offline_id"] = opening.ClientId,
        ["docstatus"] = 1,
    };
}

/// <summary>The POS Awesome POS Closing Shift of a closed till shift: every uploaded invoice of the shift, the totals and
/// the blind-count reconciliation per payment mode (opening, expected, counted, difference) from <see cref="ShiftClosing"/>.</summary>
public static class ClosingShiftPayload
{
    public const string Doctype = "POS Closing Shift";

    /// <param name="opening">The shift as opened.</param>
    /// <param name="closing">The shift's local closing (blind count).</param>
    /// <param name="openingErpName">The ERPNext name of the uploaded POS Opening Shift.</param>
    /// <param name="invoices">The shift's receipts with their ERPNext names, oldest first.</param>
    /// <param name="posProfile">The shift's POS Profile.</param>
    /// <param name="company">The profile's company.</param>
    /// <param name="erpUser">The till's ERPNext user.</param>
    /// <param name="customer">The POS Profile's customer (each transaction row names it).</param>
    public static Dictionary<string, object?> Build(ShiftOpening opening, ShiftClosing closing, string openingErpName,
        IReadOnlyList<(string ErpName, Receipt Receipt)> invoices, string posProfile, string company, string erpUser, string customer) => new()
    {
        ["doctype"] = Doctype,
        ["pos_opening_shift"] = openingErpName,
        ["period_start_date"] = ErpFormat.DateTime(opening.OpenedAt),
        ["period_end_date"] = ErpFormat.DateTime(closing.ClosedAt),
        ["posting_date"] = ErpFormat.Date(closing.ClosedAt),
        ["company"] = company,
        ["pos_profile"] = posProfile,
        ["user"] = erpUser,
        ["grand_total"] = closing.GrandTotal,
        ["net_total"] = closing.NetTotal,
        ["total_quantity"] = invoices.Sum(i => i.Receipt.Lines.Sum(l => l.Qty)),
        ["pos_transactions"] = invoices
            .Select(i => new Dictionary<string, object?>
            {
                ["pos_invoice"] = i.ErpName,
                ["posting_date"] = ErpFormat.Date(i.Receipt.CreatedAt),
                ["customer"] = customer,
                ["grand_total"] = i.Receipt.GrandTotal,
            })
            .ToList(),
        ["payment_reconciliation"] = closing.Modes
            .Select(m => new Dictionary<string, object?>
            {
                ["mode_of_payment"] = m.ModeOfPayment,
                ["opening_amount"] = m.Opening,
                ["expected_amount"] = m.Expected,
                ["closing_amount"] = m.Counted,
                ["difference"] = m.Difference,
            })
            .ToList(),
        ["custom_offline_id"] = closing.ShiftClientId,
        ["docstatus"] = 1,
    };
}

/// <summary>A supervisor approval as a row of the custom DocType "TillPOS Approval" (admin setup, plan 2b Tasks 7 and 8; not
/// submittable, so no docstatus). Cashier and supervisor are POS Cashier ids; shift and invoice are ERPNext names when known.</summary>
public static class ApprovalPayload
{
    public const string Doctype = "TillPOS Approval";

    public static Dictionary<string, object?> Build(ApprovalRecord a, string till, string? shiftErpName, string? invoiceErpName) => new()
    {
        ["doctype"] = Doctype,
        ["action"] = a.Action.ToString(),
        ["cashier"] = Blank(a.CashierId),
        ["supervisor"] = Blank(a.SupervisorId),
        ["shift"] = Blank(shiftErpName),
        ["invoice"] = Blank(invoiceErpName),
        ["item_code"] = Blank(a.ItemCode),
        ["amount"] = a.Amount,
        ["reason"] = Blank(a.Reason),
        ["at"] = ErpFormat.DateTime(a.At),
        ["till"] = till,
        ["custom_offline_id"] = a.Id,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
