using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Upload;

/// <summary>One problem a DryRun found in a payload: the field (e.g. "items[2].uom") and what is wrong with it.</summary>
public sealed record PayloadIssue(string Field, string Message);

/// <summary>DryRun's read-only checks of a payload against ERPNext: every item, unit, warehouse, payment mode, customer, tax
/// template and the POS Profile (with its change and write-off accounts when a bill needs them) must exist. Each distinct
/// document is read at most once per run (cached). Nothing is written.</summary>
public sealed class DryRunChecks(IErpClient reader)
{
    private readonly Dictionary<string, JsonElement?> docs = [];
    private readonly Dictionary<string, bool> exists = [];

    /// <summary>A whole document (for its fields and child tables), or null when ERPNext has none by that name.</summary>
    public async Task<JsonElement?> DocAsync(string doctype, string name, CancellationToken ct)
    {
        var key = $"{doctype}|{name}";
        if (docs.TryGetValue(key, out var doc)) return doc;
        try
        {
            doc = await reader.GetDocAsync(doctype, name, ct);
        }
        catch (ErpException e) when (e.StatusCode == 404 || e.ExcType == "DoesNotExistError")
        {
            doc = null;
        }
        return docs[key] = doc;
    }

    /// <summary>Whether a document of that name exists (a one-row name lookup).</summary>
    public async Task<bool> ExistsAsync(string doctype, string name, CancellationToken ct)
    {
        var key = $"{doctype}|{name}";
        if (exists.TryGetValue(key, out var found)) return found;
        var rows = await reader.GetListAsync(new ListQuery(doctype, ["name"], [["name", "=", name]], "name asc", 0, 1), ct);
        return exists[key] = rows.Count > 0;
    }

    public async Task<List<PayloadIssue>> InvoiceAsync(Receipt receipt, IReadOnlyDictionary<string, object?> body, CancellationToken ct)
    {
        var issues = new List<PayloadIssue>();
        if (Text(body, "pos_profile") is { } profileName)
        {
            if (await DocAsync("POS Profile", profileName, ct) is not { } profile)
                issues.Add(new("pos_profile", $"POS Profile {profileName} does not exist"));
            else
            {
                // The payment rows and change sent (an exact card bill's Rounding row or change is worked out for the payload, not
                // stored on the receipt); whatever they leave short of the amount due must be written off.
                var change = body.GetValueOrDefault("change_amount") as decimal? ?? 0m;
                if (change > 0m && profile.StrOrNull("account_for_change_amount") is null)
                    issues.Add(new("account_for_change_amount", $"POS Profile {profileName} has no change account, but the bill gives change"));
                var due = Equals(body.GetValueOrDefault("disable_rounded_total"), 0) ? receipt.RoundedTotal : receipt.GrandTotal;
                var paid = Rows(body, "payments").Sum(p => p.GetValueOrDefault("amount") as decimal? ?? 0m) - change;
                if (paid != due && profile.StrOrNull("write_off_account") is null)
                    issues.Add(new("write_off_account", string.Create(CultureInfo.InvariantCulture,
                        $"POS Profile {profileName} has no write-off account, but the bill needs a write-off of {due - paid}")));
            }
        }
        await MustExist(issues, "customer", "Customer", Text(body, "customer"), ct);
        await MustExist(issues, "set_warehouse", "Warehouse", Text(body, "set_warehouse"), ct);
        await MustExist(issues, "taxes_and_charges", "Sales Taxes and Charges Template", Text(body, "taxes_and_charges"), ct);
        var payments = Rows(body, "payments");
        for (var i = 0; i < payments.Count; i++)
            await MustExist(issues, $"payments[{i + 1}].mode_of_payment", "Mode of Payment", Text(payments[i], "mode_of_payment"), ct);

        var items = Rows(body, "items");
        for (var i = 0; i < items.Count; i++)
        {
            var field = $"items[{i + 1}]";
            if (Text(items[i], "warehouse") is { } warehouse && warehouse != Text(body, "set_warehouse"))
                await MustExist(issues, $"{field}.warehouse", "Warehouse", warehouse, ct);
            if (Text(items[i], "item_code") is not { } code) continue;
            if (await DocAsync("Item", code, ct) is not { } item)
            {
                issues.Add(new($"{field}.item_code", $"Item {code} does not exist"));
                continue;
            }
            if (item.Bool("disabled")) issues.Add(new($"{field}.item_code", $"Item {code} is disabled"));
            if (Text(items[i], "uom") is { } uom && !string.Equals(item.StrOrNull("stock_uom"), uom, StringComparison.OrdinalIgnoreCase)
                && !item.Rows("uoms").Any(u => string.Equals(u.StrOrNull("uom"), uom, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new($"{field}.uom", $"UOM {uom} is not set up on item {code}"));
        }
        return issues;
    }

    public async Task<List<PayloadIssue>> ShiftAsync(IReadOnlyDictionary<string, object?> body, string modesTable, CancellationToken ct)
    {
        var issues = new List<PayloadIssue>();
        if (Text(body, "pos_profile") is { } profile && await DocAsync("POS Profile", profile, ct) is null)
            issues.Add(new("pos_profile", $"POS Profile {profile} does not exist"));
        var modes = Rows(body, modesTable);
        for (var i = 0; i < modes.Count; i++)
            await MustExist(issues, $"{modesTable}[{i + 1}].mode_of_payment", "Mode of Payment", Text(modes[i], "mode_of_payment"), ct);
        return issues;
    }

    private async Task MustExist(List<PayloadIssue> issues, string field, string doctype, string? name, CancellationToken ct)
    {
        if (name is not null && !await ExistsAsync(doctype, name, ct)) issues.Add(new(field, $"{doctype} {name} does not exist"));
    }

    private static string? Text(IReadOnlyDictionary<string, object?> body, string key) =>
        body.GetValueOrDefault(key) is string { Length: > 0 } s ? s : null;

    private static List<Dictionary<string, object?>> Rows(IReadOnlyDictionary<string, object?> body, string key) =>
        body.GetValueOrDefault(key) as List<Dictionary<string, object?>> ?? [];
}
