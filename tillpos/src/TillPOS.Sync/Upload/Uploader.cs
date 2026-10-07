using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Upload;

/// <param name="Uploaded">Documents that reached ERPNext in this run (inserted, or found there by their client id).</param>
/// <param name="Waiting">Documents still to upload (bills, shift documents, approvals), after the run.</param>
/// <param name="Failed">Documents ERPNext refused (or whose totals differ), after the run.</param>
/// <param name="Problems">What went wrong or is waiting in this run, for the header and "Upload problems".</param>
public sealed record UploadReport(int Uploaded, int Waiting, int Failed, IReadOnlyList<string> Problems);

/// <summary>Uploads the till's outbox to ERPNext, oldest shift first: its POS Opening Shift, then its bills oldest first (a return
/// waits until its original sale is in ERPNext), then its POS Closing Shift once the shift is closed and every bill is in;
/// approvals go last, after the shift and bill they mention.
/// <para>Idempotent: before any insert the document's client id is looked up (read-only); a document already in ERPNext is
/// adopted, never inserted twice (e.g. when the answer to an earlier insert was lost). Invoices are compared with the till's
/// totals, and a difference over the POS Profile write-off limit is a failure, never a silent re-price.</para>
/// <para>Each failed document backs off (30 s, doubling to 5 min); the others carry on. Network errors stop the run (the next
/// run starts over). Off does nothing. DryRun builds every payload and runs the read-only lookups, hands each payload's JSON
/// to the preview callback once per session, and marks nothing. Only Live gets a writer (<see cref="UploadPipeline"/>).</para></summary>
public sealed class Uploader
{
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    public const int MaxErrorLength = 300;

    /// <summary>How long a sent document is left alone when its answer is lost (see UploadAsync).</summary>
    public static readonly TimeSpan InFlightHold = TimeSpan.FromMinutes(5);
    private const string InvoiceIdField = "posa_client_request_id";
    private const string OfflineIdField = "custom_offline_id";

    private readonly IErpClient reader;
    private readonly IErpWriter writer;
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly Func<string, PosSettings?> profileSettings;
    private readonly string till;
    private readonly Func<DateTimeOffset> now;
    private readonly Action<string, string> preview;
    private readonly HashSet<string> previewed = [];
    private string? erpUser;

    /// <param name="reader">Read-only ERPNext access (lookups).</param>
    /// <param name="writer">Null unless <paramref name="mode"/> is Live (enforced).</param>
    /// <param name="profileSettings">The synced POS settings of a shift's counter (blank = the default counter), or null.</param>
    /// <param name="till">This till's name, sent as custom_till (e.g. "TILL2").</param>
    /// <param name="erpUser">The till's ERPNext user for the shifts; null = ask ERPNext who the API key belongs to.</param>
    /// <param name="preview">DryRun: receives a file name (without extension) and the payload JSON.</param>
    /// <param name="testBuild">A test build (local test cashiers or the sample QR): Live is refused.</param>
    public Uploader(IErpClient reader, IErpWriter? writer, UploadMode mode, ShiftStore shifts, ReceiptStore receipts, ApprovalStore approvals,
        Func<string, PosSettings?> profileSettings, string till, string? erpUser, Func<DateTimeOffset> now, Action<string, string> preview,
        bool testBuild = false)
    {
        this.writer = UploadPipeline.WriterFor(mode, writer, testBuild);
        this.reader = reader;
        Mode = mode;
        this.shifts = shifts;
        this.receipts = receipts;
        this.approvals = approvals;
        this.profileSettings = profileSettings;
        this.till = till;
        this.erpUser = string.IsNullOrWhiteSpace(erpUser) ? null : erpUser;
        this.now = now;
        this.preview = preview;
    }

    public UploadMode Mode { get; }

    /// <summary>The synced Sales Taxes and Charges Template by name (the Closing Shift's taxes); none by default.</summary>
    public Func<string, SalesTaxTemplate?> TaxTemplates { get; init; } = _ => null;

