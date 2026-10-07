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

/// <summary>Supervisor approvals and their upload state (synced = 1 once in ERPNext).</summary>
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
        foreach (var id in ids) c.Exec(tx, "UPDATE approval_log SET synced = 1, sync_status = 'Synced' WHERE id = @id", ("@id", id));
        tx.Commit();
    }

    /// <summary>Approvals still to upload (pending or failed), oldest first.</summary>
    public IReadOnlyList<ApprovalOutboxEntry> Outbox()
    {
        using var c = db.Open();
        return c.Query("""
            SELECT json, sync_status, erp_name, last_error, attempts, next_attempt_at FROM approval_log
            WHERE sync_status IN ('Pending', 'Failed') ORDER BY at, id
            """,
            r => new ApprovalOutboxEntry(JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(0), Json)!, Enum.Parse<UploadStatus>(r.GetString(1)),
                SqlExt.Str(r, 2), SqlExt.Str(r, 3), r.GetInt32(4), SqlExt.Instant(r, 5)));
    }

    /// <summary>The approval is in ERPNext as <paramref name="erpName"/>.</summary>
    public void MarkUploaded(string id, string erpName) =>
        Update("""
            UPDATE approval_log SET synced = 1, sync_status = 'Synced', erp_name = @n, last_error = NULL, attempts = attempts + 1,
                next_attempt_at = NULL
            WHERE id = @id
            """, id, ("@n", erpName));

    /// <summary>A failure reported after the approval was already uploaded is ignored.</summary>
    public void MarkFailed(string id, string error, DateTimeOffset? nextAttemptAt) =>
        Update("""
            UPDATE approval_log SET sync_status = 'Failed', last_error = @e, attempts = attempts + 1, next_attempt_at = @next
            WHERE id = @id AND sync_status IN ('Pending', 'Failed')
            """, id, ("@e", error), ("@next", SqlExt.Instant(nextAttemptAt)));

    /// <summary>Failed and excluded approvals, oldest first (the Upload problems screen).</summary>
    public IReadOnlyList<OutboxProblem> Problems()
    {
        using var c = db.Open();
        return c.Query("""
            SELECT json, sync_status, last_error, attempts FROM approval_log
            WHERE sync_status IN ('Failed', 'Excluded') ORDER BY at, id
            """,
            r =>
            {
                var record = JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(0), Json)!;
                return new OutboxProblem(OutboxKind.Approval, record.Id, record.ShiftClientId, record.At, Enum.Parse<UploadStatus>(r.GetString(1)),
                    SqlExt.Str(r, 2), r.GetInt32(3));
            });
    }

    /// <summary>The approval with this id, or null.</summary>
    public ApprovalRecord? Get(string id)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM approval_log WHERE id = @id", r => JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(0), Json)!,
            ("@id", id)).FirstOrDefault();
    }

    /// <summary>A supervisor dealt with a failed or excluded approval by hand: it is never uploaded.</summary>
    public void MarkHandled(string id) =>
        Update("UPDATE approval_log SET sync_status = 'Handled', next_attempt_at = NULL WHERE id = @id AND sync_status IN ('Failed', 'Excluded')", id);

    /// <summary>Puts an excluded approval back in the queue.</summary>
    public void Include(string id) =>
        Update("UPDATE approval_log SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL WHERE id = @id AND sync_status = 'Excluded'", id);

    /// <summary>Written just before the approval is sent (see <see cref="ReceiptStore.MarkInFlight"/>).</summary>
    public void MarkInFlight(string id, DateTimeOffset until) =>
        Update("""
            UPDATE approval_log SET sync_status = 'Pending', last_error = @e, next_attempt_at = @u
            WHERE id = @id AND sync_status IN ('Pending', 'Failed')
            """, id, ("@e", ReceiptStore.InFlight), ("@u", SqlExt.Instant(until)));

    /// <summary>A failed approval goes back to the queue at once, its backoff reset.</summary>
    public void Retry(string id) =>
        Update("UPDATE approval_log SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL WHERE id = @id AND sync_status = 'Failed'", id);

    public int CountPending() => Count("Pending");

    public int CountFailed() => Count("Failed");

    private int Count(string status)
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, "SELECT COUNT(*) FROM approval_log WHERE sync_status = @s", ("@s", status)), CultureInfo.InvariantCulture);
    }

    private void Update(string sql, string id, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        if (c.Exec(null, sql, [("@id", id), .. extra]) == 0 && c.Scalar(null, "SELECT 1 FROM approval_log WHERE id = @id", ("@id", id)) is null)
            throw new KeyNotFoundException($"Approval {id} not found.");
    }
}
