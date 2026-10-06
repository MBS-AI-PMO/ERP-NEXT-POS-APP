using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

public enum ReceiptSyncStatus { Pending, Synced, Failed }

public sealed record ReceiptSyncInfo(ReceiptSyncStatus Status, string? ErpName, string? LastError, int Attempts);

/// <summary>Completed bills and their upload state (the outbox). Pending = waiting for upload,
/// Failed = ERPNext refused it (waits for a supervisor retry), Synced = uploaded.</summary>
public sealed class ReceiptStore(TillDb db) : IReceiptStore
{
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public long NextSequence()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, "INSERT INTO kv (key, value) VALUES ('receipt_seq', '0') ON CONFLICT(key) DO NOTHING");
        c.Exec(tx, "UPDATE kv SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key = 'receipt_seq'");
        var value = Convert.ToInt64(c.Scalar(tx, "SELECT value FROM kv WHERE key = 'receipt_seq'"), CultureInfo.InvariantCulture);
        tx.Commit();
        return value;
    }

    public void Save(Receipt receipt)
    {
        using var c = db.Open();
        c.Exec(null, """
            INSERT INTO receipt (client_id, kind, return_against, shift_client_id, created_at, json)
            VALUES (@id, @k, @ra, @s, @at, @j)
            """,
            ("@id", receipt.ClientId), ("@k", receipt.Kind.ToString()), ("@ra", receipt.ReturnAgainst), ("@s", receipt.ShiftClientId),
            ("@at", receipt.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("@j", JsonSerializer.Serialize(receipt, Json)));
    }

    public Receipt? Get(string clientId) => Query("WHERE client_id = @p", clientId).FirstOrDefault();
    public IReadOnlyList<Receipt> ReturnsAgainst(string clientId) => Query("WHERE return_against = @p ORDER BY created_at", clientId);
    public IReadOnlyList<Receipt> ByShift(string shiftClientId) => Query("WHERE shift_client_id = @p ORDER BY created_at", shiftClientId);

    public IReadOnlyList<Receipt> ListPending(int limit)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM receipt WHERE sync_status = 'Pending' ORDER BY created_at, client_id LIMIT @l",
            r => Deserialize(r.GetString(0)), ("@l", limit));
    }

    public IReadOnlyList<Receipt> ListFailed() => Query("WHERE sync_status = 'Failed' ORDER BY created_at", null);

    public void MarkSynced(string clientId, string erpName) =>
        Update("UPDATE receipt SET sync_status = 'Synced', erp_name = @n, last_error = NULL, attempts = attempts + 1 WHERE client_id = @id",
            clientId, ("@n", erpName));

    public void MarkFailed(string clientId, string error) =>
        Update("UPDATE receipt SET sync_status = 'Failed', last_error = @e, attempts = attempts + 1 WHERE client_id = @id",
            clientId, ("@e", error));

    public void Retry(string clientId) =>
        Update("UPDATE receipt SET sync_status = 'Pending' WHERE client_id = @id AND sync_status = 'Failed'", clientId);

    public ReceiptSyncInfo SyncInfo(string clientId)
    {
        using var c = db.Open();
        return c.Query("SELECT sync_status, erp_name, last_error, attempts FROM receipt WHERE client_id = @id",
                r => new ReceiptSyncInfo(Enum.Parse<ReceiptSyncStatus>(r.GetString(0)), SqlExt.Str(r, 1), SqlExt.Str(r, 2), r.GetInt32(3)),
                ("@id", clientId))
            .FirstOrDefault() ?? throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private IReadOnlyList<Receipt> Query(string where, string? p)
    {
        using var c = db.Open();
        return p is null
            ? c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)))
            : c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)), ("@p", p));
    }

    private void Update(string sql, string clientId, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        var changed = c.Exec(null, sql, [("@id", clientId), .. extra]);
        if (changed == 0 && !sql.Contains("sync_status = 'Failed'", StringComparison.Ordinal))
            throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private static Receipt Deserialize(string json) =>
        JsonSerializer.Deserialize<Receipt>(json, Json) ?? throw new InvalidDataException("Stored receipt is empty.");
}
