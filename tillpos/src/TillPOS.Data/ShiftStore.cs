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

    private const string SyncColumns =
        "opening_status, erp_opening, closing_status, erp_closing, last_error, attempts, next_attempt_at, unknown_attempts, opening_error, closing_error";

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

    // Each document keeps its own error (opening_error, closing_error); last_error mirrors the latest one for older readers.

    public void MarkSynced(string clientId, ShiftDocument document, string erpName)
    {
        var (status, name, error, _) = Columns(document);
        Update($"""
            UPDATE shift SET {status} = 'Synced', {name} = @n, {error} = NULL, last_error = NULL, attempts = 0, next_attempt_at = NULL,
                unknown_attempts = 0
            WHERE client_id = @id
            """, clientId, ("@n", erpName));
    }

    /// <summary>A failure reported after the document was already uploaded is ignored. <paramref name="keepUnknown"/>: the failure
    /// is the escalation of unanswered writes, so the count stays (later tries only look the document up).</summary>
    public void MarkFailed(string clientId, ShiftDocument document, string error, DateTimeOffset? nextAttemptAt, bool keepUnknown = false)
    {
        var (status, _, errorColumn, _) = Columns(document);
        Update($"""
            UPDATE shift SET {status} = 'Failed', {errorColumn} = @e, last_error = @e, attempts = attempts + 1, next_attempt_at = @next,
                unknown_attempts = CASE WHEN @keep = 1 THEN unknown_attempts ELSE 0 END
            WHERE client_id = @id AND {status} IN ('Pending', 'Failed')
            """,
            clientId, ("@e", error), ("@next", SqlExt.Instant(nextAttemptAt)), ("@keep", keepUnknown ? 1 : 0));
    }

    /// <summary>Written just before the document is sent (see <see cref="ReceiptStore.MarkInFlight"/>); false when it is no
    /// longer Pending or Failed.</summary>
    public bool MarkInFlight(string clientId, ShiftDocument document, DateTimeOffset until)
    {
        var (status, _, error, _) = Columns(document);
        return Update($"""
            UPDATE shift SET {status} = 'Pending', {error} = @e, last_error = @e, next_attempt_at = @u
            WHERE client_id = @id AND {status} IN ('Pending', 'Failed')
            """, clientId, ("@e", ReceiptStore.InFlight), ("@u", SqlExt.Instant(until))) > 0;
    }

    /// <summary>A write of the document got no answer (see <see cref="ReceiptStore.MarkUnknown"/>).</summary>
    public void MarkUnknown(string clientId, ShiftDocument document, string error)
    {
        var (status, _, errorColumn, _) = Columns(document);
        Update($"""
            UPDATE shift SET unknown_attempts = unknown_attempts + 1, {errorColumn} = @e, last_error = @e
            WHERE client_id = @id AND {status} IN ('Pending', 'Failed')
            """, clientId, ("@e", error));
    }

    /// <summary>Failed, excluded and handled openings and closings, oldest shift first (the Upload problems screen).</summary>
    public IReadOnlyList<OutboxProblem> Problems()
    {
        using var c = db.Open();
        var rows = c.Query("""
            SELECT client_id, opened_at, closed_at, opening_status, closing_status, opening_error, closing_error, attempts FROM shift
            WHERE opening_status IN ('Failed', 'Excluded', 'Handled')
               OR (closed_at IS NOT NULL AND closing_status IN ('Failed', 'Excluded', 'Handled'))
            ORDER BY opened_at, client_id
            """,
            r => (Id: r.GetString(0), Opened: SqlExt.Instant(r, 1)!.Value, Closed: SqlExt.Instant(r, 2),
                Opening: Enum.Parse<UploadStatus>(r.GetString(3)), Closing: Enum.Parse<UploadStatus>(r.GetString(4)),
                OpeningError: SqlExt.Str(r, 5), ClosingError: SqlExt.Str(r, 6), Attempts: r.GetInt32(7)));
        var problems = new List<OutboxProblem>();
        foreach (var s in rows)
        {
            if (s.Opening is UploadStatus.Failed or UploadStatus.Excluded or UploadStatus.Handled)
                problems.Add(new OutboxProblem(OutboxKind.Opening, s.Id, s.Id, s.Opened, s.Opening, s.OpeningError, s.Attempts));
            if (s.Closed is { } closed && s.Closing is UploadStatus.Failed or UploadStatus.Excluded or UploadStatus.Handled)
                problems.Add(new OutboxProblem(OutboxKind.Closing, s.Id, s.Id, closed, s.Closing, s.ClosingError, s.Attempts));
        }
        return problems;
    }

    /// <summary>A supervisor dealt with a failed or excluded shift document by hand in ERPNext: it is never uploaded.
    /// <paramref name="note"/> is kept as the document's error, and the status it had is kept for <see cref="Unhandle"/>.
    /// <paramref name="erpName"/>: the document's name in ERPNext when the supervisor gave it (an opening's bills can then go).
    /// False when the document was no longer Failed or Excluded (nothing changed).</summary>
    public bool MarkHandled(string clientId, ShiftDocument document, string note, string? erpName = null)
    {
        var (status, name, error, before) = Columns(document);
        return Update($"""
            UPDATE shift SET {before} = {status}, {status} = 'Handled', {error} = @n, last_error = @n, {name} = COALESCE(@erp, {name}),
                next_attempt_at = NULL, unknown_attempts = 0
            WHERE client_id = @id AND {status} IN ('Failed', 'Excluded')
            """, clientId, ("@n", note), ("@erp", erpName)) > 0;
    }

    /// <summary>Takes back a handled shift document: an excluded one is excluded again, a failed one goes back to the queue
    /// (backoff reset). False when it was no longer Handled.</summary>
    public bool Unhandle(string clientId, ShiftDocument document)
    {
        var (status, name, error, before) = Columns(document);
        return Update($"""
            UPDATE shift SET {status} = CASE {before} WHEN 'Excluded' THEN 'Excluded' ELSE 'Pending' END, {before} = NULL, {name} = NULL,
                {error} = NULL, last_error = NULL, attempts = 0, next_attempt_at = NULL, unknown_attempts = 0
            WHERE client_id = @id AND {status} = 'Handled'
            """, clientId) > 0;
    }

    /// <summary>A failed shift document goes back to the queue at once, its backoff reset. False when none was failed.</summary>
    public bool Retry(string clientId) =>
        Update("""
            UPDATE shift SET opening_status = CASE opening_status WHEN 'Failed' THEN 'Pending' ELSE opening_status END,
                closing_status = CASE closing_status WHEN 'Failed' THEN 'Pending' ELSE closing_status END,
                attempts = 0, next_attempt_at = NULL, unknown_attempts = 0
            WHERE client_id = @id AND (opening_status = 'Failed' OR closing_status = 'Failed')
            """, clientId) > 0;

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
    /// <returns>False when nothing of the shift was excluded (nothing changed).</returns>
    public bool Include(string clientId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var changed = IncludeWhere(c, tx, "WHERE client_id = @id", "WHERE shift_client_id = @id",
            "WHERE json_extract(json, '$.ShiftClientId') = @id", ("@id", clientId));
        tx.Commit();
        return changed > 0;
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
                next_attempt_at = NULL, unknown_attempts = 0
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

    private static int IncludeWhere(SqliteConnection c, SqliteTransaction tx, string shiftWhere, string receiptWhere, string approvalWhere,
        params (string Name, object? Value)[] ps) =>
        c.Exec(tx, $"""
            UPDATE shift SET opening_status = CASE opening_status WHEN 'Excluded' THEN 'Pending' ELSE opening_status END,
                closing_status = CASE closing_status WHEN 'Excluded' THEN 'Pending' ELSE closing_status END
            {And(shiftWhere, "(opening_status = 'Excluded' OR closing_status = 'Excluded')")}
            """, ps)
        + c.Exec(tx, $"UPDATE receipt SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL, unknown_attempts = 0 {And(receiptWhere, "sync_status = 'Excluded'")}", ps)
        + c.Exec(tx, $"UPDATE approval_log SET sync_status = 'Pending', attempts = 0, next_attempt_at = NULL, unknown_attempts = 0 {And(approvalWhere, "sync_status = 'Excluded'")}", ps);

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

    private static (string Status, string Name, string Error, string Before) Columns(ShiftDocument document) =>
        document == ShiftDocument.Opening
            ? ("opening_status", "erp_opening", "opening_error", "opening_before_handled")
            : ("closing_status", "erp_closing", "closing_error", "closing_before_handled");

    private static ShiftSyncInfo ReadSync(SqliteDataReader r, int i) =>
        new(Enum.Parse<UploadStatus>(r.GetString(i)), SqlExt.Str(r, i + 1), Enum.Parse<UploadStatus>(r.GetString(i + 2)), SqlExt.Str(r, i + 3),
            SqlExt.Str(r, i + 4), r.GetInt32(i + 5), SqlExt.Instant(r, i + 6), r.GetInt32(i + 7), SqlExt.Str(r, i + 8), SqlExt.Str(r, i + 9));

    private int Update(string sql, string clientId, params (string Name, object? Value)[] extra)
    {
        using var c = db.Open();
        var changed = c.Exec(null, sql, [("@id", clientId), .. extra]);
        if (changed == 0 && c.Scalar(null, "SELECT 1 FROM shift WHERE client_id = @id", ("@id", clientId)) is null)
            throw new KeyNotFoundException($"Shift {clientId} not found.");
        return changed;
    }
}
