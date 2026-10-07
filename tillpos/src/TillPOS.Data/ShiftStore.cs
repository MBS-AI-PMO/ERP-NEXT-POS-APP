using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TillPOS.Core.Shifts;

namespace TillPOS.Data;

public sealed class ShiftStore(TillDb db)
{
    /// <summary>Raised after a shift is opened or closed, so the upload can start soon.</summary>
    public event Action? Changed;

    public void Open(ShiftOpening opening)
    {
        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            if (c.Scalar(tx, "SELECT client_id FROM shift WHERE closed_at IS NULL") is string open)
                throw new InvalidOperationException($"Shift {open} is still open; close it first.");
            c.Exec(tx, "INSERT INTO shift (client_id, opened_at, opening_json) VALUES (@id, @at, @j)",
                ("@id", opening.ClientId), ("@at", opening.OpenedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("@j", JsonSerializer.Serialize(opening)));
            tx.Commit();
        }
        Changed?.Invoke();
    }

    public ShiftOpening? Current()
    {
        using var c = db.Open();
        return c.Query("SELECT opening_json FROM shift WHERE closed_at IS NULL",
            r => JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!).FirstOrDefault();
    }

    /// <summary>Rewrites the opening of a shift that is still open (e.g. to save the counter on a shift opened before counters
    /// existed). Its client id, time and float must not change. Returns false when the shift is not open.</summary>
    public bool UpdateOpening(ShiftOpening opening)
    {
        using var c = db.Open();
        return c.Exec(null, "UPDATE shift SET opening_json = @j WHERE client_id = @id AND closed_at IS NULL",
            ("@id", opening.ClientId), ("@j", JsonSerializer.Serialize(opening))) > 0;
    }

    public void Close(ShiftClosing closing)
    {
        using (var c = db.Open())
        {
            var changed = c.Exec(null, "UPDATE shift SET closed_at = @at, closing_json = @j WHERE client_id = @id AND closed_at IS NULL",
                ("@id", closing.ShiftClientId), ("@at", closing.ClosedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("@j", JsonSerializer.Serialize(closing)));
            if (changed == 0) throw new InvalidOperationException($"Shift {closing.ShiftClientId} is not open.");
        }
        Changed?.Invoke();
    }

    public (ShiftOpening Opening, ShiftClosing? Closing)? Get(string clientId)
    {
        using var c = db.Open();
        var rows = c.Query("SELECT opening_json, closing_json FROM shift WHERE client_id = @id",
            r => (JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!,
                  r.IsDBNull(1) ? null : JsonSerializer.Deserialize<ShiftClosing>(r.GetString(1))),
            ("@id", clientId));
        return rows.Count == 0 ? null : rows[0];
    }

    private const string SyncColumns = "opening_status, erp_opening, closing_status, erp_closing, last_error, attempts, next_attempt_at";

    /// <summary>Shifts with a document still to upload (the opening, a bill, or the closing of a closed shift), oldest first.</summary>
    public IReadOnlyList<ShiftOutboxEntry> Unfinished()
    {
        using var c = db.Open();
        return c.Query($"""
            SELECT opening_json, closing_json, {SyncColumns} FROM shift
            WHERE opening_status <> 'Synced' OR (closed_at IS NOT NULL AND closing_status <> 'Synced')
               OR EXISTS (SELECT 1 FROM receipt WHERE receipt.shift_client_id = shift.client_id AND receipt.sync_status <> 'Synced')
            ORDER BY opened_at, client_id
            """,
            r => new ShiftOutboxEntry(JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!,
                r.IsDBNull(1) ? null : JsonSerializer.Deserialize<ShiftClosing>(r.GetString(1)), ReadSync(r, 2)));
    }

    /// <summary>The shift's upload state, or null for an unknown shift.</summary>
    public ShiftSyncInfo? SyncInfo(string clientId)
    {
        using var c = db.Open();
        return c.Query($"SELECT {SyncColumns} FROM shift WHERE client_id = @id", r => ReadSync(r, 0), ("@id", clientId)).FirstOrDefault();
    }

    public void MarkSynced(string clientId, ShiftDocument document, string erpName)
    {
        var (status, name) = Columns(document);
        Update($"UPDATE shift SET {status} = 'Synced', {name} = @n, last_error = NULL, attempts = 0, next_attempt_at = NULL WHERE client_id = @id",
            clientId, ("@n", erpName));
    }

    /// <summary>A failure reported after the document was already uploaded is ignored.</summary>
    public void MarkFailed(string clientId, ShiftDocument document, string error, DateTimeOffset? nextAttemptAt)
    {
        var (status, _) = Columns(document);
        Update($"""
            UPDATE shift SET {status} = 'Failed', last_error = @e, attempts = attempts + 1, next_attempt_at = @next
            WHERE client_id = @id AND {status} <> 'Synced'
            """,
            clientId, ("@e", error), ("@next", SqlExt.Instant(nextAttemptAt)));
    }

    /// <summary>Written just before the document is sent (see <see cref="ReceiptStore.MarkInFlight"/>).</summary>
    public void MarkInFlight(string clientId, ShiftDocument document, DateTimeOffset until)
    {
        var (status, _) = Columns(document);
        Update($"""
            UPDATE shift SET {status} = 'Pending', last_error = @e, next_attempt_at = @u
            WHERE client_id = @id AND {status} IN ('Pending', 'Failed')
            """, clientId, ("@e", ReceiptStore.InFlight), ("@u", SqlExt.Instant(until)));
    }

    /// <summary>A failed shift document goes back to the queue at once, its backoff reset.</summary>
    public void Retry(string clientId) =>
        Update("""
            UPDATE shift SET opening_status = CASE opening_status WHEN 'Failed' THEN 'Pending' ELSE opening_status END,
                closing_status = CASE closing_status WHEN 'Failed' THEN 'Pending' ELSE closing_status END,
                attempts = 0, next_attempt_at = NULL
            WHERE client_id = @id
            """, clientId);

    /// <summary>Shift documents waiting for upload: openings, and closings of closed shifts.</summary>
    public int CountPending() => Count("Pending");

    public int CountFailed() => Count("Failed");

    private int Count(string status)
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, """
            SELECT (SELECT COUNT(*) FROM shift WHERE opening_status = @s)
                 + (SELECT COUNT(*) FROM shift WHERE closed_at IS NOT NULL AND closing_status = @s)
            """, ("@s", status)), CultureInfo.InvariantCulture);
    }

    private static (string Status, string Name) Columns(ShiftDocument document) =>
        document == ShiftDocument.Opening ? ("opening_status", "erp_opening") : ("closing_status", "erp_closing");

    private static ShiftSyncInfo ReadSync(SqliteDataReader r, int i) =>
        new(Enum.Parse<UploadStatus>(r.GetString(i)), SqlExt.Str(r, i + 1), Enum.Parse<UploadStatus>(r.GetString(i + 2)), SqlExt.Str(r, i + 3),
            SqlExt.Str(r, i + 4), r.GetInt32(i + 5), SqlExt.Instant(r, i + 6));

    private void Update(string sql, string clientId, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        if (c.Exec(null, sql, [("@id", clientId), .. extra]) == 0 && c.Scalar(null, "SELECT 1 FROM shift WHERE client_id = @id", ("@id", clientId)) is null)
            throw new KeyNotFoundException($"Shift {clientId} not found.");
    }
}