    /// <summary>Where unexpected exceptions go (errors.log); network errors and ERPNext's answers are not logged.</summary>
    public Action<Exception> LogError { get; init; } = _ => { };

    /// <summary>The wait after the <paramref name="attempts"/>-th failure in a row: 30 s, 60 s, 120 s, 240 s, then 5 min.</summary>
    public static TimeSpan Backoff(int attempts) =>
        attempts <= 1 ? FirstBackoff : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Min(attempts - 1, 10)));

    /// <summary>The outbox counts, from the till only (no ERPNext call).</summary>
    public UploadReport Counts(IReadOnlyList<string>? problems = null, int uploaded = 0) =>
        new(uploaded, receipts.CountPending() + shifts.CountPending() + approvals.CountPending(),
            receipts.CountFailed() + shifts.CountFailed() + approvals.CountFailed(), problems ?? []);

    public async Task<UploadReport> RunOnceAsync(CancellationToken ct = default)
    {
        if (Mode == UploadMode.Off) return Counts();
        var run = new Run();
        try
        {
            erpUser ??= (await reader.PingAsync(ct)).User;
            foreach (var shift in shifts.Unfinished()) await ShiftAsync(shift, erpUser, run, ct);
            await ApprovalsAsync(run, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Not an answer about one document (ERPNext unreachable, timeout, …): stop here, start over on the next run.
            if (ex is not (ErpException or HttpRequestException or TaskCanceledException or TimeoutException or IOException)) LogError(ex);
            run.Problems.Add($"Upload stopped: {Short(ex.Message)}");
        }
        return Counts(run.Problems, run.Uploaded);
    }

    private async Task ShiftAsync(ShiftOutboxEntry entry, string user, Run run, CancellationToken ct)
    {
        var opening = entry.Opening;
        var id = opening.ClientId;
        if (profileSettings(opening.Counter) is not { } profile)
        {
            run.Problems.Add($"Shift {id}: the POS settings of counter '{opening.Counter}' are not on the till yet.");
            return;
        }

        // An excluded shift (from before the till went Live) is never uploaded; bills taken in it since are excluded too.
        var sync = entry.Sync;
        if (sync.OpeningStatus == UploadStatus.Excluded)
        {
            if (Mode == UploadMode.Live) shifts.Exclude(id);
            return;
        }

        // 1. POS Opening Shift.
        var openingName = sync.OpeningStatus == UploadStatus.Synced ? sync.ErpOpeningName : run.Planned(OpeningKey(id));
        if (openingName is null)
        {
            if (!Due(sync.NextAttemptAt, $"Opening of shift {id}", sync.LastError, run)) return;
            openingName = await UploadAsync(
                new Doc(OpeningShiftPayload.Doctype, OfflineIdField, id, OpeningKey(id), $"Opening of shift {id}", true,
                    () => OpeningShiftPayload.Build(opening, profile.PosProfile, profile.Company, user)),
                new Marks(sync.Attempts, name => shifts.MarkSynced(id, ShiftDocument.Opening, name),
                    (error, next) => shifts.MarkFailed(id, ShiftDocument.Opening, error, next),
                    until => shifts.MarkInFlight(id, ShiftDocument.Opening, until)), run, ct);
            if (openingName is null) return;
        }

        // 2. POS Invoices, oldest first.
        var uploaded = new List<(string ErpName, Receipt Receipt)>();
        var waiting = 0;
        foreach (var (receipt, bill) in receipts.Outbox(id).Select(e => (e.Receipt, e.Sync)))
        {
            if (bill.Status == ReceiptSyncStatus.Excluded) continue;
            var name = bill.Status == ReceiptSyncStatus.Synced ? bill.ErpName : run.Planned(InvoiceKey(receipt.ClientId));
            name ??= await InvoiceAsync(receipt, bill, profile, openingName, run, ct);
            if (name is null) waiting++;
            else uploaded.Add((name, receipt));
        }

        // 3. POS Closing Shift: only for a closed shift whose bills are all in ERPNext.
        if (entry.Closing is not { } closing) return;
        sync = shifts.SyncInfo(id)!;
        if (sync.ClosingStatus == UploadStatus.Synced) return;
        if (waiting > 0)
        {
            run.Problems.Add($"Closing of shift {id} waits: {waiting.ToString(CultureInfo.InvariantCulture)} bill(s) of the shift are not uploaded yet.");
            return;
        }
        if (!Due(sync.NextAttemptAt, $"Closing of shift {id}", sync.LastError, run)) return;
        await UploadAsync(
            new Doc(ClosingShiftPayload.Doctype, OfflineIdField, id, ClosingKey(id), $"Closing of shift {id}", true,
                () => ClosingShiftPayload.Build(opening, closing, openingName, uploaded, profile.PosProfile, profile.Company, user,
                    profile.Customer, profile.TaxesAndCharges is { Length: > 0 } taxes ? TaxTemplates(taxes) : null)),
            new Marks(sync.Attempts, name => shifts.MarkSynced(id, ShiftDocument.Closing, name),
                (error, next) => shifts.MarkFailed(id, ShiftDocument.Closing, error, next),
                until => shifts.MarkInFlight(id, ShiftDocument.Closing, until)), run, ct);
    }

    /// <summary>Uploads one bill; returns its ERPNext name (DryRun: a stand-in), or null when it failed or waits.</summary>
    private async Task<string?> InvoiceAsync(Receipt receipt, ReceiptSyncInfo bill, PosSettings profile, string openingName, Run run,
        CancellationToken ct)
    {
        var label = $"Bill {receipt.ClientId}";
        if (!Due(bill.NextAttemptAt, label, bill.LastError, run)) return null;

        string? returnAgainst = null;
        if (receipt.Kind == ReceiptKind.Return && receipt.ReturnAgainst is { Length: > 0 } originalId)
        {
            // A return with a receipt is never sent without return_against: it waits for its original sale.
            returnAgainst = OriginalName(originalId, run);
            if (returnAgainst is null)
            {
                run.Problems.Add($"Return {receipt.ClientId} waits for its original sale {originalId} to upload.");
                return null;
            }
        }

        var payload = PosInvoicePayload.Build(receipt, profile.PosProfile, profile, openingName, receipt.CashierUser, till, returnAgainst);
        return await UploadAsync(
            new Doc(PosInvoicePayload.Doctype, InvoiceIdField, receipt.ClientId, InvoiceKey(receipt.ClientId), label, true, () => payload.Doc,
                payload.Expected, profile.WriteOffLimit),
            new Marks(bill.Attempts, name => receipts.MarkSynced(receipt.ClientId, name),
                (error, next) => receipts.MarkFailed(receipt.ClientId, error, next), until => receipts.MarkInFlight(receipt.ClientId, until)),
            run, ct);
    }

    /// <summary>The ERPNext name of a sale on this till (DryRun: a stand-in when it would be uploaded in this run), or null.</summary>
    private string? OriginalName(string clientId, Run run)
    {
        ReceiptSyncInfo info;
        try
        {
            info = receipts.SyncInfo(clientId);
        }
        catch (KeyNotFoundException)
        {
            return null; // not on this till: cross-till returns come with Task 5
        }
        return info.Status == ReceiptSyncStatus.Synced ? info.ErpName : run.Planned(InvoiceKey(clientId));
    }

    private async Task ApprovalsAsync(Run run, CancellationToken ct)
    {
        foreach (var entry in approvals.Outbox())
        {
            var a = entry.Record;
            var label = $"Approval {a.Action} ({a.Id})";
            // Approvals go after the shift and the bill they mention; one that names neither (or a bill that was never
            // saved, e.g. a voided line) goes without the link.
            string? shiftName = null;
            if (!string.IsNullOrEmpty(a.ShiftClientId) && shifts.SyncInfo(a.ShiftClientId) is { } shift)
            {
                if (shift.OpeningStatus == UploadStatus.Excluded) continue;
                shiftName = shift.OpeningStatus == UploadStatus.Synced ? shift.ErpOpeningName : run.Planned(OpeningKey(a.ShiftClientId));
                if (shiftName is null) continue;
            }
            string? invoiceName = null;
            if (!string.IsNullOrEmpty(a.ReceiptClientId) && receipts.Get(a.ReceiptClientId) is not null)
            {
                invoiceName = OriginalName(a.ReceiptClientId, run);
                if (invoiceName is null) continue;
            }
            if (!Due(entry.NextAttemptAt, label, entry.LastError, run)) continue;
            await UploadAsync(
                new Doc(ApprovalPayload.Doctype, OfflineIdField, a.Id, $"APPROVAL-{a.Id}", label, false,
                    () => ApprovalPayload.Build(a, till, shiftName, invoiceName)),
                new Marks(entry.Attempts, name => approvals.MarkUploaded(a.Id, name), (error, next) => approvals.MarkFailed(a.Id, error, next),
                    until => approvals.MarkInFlight(a.Id, until)), run, ct);
        }
    }

    /// <summary>A document to upload. Submittable documents are only adopted when submitted (docstatus 1).</summary>
    private sealed record Doc(string Doctype, string IdField, string ClientId, string Key, string Label, bool Submittable,
        Func<Dictionary<string, object?>> Build, ExpectedTotals? Expected = null, decimal WriteOffLimit = 0m);

    /// <summary>How a document's upload state is written: synced (with its ERPNext name), failed (error, next try) and in flight
    /// (sent, answer pending, until).</summary>
    private sealed record Marks(int Attempts, Action<string> Synced, Action<string, DateTimeOffset> Failed, Action<DateTimeOffset> InFlight);

    /// <summary>A document found in (or just written to) ERPNext: its name, its fields and its docstatus (0 draft, 1 submitted).</summary>
    private sealed record Found(string Name, JsonElement Doc, int DocStatus);

    /// <summary>Lookup, then insert as a draft, check, and submit (Live) or preview (DryRun). Returns the ERPNext name (DryRun:
    /// the found name or a stand-in), or null when the document failed. Only ERPNext's definite refusals (<see cref="Classify"/>)
    /// and the till's own checks are recorded on the document, with a backoff; an unknown outcome (network, timeout, gateway,
    /// auth, rate limit) propagates and stops the run.
    /// <para>Submittable documents go in as a draft (docstatus 0); an invoice's totals are compared with the till's before it is
    /// submitted, so a wrong price never becomes a submitted document. A draft of this till's own client id found by the lookup
    /// is checked and submitted the same way.</para>
    /// <para>Before each write the document is marked in flight for <see cref="InFlightHold"/>: if the answer is lost, it is not
    /// sent again before then, and the next try looks it up first. The hold (5 min) is longer than the client's 60 s timeout
    /// and gunicorn's 120 s worker timeout, so a request ERPNext is still working on has finished before the lookup.</para></summary>
    private async Task<string?> UploadAsync(Doc doc, Marks marks, Run run, CancellationToken ct)
    {
        if (Mode == UploadMode.DryRun && previewed.Contains(doc.Key)) return run.Plan(doc.Key, doc.ClientId);
        try
        {
            var found = await LookupAsync(doc, ct);
            if (Mode == UploadMode.DryRun)
            {
                if (found is not null) return found.Name;
                preview(doc.Key, ErpFormat.Json(doc.Build(), indented: true));
                previewed.Add(doc.Key);
                return run.Plan(doc.Key, doc.ClientId);
            }

            found ??= await InsertDraftAsync(doc, marks, ct);
            var draft = doc.Submittable && found.DocStatus == 0;
            if (Mismatch(doc, found) is { } mismatch)
                throw new DocumentFailure(draft ? $"Draft {found.Name} created in ERPNext but totals differ — check and submit or delete it. {mismatch}" : mismatch);
            if (draft)
            {
                found = await SubmitAsync(doc, found, marks, ct);
                if (Mismatch(doc, found) is { } afterSubmit) throw new DocumentFailure(afterSubmit);
            }
            marks.Synced(found.Name);
            run.Uploaded++;
            return found.Name;
        }
        catch (Exception ex) when (ex is DocumentFailure || Classify(ex) == ErpOutcome.Refused)
        {
            var error = Short(ex.Message);
            marks.Failed(error, now() + Backoff(marks.Attempts + 1));
            run.Problems.Add($"{doc.Label}: {error}");
            return null;
        }
    }

    /// <summary>Marks the document in flight and inserts it (a submittable one as a draft). ERPNext saying it already has the
    /// document (a unique client id) means an earlier insert went through: it is looked up and adopted.</summary>
    private async Task<Found> InsertDraftAsync(Doc doc, Marks marks, CancellationToken ct)
    {
        var body = doc.Build();
        if (doc.Submittable) body["docstatus"] = 0;
        marks.InFlight(now() + InFlightHold);
        JsonElement saved;
        try
        {
            saved = await writer.InsertAsync(doc.Doctype, body, ct);
        }
        catch (Exception ex) when (Classify(ex) == ErpOutcome.Duplicate)
        {
            return await LookupAsync(doc, ct)
                ?? throw new DocumentFailure($"ERPNext reports this {doc.Doctype} as a duplicate, but none has this till's id: {Short(ex.Message)}");
        }
        // No name in the answer: the outcome is unknown, the in-flight marker stays and the next try looks it up.
        var name = saved.StrOrNull("name") ?? throw new InvalidDataException($"ERPNext answered the {doc.Doctype} insert without its name.");
        return new Found(name, saved, saved.Int("docstatus"));
    }

    /// <summary>Submits a checked draft. A refusal leaves the draft in ERPNext (the next try finds it and submits it again).</summary>
    private async Task<Found> SubmitAsync(Doc doc, Found draft, Marks marks, CancellationToken ct)
    {
        marks.InFlight(now() + InFlightHold);
        try
        {
            var submitted = await writer.SubmitAsync(doc.Doctype, draft.Name, ct);
            return new Found(draft.Name, submitted, 1);
        }
        catch (Exception ex) when (Classify(ex) == ErpOutcome.Refused)
        {
            throw new DocumentFailure($"Draft {draft.Name} is in ERPNext but could not be submitted: {Short(ex.Message)}");
        }
    }

    public enum ErpOutcome { Unknown, Refused, Duplicate }

    private static readonly string[] DuplicateMarkers = ["DuplicateEntryError", "UniqueValidationError", "Duplicate entry", "already exists"];
    private static readonly string[] RefusalTypes = ["ValidationError", "MandatoryError", "LinkValidationError", "PermissionError"];

    /// <summary>What an error says about a document: Duplicate (ERPNext already has it), Refused (ERPNext checked it and said no:
    /// the validation and permission errors, HTTP 417), or Unknown (network, timeout, gateway 502/503/504, 408, 429, 401, an
    /// unreadable answer, a server error): nothing is known about the document, so it is left as it is.</summary>
    public static ErpOutcome Classify(Exception ex)
    {
        if (ex is not ErpException erp) return ErpOutcome.Unknown;
        var text = $"{erp.ExcType} {erp.Message}";
        if (DuplicateMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return ErpOutcome.Duplicate;
        if (erp.ExcType is { } type && RefusalTypes.Contains(type, StringComparer.Ordinal)) return ErpOutcome.Refused;
        return erp.StatusCode == 417 ? ErpOutcome.Refused : ErpOutcome.Unknown;
    }

    private static readonly string[] InvoiceFields =
        ["name", "docstatus", "grand_total", "rounded_total", "paid_amount", "change_amount", "outstanding_amount"];

    /// <summary>The document with this client id in ERPNext (submitted first, else this till's own draft), or null. One found
    /// only cancelled is a failure to check by hand: it is never adopted and never inserted again.</summary>
    private async Task<Found?> LookupAsync(Doc doc, CancellationToken ct)
    {
        IReadOnlyList<string> fields = doc.Expected is null ? ["name", "docstatus"] : InvoiceFields;
        var rows = await reader.GetListAsync(new ListQuery(doc.Doctype, fields, [[doc.IdField, "=", doc.ClientId]], "creation asc", 0, 10), ct);
        if (rows.Count == 0) return null;
        if (!doc.Submittable) return new Found(rows[0].Str("name"), rows[0], 0);
        foreach (var status in new[] { 1, 0 })
            foreach (var row in rows)
                if (row.Int("docstatus") == status)
                    return new Found(row.Str("name"), row, status);
        throw new DocumentFailure(
            $"{doc.Doctype} {string.Join(", ", rows.Select(r => r.StrOrNull("name")))} has this till's id but is cancelled in ERPNext. Check it there.");
    }

    /// <summary>Why ERPNext's invoice is not what the till charged, or null: grand and rounded total, and what was paid (paid −
    /// change) against the amount due, each within the write-off limit; once submitted, nothing may be outstanding.</summary>
    private static string? Mismatch(Doc doc, Found found)
    {
        if (doc.Expected is not { } till) return null;
        var erp = found.Doc;
        var grand = erp.Dec("grand_total");
        var rounded = erp.Dec("rounded_total");
        var limit = doc.WriteOffLimit;
        var problems = new List<string>();
        if (Math.Abs(grand - till.GrandTotal) > limit || (till.UsesRoundedTotal && Math.Abs(rounded - till.RoundedTotal) > limit))
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"ERPNext's total is {grand} (rounded {rounded}), but the till charged {till.GrandTotal} (rounded {till.RoundedTotal})"));
        var due = rounded != 0m ? rounded : grand;
        var paid = erp.Dec("paid_amount") - erp.Dec("change_amount");
        if (Math.Abs(paid - due) > limit)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"paid {paid} (after change) against {due} due"));
        if (found.DocStatus == 1 && erp.Dec("outstanding_amount") != 0m)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{erp.Dec("outstanding_amount")} is outstanding after submit"));
        if (problems.Count == 0) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{found.Name}: {string.Join("; ", problems)}: over the write-off limit {limit}. Check the invoice in ERPNext.");
    }

    /// <summary>False (and noted) while a failed document waits for its backoff.</summary>
    private bool Due(DateTimeOffset? nextAttemptAt, string label, string? lastError, Run run)
    {
        if (nextAttemptAt is not { } next || next <= now()) return true;
        run.Problems.Add($"{label}: {lastError ?? "failed"} (next try {next.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)})");
        return false;
    }

    private static string Short(string message)
    {
        var text = message.Trim();
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }

    private static string OpeningKey(string shiftId) => $"{shiftId}-opening";
    private static string ClosingKey(string shiftId) => $"{shiftId}-closing";
    private static string InvoiceKey(string clientId) => clientId;

    /// <summary>One run's results. Planned holds DryRun stand-in names for documents that would be created, so the documents
    /// that refer to them can be previewed too.</summary>
    private sealed class Run
    {
        private readonly Dictionary<string, string> planned = [];
        public int Uploaded { get; set; }
        public List<string> Problems { get; } = [];
        public string? Planned(string key) => planned.GetValueOrDefault(key);
        public string Plan(string key, string clientId) => planned[key] = $"(new: {clientId})";
    }

    /// <summary>A definite problem with one document (recorded on it as Failed).</summary>
    private sealed class DocumentFailure(string message) : Exception(message);
}
