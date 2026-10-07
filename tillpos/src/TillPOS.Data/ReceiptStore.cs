using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

/// <summary>See <see cref="UploadStatus"/> (same meanings).</summary>
public enum ReceiptSyncStatus { Pending, Synced, Failed, Excluded }

/// <param name="NextAttemptAt">After a failure: the uploader leaves the bill alone until then (backoff); null = due now.</param>
public sealed record ReceiptSyncInfo(ReceiptSyncStatus Status, string? ErpName, string? LastError, int Attempts, DateTimeOffset? NextAttemptAt = null);

/// <summary>A bill with its upload state.</summary>
public sealed record ReceiptOutboxEntry(Receipt Receipt, ReceiptSyncInfo Sync);

/// <summary>Completed bills and their upload state (the outbox). Pending = waiting for upload,
/// Failed = ERPNext refused it (tried again after its backoff, or at once after a Retry), Synced = uploaded.</summary>
public sealed class ReceiptStore(TillDb db) : IReceiptStore
{
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Raised after a bill is saved, so the upload can start soon.</summary>
    public event Action? Saved;

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
        using (var c = db.Open())
        {
            c.Exec(null, """
                INSERT INTO receipt (client_id, kind, return_against, shift_client_id, created_at, json)
                VALUES (@id, @k, @ra, @s, @at, @j)
                """,
                ("@id", receipt.ClientId), ("@k", receipt.Kind.ToString()), ("@ra", receipt.ReturnAgainst), ("@s", receipt.ShiftClientId),
                ("@at", receipt.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("@j", JsonSerializer.Serialize(receipt, Json)));
        }
        Saved?.Invoke();
    }

    public Receipt? Get(string clientId) => Query("WHERE client_id = @p", clientId).FirstOrDefault();
    public IReadOnlyList<Receipt> ReturnsAgainst(string clientId) => Query("WHERE return_against = @p ORDER BY created_at", clientId);
    public IReadOnlyList<Receipt> ByShift(string shiftClientId) => Query("WHERE shift_client_id = @p ORDER BY created_at", shiftClientId);

    /// <summary>The newest sales on this till, newest first (the Returns screen's recent bills). Returns are left out;
    /// ask <see cref="ReturnsAgainst"/> for what was already returned from a sale.</summary>
    public IReadOnlyList<Receipt> RecentSales(int limit)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM receipt WHERE kind = 'Sale' ORDER BY created_at DESC, client_id DESC LIMIT @l",
            r => Deserialize(r.GetString(0)), ("@l", limit));
    }

    public int CountPending() => Count("Pending");

    public int CountFailed() => Count("Failed");

    public IReadOnlyList<Receipt> ListPending(int limit)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM receipt WHERE sync_status = 'Pending' ORDER BY created_at, client_id LIMIT @l",
            r => Deserialize(r.GetString(0)), ("@l", limit));
    }

    public IReadOnlyList<Receipt> ListFailed() => Query("WHERE sync_status = 'Failed' ORDER BY created_at", null);

    /// <summary>Every bill of a shift with its upload state, oldest first.</summary>
    public IReadOnlyList<ReceiptOutboxEntry> Outbox(string shiftClientId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT json, sync_status, erp_name, last_error, attempts, next_attempt_at FROM receipt
            WHERE shift_client_id = @s ORDER BY created_at, client_id
            """,
            r => new ReceiptOutboxEntry(Deserialize(r.GetString(0)), ReadSync(r, 1)), ("@s", shiftClientId));
    }

    public void MarkSynced(string clientId, string erpName) =>
        Update("""
            UPDATE receipt SET sync_status = 'Synced', erp_name = @n, last_error = NULL, attempts = attempts + 1, next_attempt_at = NULL
            WHERE client_id = @id
            """,
            clientId, false, ("@n", erpName));

    /// <summary>A failure reported after the bill was already uploaded is ignored. <paramref name="nextAttemptAt"/> is the backoff:
    /// the uploader leaves the bill alone until then.</summary>
    public void MarkFailed(string clientId, string error, DateTimeOffset? nextAttemptAt = null)
    {
        var changed = Update("""
            UPDATE receipt SET sync_status = 'Failed', last_error = @e, attempts = attempts + 1, next_attempt_at = @next
            WHERE client_id = @id AND sync_status IN ('Pending', 'Failed')
            """,
            clientId, true, ("@e", error), ("@next", SqlExt.Instant(nextAttemptAt)));
        if (changed != 0) return;
        using var c = db.Open();
        if (c.Scalar(null, "SELECT 1 FROM receipt WHERE client_id = @id", ("@id", clientId)) is null)
            throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    /// <summary>Written just before the bill is sent: it stays Pending with "upload in progress" and is not tried again before
    /// <paramref name="until"/>. If the answer never comes (timeout, dropped connection), the marker stays, and the next try
    /// starts with the client-id lookup. A synced bill is left alone.</summary>
    public void MarkInFlight(string clientId, DateTimeOffset until) =>
        Update("""
            UPDATE receipt SET sync_status = 'Pending', last_error = @e, next_attempt_at = @u
            WHERE client_id = @id AND sync_status IN ('Pending', 'Failed')
            """, clientId, true, ("@e", InFlight), ("@u", SqlExt.Instant(until)));

    /// <summary>The last_error of a document whose upload was started but not answered.</summary>
    public const string InFlight = "upload in progress";

    /// <summary>A failed bill goes back to the queue at once, its backoff reset.</summary>
    public void Retry(string clientId) =>
        Update("UPDATE receipt SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL WHERE client_id = @id AND sync_status = 'Failed'",
            clientId, true);

    public ReceiptSyncInfo SyncInfo(string clientId)
    {
        using var c = db.Open();
        return c.Query("SELECT sync_status, erp_name, last_error, attempts, next_attempt_at FROM receipt WHERE client_id = @id",
                r => ReadSync(r, 0), ("@id", clientId))
            .FirstOrDefault() ?? throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private static ReceiptSyncInfo ReadSync(SqliteDataReader r, int i) =>
        new(Enum.Parse<ReceiptSyncStatus>(r.GetString(i)), SqlExt.Str(r, i + 1), SqlExt.Str(r, i + 2), r.GetInt32(i + 3), SqlExt.Instant(r, i + 4));

    private int Count(string status)
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, "SELECT COUNT(*) FROM receipt WHERE sync_status = @s", ("@s", status)), CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<Receipt> Query(string where, string? p)
    {
        using var c = db.Open();
        return p is null
            ? c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)))
            : c.Query($"SELECT json FROM receipt {where}", r => Deserialize(r.GetString(0)), ("@p", p));
    }

    private int Update(string sql, string clientId, bool allowNoMatch, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        var changed = c.Exec(null, sql, [("@id", clientId), .. extra]);
        if (changed == 0 && !allowNoMatch)
            throw new KeyNotFoundException($"Receipt {clientId} not found.");
        return changed;
    }

    private static Receipt Deserialize(string json) =>
        JsonSerializer.Deserialize<Receipt>(json, Json) ?? throw new InvalidDataException("Stored receipt is empty.");
}
