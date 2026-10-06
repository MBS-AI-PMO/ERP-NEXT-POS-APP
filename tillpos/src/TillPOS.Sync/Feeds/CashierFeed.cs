using System.Text.Json;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Full refresh of the POS Cashier list (small). PINs are hashed on the till; an unchanged PIN keeps its hash.
/// Cashiers without a valid 4–6 digit PIN are skipped.</summary>
public sealed record CashierFeedResult(int Stored, int Skipped, int SharedPins);

public sealed class CashierFeed(SyncContext ctx, CashierStore cashiers) : ISyncFeed
{
    public string Name => "POS Cashier";

    public CashierFeedResult? LastResult { get; private set; }

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
        var pinUses = new Dictionary<string, int>();
        var skipped = 0;
        foreach (var row in rows)
        {
            if (row.StrOrNull("pin") is not { } pin || !PinHasher.IsValidPin(pin))
            {
                skipped++;
                continue;
            }
            pinUses[pin] = pinUses.GetValueOrDefault(pin) + 1;
            var id = row.Str("name");
            var hash = existing.TryGetValue(id, out var old) && PinHasher.Verify(pin, old.PinHash) ? old.PinHash : PinHasher.Hash(pin);
            list.Add(new Cashier(id, row.StrOrNull("cashier_name") ?? id, row.StrOrNull("user"), hash, row.Bool("is_supervisor"), row.Bool("enabled")));
        }
        if (list.Count == 0 && (rows.Count > 0 || existing.Count > 0))
            throw new InvalidOperationException(
                $"POS Cashier download had no usable cashiers ({rows.Count} rows, all without a valid 4–6 digit PIN); keeping the {existing.Count} cashiers already on this till.");
        cashiers.ReplaceAll(list);
        LastResult = new CashierFeedResult(list.Count, skipped, pinUses.Count(p => p.Value > 1));
        return list.Count;
    }
}
