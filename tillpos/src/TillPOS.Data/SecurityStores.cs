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
