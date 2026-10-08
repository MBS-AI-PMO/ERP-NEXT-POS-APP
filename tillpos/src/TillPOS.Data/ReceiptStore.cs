using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

/// <summary>See <see cref="UploadStatus"/> (same meanings).</summary>
public enum ReceiptSyncStatus { Pending, Synced, Failed, Excluded, Handled }

/// <param name="NextAttemptAt">After a failure: the uploader leaves the bill alone until then (backoff); null = due now.</param>
/// <param name="UnknownAttempts">Writes in a row that got no answer (the uploader escalates after 3).</param>
public sealed record ReceiptSyncInfo(ReceiptSyncStatus Status, string? ErpName, string? LastError, int Attempts, DateTimeOffset? NextAttemptAt = null,
    int UnknownAttempts = 0);

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
            SELECT json, sync_status, erp_name, last_error, attempts, next_attempt_at, unknown_attempts FROM receipt
            WHERE shift_client_id = @s ORDER BY created_at, client_id
            """,
            r => new ReceiptOutboxEntry(Deserialize(r.GetString(0)), ReadSync(r, 1)), ("@s", shiftClientId));
    }

    /// <summary>The bill is in ERPNext as <paramref name="erpName"/>, since <paramref name="at"/> (default: now).</summary>
    public void MarkSynced(string clientId, string erpName, DateTimeOffset? at = null) =>
        Update("""
            UPDATE receipt SET sync_status = 'Synced', erp_name = @n, last_error = NULL, attempts = attempts + 1, next_attempt_at = NULL, unknown_attempts = 0,
                synced_at = @at
            WHERE client_id = @id
            """,
            clientId, false, ("@n", erpName), ("@at", SqlExt.Instant(at ?? DateTimeOffset.UtcNow)));

    /// <summary>Bills that reached ERPNext at or after <paramref name="since"/>.</summary>
    public int CountSyncedSince(DateTimeOffset since)
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, "SELECT COUNT(*) FROM receipt WHERE sync_status = 'Synced' AND synced_at >= @s",
            ("@s", SqlExt.Instant(since))), CultureInfo.InvariantCulture);
    }

    /// <summary>The newest <paramref name="limit"/> bills that reached ERPNext at or after <paramref name="since"/>, newest first.</summary>
    public IReadOnlyList<SyncedDocument> SyncedSince(DateTimeOffset since, int limit = int.MaxValue)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT json, erp_name, synced_at FROM receipt WHERE sync_status = 'Synced' AND synced_at >= @s
            ORDER BY synced_at DESC, client_id LIMIT @l
            """,
            r =>
            {
                var receipt = Deserialize(r.GetString(0));
                return new SyncedDocument(OutboxKind.Bill, receipt.ClientId, SqlExt.Str(r, 1), SqlExt.Instant(r, 2)!.Value, receipt.GrandTotal,
                    receipt.Kind == ReceiptKind.Return);
            }, ("@s", SqlExt.Instant(since)), ("@l", limit));
    }

    /// <summary>The newest <paramref name="limit"/> bills waiting for upload (Pending), newest first, with their last error or note
    /// (e.g. "upload in progress"). The bills themselves are not read.</summary>
    public IReadOnlyList<OutboxProblem> Waiting(int limit = int.MaxValue) => Newest("Pending", limit);

    /// <summary>The newest <paramref name="limit"/> bills ERPNext refused (Failed), newest first, with their error.</summary>
    public IReadOnlyList<OutboxProblem> Failed(int limit = int.MaxValue) => Newest("Failed", limit);

    private List<OutboxProblem> Newest(string status, int limit)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT client_id, shift_client_id, created_at, last_error, attempts, kind FROM receipt
            WHERE sync_status = @st ORDER BY created_at DESC, client_id DESC LIMIT @l
            """,
            r => new OutboxProblem(OutboxKind.Bill, r.GetString(0), r.GetString(1), SqlExt.Instant(r, 2)!.Value, Enum.Parse<UploadStatus>(status),
                SqlExt.Str(r, 3), r.GetInt32(4), r.GetString(5) == nameof(ReceiptKind.Return)), ("@st", status), ("@l", limit));
    }

    /// <summary>A failure reported after the bill was already uploaded is ignored. <paramref name="nextAttemptAt"/> is the backoff:
    /// the uploader leaves the bill alone until then.</summary>
    /// <param name="keepUnknown">The failure is the escalation of unanswered writes: the count stays, so later tries only look
    /// the bill up (Retry resets it).</param>
    public void MarkFailed(string clientId, string error, DateTimeOffset? nextAttemptAt = null, bool keepUnknown = false)
    {
        var changed = Update("""
            UPDATE receipt SET sync_status = 'Failed', last_error = @e, attempts = attempts + 1, next_attempt_at = @next,
                unknown_attempts = CASE WHEN @keep = 1 THEN unknown_attempts ELSE 0 END
            WHERE client_id = @id AND sync_status IN ('Pending', 'Failed')
            """,
            clientId, true, ("@e", error), ("@next", SqlExt.Instant(nextAttemptAt)), ("@keep", keepUnknown ? 1 : 0));
        if (changed != 0) return;
        using var c = db.Open();
        if (c.Scalar(null, "SELECT 1 FROM receipt WHERE client_id = @id", ("@id", clientId)) is null)
            throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    /// <summary>Written just before the bill is sent: it stays Pending with "upload in progress" and is not tried again before
    /// <paramref name="until"/>. If the answer never comes (timeout, dropped connection), the marker stays, and the next try
    /// starts with the client-id lookup. A bill that is no longer Pending or Failed (synced, or marked handled meanwhile) is left
    /// alone: false, and the uploader does not send it.</summary>
    public bool MarkInFlight(string clientId, DateTimeOffset until) =>
        Update("""
            UPDATE receipt SET sync_status = 'Pending', last_error = @e, next_attempt_at = @u
            WHERE client_id = @id AND sync_status IN ('Pending', 'Failed')
            """, clientId, true, ("@e", InFlight), ("@u", SqlExt.Instant(until))) > 0;

    /// <summary>A write got no answer (timeout, gateway, server error): one more unknown outcome in a row, and what came back
    /// instead of an answer. The bill stays Pending under its in-flight marker.</summary>
    public void MarkUnknown(string clientId, string error) =>
        Update("""
            UPDATE receipt SET unknown_attempts = unknown_attempts + 1, last_error = @e
            WHERE client_id = @id AND sync_status IN ('Pending', 'Failed')
            """, clientId, true, ("@e", error));

    /// <summary>The last_error of a document whose upload was started but not answered.</summary>
    public const string InFlight = "upload in progress";

    /// <summary>Failed, excluded and handled bills, oldest first (the Upload problems screen).</summary>
    public IReadOnlyList<OutboxProblem> Problems()
    {
        using var c = db.Open();
        return c.Query("""
            SELECT client_id, shift_client_id, created_at, sync_status, last_error, attempts, kind FROM receipt
            WHERE sync_status IN ('Failed', 'Excluded', 'Handled') ORDER BY created_at, client_id
            """,
            r => new OutboxProblem(OutboxKind.Bill, r.GetString(0), r.GetString(1), SqlExt.Instant(r, 2)!.Value,
                Enum.Parse<UploadStatus>(r.GetString(3)), SqlExt.Str(r, 4), r.GetInt32(5), r.GetString(6) == nameof(ReceiptKind.Return)));
    }

    /// <summary>A supervisor dealt with a failed or excluded bill by hand in ERPNext: it is never uploaded and no longer counted.
    /// <paramref name="note"/> ("Handled by …: reason reference") is kept as its last error, and the status it had is kept for
    /// <see cref="Unhandle"/>. False when it was no longer Failed or Excluded (nothing changed).</summary>
    public bool MarkHandled(string clientId, string note) =>
        Update("""
            UPDATE receipt SET status_before_handled = sync_status, sync_status = 'Handled', last_error = @n, next_attempt_at = NULL,
                unknown_attempts = 0
            WHERE client_id = @id AND sync_status IN ('Failed', 'Excluded')
            """, clientId, true, ("@n", note)) > 0;

    /// <summary>Takes back a handled bill: an excluded one is excluded again, a failed one goes back to the queue (backoff
    /// reset). False when it was no longer Handled.</summary>
    public bool Unhandle(string clientId) =>
        Update("""
            UPDATE receipt SET sync_status = CASE status_before_handled WHEN 'Excluded' THEN 'Excluded' ELSE 'Pending' END,
                status_before_handled = NULL, last_error = NULL, attempts = 0, next_attempt_at = NULL, unknown_attempts = 0
            WHERE client_id = @id AND sync_status = 'Handled'
            """, clientId, true) > 0;

    /// <summary>A failed bill goes back to the queue at once, its backoff reset.</summary>
    public bool Retry(string clientId) =>
        Update("UPDATE receipt SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL, unknown_attempts = 0 WHERE client_id = @id AND sync_status = 'Failed'",
            clientId, true) > 0;

    public ReceiptSyncInfo SyncInfo(string clientId)
    {
        using var c = db.Open();
        return c.Query("SELECT sync_status, erp_name, last_error, attempts, next_attempt_at, unknown_attempts FROM receipt WHERE client_id = @id",
                r => ReadSync(r, 0), ("@id", clientId))
            .FirstOrDefault() ?? throw new KeyNotFoundException($"Receipt {clientId} not found.");
    }

    private static ReceiptSyncInfo ReadSync(SqliteDataReader r, int i) =>
        new(Enum.Parse<ReceiptSyncStatus>(r.GetString(i)), SqlExt.Str(r, i + 1), SqlExt.Str(r, i + 2), r.GetInt32(i + 3), SqlExt.Instant(r, i + 4),
            r.GetInt32(i + 5));

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
