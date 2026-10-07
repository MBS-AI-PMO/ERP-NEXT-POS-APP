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
            WHERE opening_status IN ('Pending', 'Failed') OR (closed_at IS NOT NULL AND closing_status IN ('Pending', 'Failed'))
               OR EXISTS (SELECT 1 FROM receipt WHERE receipt.shift_client_id = shift.client_id AND receipt.sync_status IN ('Pending', 'Failed'))
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
            WHERE client_id = @id AND {status} IN ('Pending', 'Failed')
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

    /// <summary>Failed and excluded openings and closings, oldest shift first (the Upload problems screen).</summary>
    public IReadOnlyList<OutboxProblem> Problems()
    {
        using var c = db.Open();
        var rows = c.Query("""
            SELECT client_id, opened_at, closed_at, opening_status, closing_status, last_error, attempts FROM shift
            WHERE opening_status IN ('Failed', 'Excluded') OR (closed_at IS NOT NULL AND closing_status IN ('Failed', 'Excluded'))
            ORDER BY opened_at, client_id
            """,
            r => (Id: r.GetString(0), Opened: SqlExt.Instant(r, 1)!.Value, Closed: SqlExt.Instant(r, 2),
                Opening: Enum.Parse<UploadStatus>(r.GetString(3)), Closing: Enum.Parse<UploadStatus>(r.GetString(4)), Error: SqlExt.Str(r, 5),
                Attempts: r.GetInt32(6)));
        var problems = new List<OutboxProblem>();
        foreach (var s in rows)
        {
            if (s.Opening is UploadStatus.Failed or UploadStatus.Excluded)
                problems.Add(new OutboxProblem(OutboxKind.Opening, s.Id, s.Id, s.Opened, s.Opening, s.Error, s.Attempts));
            if (s.Closed is { } closed && s.Closing is UploadStatus.Failed or UploadStatus.Excluded)
                problems.Add(new OutboxProblem(OutboxKind.Closing, s.Id, s.Id, closed, s.Closing, s.Error, s.Attempts));
        }
        return problems;
    }

    /// <summary>A supervisor dealt with a failed or excluded shift document by hand in ERPNext: it is never uploaded.</summary>
    public void MarkHandled(string clientId, ShiftDocument document)
    {
        var (status, _) = Columns(document);
        Update($"UPDATE shift SET {status} = 'Handled', next_attempt_at = NULL WHERE client_id = @id AND {status} IN ('Failed', 'Excluded')",
            clientId);
    }

    /// <summary>A failed shift document goes back to the queue at once, its backoff reset.</summary>
    public void Retry(string clientId) =>
        Update("""
            UPDATE shift SET opening_status = CASE opening_status WHEN 'Failed' THEN 'Pending' ELSE opening_status END,
                closing_status = CASE closing_status WHEN 'Failed' THEN 'Pending' ELSE closing_status END,
                attempts = 0, next_attempt_at = NULL
            WHERE client_id = @id
            """, clientId);

    /// <summary>The first switch to Live: every shift <b>closed</b> before <paramref name="since"/> with something still to upload
    /// is excluded (with its bills and approvals of before then), and so is every approval of before then that names no shift.
    /// Nothing created at or after <paramref name="since"/> is excluded. Returns how many shifts were excluded.</summary>
    public int ExcludeClosedBefore(DateTimeOffset since)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var at = SqlExt.Instant(since);
        var ids = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT client_id FROM shift WHERE closed_at IS NOT NULL AND closed_at < @at AND (opening_status IN ('Pending', 'Failed')
                    OR closing_status IN ('Pending', 'Failed')
                    OR EXISTS (SELECT 1 FROM receipt WHERE receipt.shift_client_id = shift.client_id AND receipt.sync_status IN ('Pending', 'Failed')))
                """;
            cmd.Parameters.AddWithValue("@at", at);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        foreach (var id in ids) ExcludeShift(c, tx, id, at!);
        c.Exec(tx, "UPDATE approval_log SET sync_status = 'Excluded' WHERE at < @at AND sync_status IN ('Pending', 'Failed')", ("@at", at));
        tx.Commit();
        return ids.Count;
    }

    /// <summary>Puts an excluded shift back in the queue: its excluded opening, closing, bills and approvals become Pending.</summary>
    public void Include(string clientId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        IncludeWhere(c, tx, "WHERE client_id = @id", "WHERE shift_client_id = @id", "WHERE json_extract(json, '$.ShiftClientId') = @id",
            ("@id", clientId));
        tx.Commit();
    }

    /// <summary>Puts every excluded document (shifts, bills, approvals) back in the queue.</summary>
    public void IncludeAll()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        IncludeWhere(c, tx, "", "", "");
        tx.Commit();
    }

    /// <summary>When the earliest shift on the till was opened, or null with no shifts.</summary>
    public DateTimeOffset? EarliestOpening()
    {
        using var c = db.Open();
        return c.Scalar(null, "SELECT MIN(opened_at) FROM shift") is string at
            ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    private static void ExcludeShift(SqliteConnection c, SqliteTransaction tx, string id, string before)
    {
        c.Exec(tx, """
            UPDATE shift SET opening_status = CASE WHEN opening_status IN ('Pending', 'Failed') THEN 'Excluded' ELSE opening_status END,
                closing_status = CASE WHEN closing_status IN ('Pending', 'Failed') THEN 'Excluded' ELSE closing_status END,
                next_attempt_at = NULL
            WHERE client_id = @id
            """, ("@id", id));
        c.Exec(tx, """
            UPDATE receipt SET sync_status = 'Excluded'
            WHERE shift_client_id = @id AND created_at < @before AND sync_status IN ('Pending', 'Failed')
            """, ("@id", id), ("@before", before));
        c.Exec(tx, """
            UPDATE approval_log SET sync_status = 'Excluded'
            WHERE json_extract(json, '$.ShiftClientId') = @id AND at < @before AND sync_status IN ('Pending', 'Failed')
            """, ("@id", id), ("@before", before));
    }

    private static void IncludeWhere(SqliteConnection c, SqliteTransaction tx, string shiftWhere, string receiptWhere, string approvalWhere,
        params (string Name, object? Value)[] ps)
    {
        c.Exec(tx, $"""
            UPDATE shift SET opening_status = CASE opening_status WHEN 'Excluded' THEN 'Pending' ELSE opening_status END,
                closing_status = CASE closing_status WHEN 'Excluded' THEN 'Pending' ELSE closing_status END
            {shiftWhere}
            """, ps);
        c.Exec(tx, $"UPDATE receipt SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL {And(receiptWhere, "sync_status = 'Excluded'")}", ps);
        c.Exec(tx, $"UPDATE approval_log SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL {And(approvalWhere, "sync_status = 'Excluded'")}", ps);
    }

    private static string And(string where, string condition) => where.Length == 0 ? $"WHERE {condition}" : $"{where} AND {condition}";

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
