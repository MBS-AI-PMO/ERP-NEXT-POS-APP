using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Sync.Upload;

namespace TillPOS.Presentation;

/// <summary>One document on the Upload problems screen.</summary>
public sealed class UploadProblemRow(OutboxProblem problem)
{
    public OutboxProblem Problem { get; } = problem;

    public string Kind => Problem.Kind switch
    {
        OutboxKind.Opening => "Opening shift",
        OutboxKind.Closing => "Closing shift",
        OutboxKind.Approval => "Approval",
        _ => "Bill",
    };

    public string Id => Problem.Id;
    public string Shift => Problem.ShiftId;
    public string Created => Problem.Created.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string Error => Problem.Error ?? "";
    public int Attempts => Problem.Attempts;
    public bool IsExcluded => Problem.Status == UploadStatus.Excluded;
    public bool IsHandled => Problem.Status == UploadStatus.Handled;
    public string Title => $"{Kind} {Id}";
}

/// <summary>Upload problems (supervisor): the documents ERPNext refused (Failed), those left out because they were taken
/// before the till went Live (Excluded), and those a supervisor dealt with by hand (Handled: who, when, why). Retry puts a
/// failed document back in the queue at once; View shows what would be sent; Mark as handled (with a reason and an optional
/// ERPNext reference) is for a document fixed by hand in ERPNext: it is never uploaded; Un-handle takes it back; Include puts
/// an excluded shift (or approval) back in the queue. Each action needs a supervisor PIN and is logged.</summary>
public sealed class UploadProblemsViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SupervisorGate gate;
    private readonly Func<object> back;
    private string message = "";

    public UploadProblemsViewModel(TillContext ctx, SupervisorGate gate, Func<object> back)
    {
        this.ctx = ctx;
        this.gate = gate;
        this.back = back;
        RetryCommand = new AsyncRelayCommand<UploadProblemRow>(RetryAsync);
        ViewCommand = new RelayCommand<UploadProblemRow>(View);
        MarkHandledCommand = new AsyncRelayCommand<UploadProblemRow>(MarkHandledAsync);
        IncludeCommand = new AsyncRelayCommand<UploadProblemRow>(IncludeAsync);
        UnhandleCommand = new AsyncRelayCommand<UploadProblemRow>(UnhandleAsync);
        BackCommand = new RelayCommand(() => ctx.Navigator.Show(back()));
        Reload();
    }

    /// <summary>Documents ERPNext refused, or whose totals differ.</summary>
    public ObservableCollection<UploadProblemRow> Failed { get; } = [];

    /// <summary>Documents from before the till went Live (test data): never uploaded unless included.</summary>
    public ObservableCollection<UploadProblemRow> Excluded { get; } = [];

    /// <summary>Documents a supervisor marked as handled by hand in ERPNext (final; the error column says who, when and why).</summary>
    public ObservableCollection<UploadProblemRow> Handled { get; } = [];

    public string Message { get => message; private set => SetProperty(ref message, value); }
    public AsyncRelayCommand<UploadProblemRow> RetryCommand { get; }
    public RelayCommand<UploadProblemRow> ViewCommand { get; }
    public AsyncRelayCommand<UploadProblemRow> MarkHandledCommand { get; }
    public AsyncRelayCommand<UploadProblemRow> IncludeCommand { get; }
    public AsyncRelayCommand<UploadProblemRow> UnhandleCommand { get; }
    public RelayCommand BackCommand { get; }

    /// <summary>How many documents need a look (failed + excluded), e.g. for the login screen's button; 0 when the stores fail.</summary>
    public static int Count(TillContext ctx) => Counts(ctx).NeedALook;

    /// <summary>The documents that need a look (failed + excluded) and those handled by hand; (0, 0) when the stores fail.</summary>
    public static (int NeedALook, int Handled) Counts(TillContext ctx)
    {
        try
        {
            var all = All(ctx);
            var handled = all.Count(p => p.Status == UploadStatus.Handled);
            return (all.Count - handled, handled);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static List<OutboxProblem> All(TillContext ctx) =>
        [.. ctx.Shifts.Problems(), .. ctx.Receipts.Problems(), .. ctx.Approvals.Problems()];

    /// <summary>Lists the documents again; a store failure shows a message (never throws).</summary>
    public void Reload()
    {
        Failed.Clear();
        Excluded.Clear();
        Handled.Clear();
        try
        {
            foreach (var problem in All(ctx).OrderBy(p => p.Created).ThenBy(p => p.Kind))
                (problem.Status switch { UploadStatus.Excluded => Excluded, UploadStatus.Handled => Handled, _ => Failed })
                    .Add(new UploadProblemRow(problem));
        }
        catch (Exception ex)
        {
            Message = $"Could not read the upload state: {ex.Message}";
        }
    }

    private async Task RetryAsync(UploadProblemRow? row)
    {
        if (row is null || row.IsExcluded || row.IsHandled || !StillListed(row)) return;
        if (await gate.ApproveAsync(ApprovalAction.UploadRetry, $"Retry upload of {row.Title}", ReceiptId(row)) is null) return;
        Act(row, $"{row.Title} will be uploaded again shortly.", () => row.Problem.Kind switch
        {
            OutboxKind.Bill => ctx.Receipts.Retry(row.Id),
            OutboxKind.Approval => ctx.Approvals.Retry(row.Id),
            _ => ctx.Shifts.Retry(row.Id),
        });
    }

    /// <summary>A POS Opening Shift's name in ERPNext (POS Awesome numbers them POSA-OS-…).</summary>
    private static bool LooksLikeOpeningShift(string reference) => reference.StartsWith("POSA-OS-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks why (required) and for the ERPNext reference (optional), then a supervisor; the note "Handled by {supervisor}
    /// ({when}): {reason} {reference}" stays on the document. An opening handled with its POS Opening Shift name lets the shift's
    /// bills go with that name; without it, its waiting bills would wait, so that needs a confirmation.</summary>
    private async Task MarkHandledAsync(UploadProblemRow? row)
    {
        if (row is null || row.IsHandled || !StillListed(row)) return;
        var reason = (await ctx.Dialogs.AskTextAsync("Mark as handled", $"Why is {row.Title} handled by hand?"))?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            Message = "Not marked: a reason is needed.";
            return;
        }
        var reference = (await ctx.Dialogs.AskTextAsync("Mark as handled", "ERPNext document name (optional)"))?.Trim() ?? "";
        string? openingName = null;
        if (row.Problem.Kind == OutboxKind.Opening)
        {
            if (LooksLikeOpeningShift(reference)) openingName = reference;
            else if (WaitingBills(row.Shift) is var waiting and > 0 && !ctx.Dialogs.Confirm("Mark as handled", string.Create(CultureInfo.InvariantCulture,
                         $"{waiting} bill(s) of this shift will wait until its opening is in ERPNext — continue?")))
            {
                Message = "Not marked.";
                return;
            }
        }
        if (await gate.ApproveAsync(ApprovalAction.UploadMarkHandled, $"Mark {row.Title} as handled in ERPNext: {reason} {reference}".Trim(),
                ReceiptId(row)) is not { } supervisor)
            return;
        var when = ctx.Clock.Now.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        var note = $"Handled by {supervisor} ({when}): {reason} {reference}".Trim();
        var done = openingName is null
            ? $"{row.Title} is marked as handled; it will not be uploaded."
            : $"{row.Title} is marked as handled as {openingName}; its bills will upload with it.";
        Act(row, done, () => row.Problem.Kind switch
        {
            OutboxKind.Bill => ctx.Receipts.MarkHandled(row.Id, note),
            OutboxKind.Approval => ctx.Approvals.MarkHandled(row.Id, note),
            OutboxKind.Opening => ctx.Shifts.MarkHandled(row.Id, ShiftDocument.Opening, note, openingName),
            _ => ctx.Shifts.MarkHandled(row.Id, ShiftDocument.Closing, note),
        });
    }

    /// <summary>Takes a handled document back: a failed one into the upload queue, an excluded one back to Excluded.</summary>
    private async Task UnhandleAsync(UploadProblemRow? row)
    {
        if (row is null || !row.IsHandled || !StillListed(row)) return;
        if (await gate.ApproveAsync(ApprovalAction.UploadUnhandle, $"Upload {row.Title} again (no longer handled by hand)", ReceiptId(row)) is null)
            return;
        Act(row, $"{row.Title} is no longer handled by hand.", () => row.Problem.Kind switch
        {
            OutboxKind.Bill => ctx.Receipts.Unhandle(row.Id),
            OutboxKind.Approval => ctx.Approvals.Unhandle(row.Id),
            OutboxKind.Opening => ctx.Shifts.Unhandle(row.Id, ShiftDocument.Opening),
            _ => ctx.Shifts.Unhandle(row.Id, ShiftDocument.Closing),
        });
    }

    /// <summary>An excluded document comes back with its whole shift (its bills need the shift's opening); an approval made
    /// outside a shift comes back alone.</summary>
    private async Task IncludeAsync(UploadProblemRow? row)
    {
        if (row is null || !row.IsExcluded || !StillListed(row)) return;
        var what = row.Shift.Length > 0 ? $"shift {row.Shift}" : row.Title;
        if (await gate.ApproveAsync(ApprovalAction.UploadIncludeHistory, $"Upload {what} (taken before going Live)", ReceiptId(row)) is null) return;
        Act(row, $"{what} will be uploaded.", () => row.Shift.Length > 0 ? ctx.Shifts.Include(row.Shift) : ctx.Approvals.Include(row.Id));
    }

    /// <summary>True when the document is still as listed. Otherwise (changed by the uploader or another screen since) nothing is
    /// asked or logged: the list is refreshed with "changed meanwhile — nothing done".</summary>
    private bool StillListed(UploadProblemRow row)
    {
        bool listed;
        try
        {
            listed = All(ctx).Any(p => p.Kind == row.Problem.Kind && p.Id == row.Id && p.Status == row.Problem.Status);
        }
        catch (Exception ex)
        {
            Message = $"Could not read the upload state: {ex.Message}";
            return false;
        }
        if (listed) return true;
        Message = $"{row.Title} changed meanwhile — nothing done.";
        Reload();
        return false;
    }

    private int WaitingBills(string shiftId)
    {
        try
        {
            return ctx.Receipts.Outbox(shiftId).Count(e => e.Sync.Status is ReceiptSyncStatus.Pending or ReceiptSyncStatus.Failed);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Runs the change; when it changed nothing (the document changed in between), says so.</summary>
    private void Act(UploadProblemRow row, string done, Func<bool> action)
    {
        try
        {
            Message = action() ? done : $"{row.Title} changed meanwhile — nothing done.";
        }
        catch (Exception ex)
        {
            Message = $"Could not change {row.Title}: {ex.Message}";
        }
        Reload();
    }

    private static string? ReceiptId(UploadProblemRow row) => row.Problem.Kind == OutboxKind.Bill ? row.Id : null;

    private void View(UploadProblemRow? row)
    {
        if (row is null) return;
        string text;
        try
        {
            text = Describe(row);
        }
        catch (Exception ex)
        {
            text = $"Could not build what would be sent: {ex.Message}";
        }
        ctx.Dialogs.ShowText(row.Title, text);
    }

    /// <summary>The problem and what the till would send (names not known yet are shown in brackets).</summary>
    private string Describe(UploadProblemRow row)
    {
        var p = row.Problem;
        var header = string.Create(CultureInfo.InvariantCulture,
            $"{row.Title}\nStatus: {p.Status}   Attempts: {p.Attempts}\nShift: {p.ShiftId}\nError: {p.Error ?? "-"}\n\n");
        var till = $"TILL{ctx.TillNumber.ToString(CultureInfo.InvariantCulture)}";
        object? payload = null;
        switch (p.Kind)
        {
            case OutboxKind.Bill when ctx.Receipts.Get(p.Id) is { } receipt && ShiftSettings(receipt.ShiftClientId) is { } settings:
                var returnAgainst = receipt.ReturnAgainst is { } original ? ErpNameOf(original) ?? $"(not uploaded yet: {original})" : null;
                payload = PosInvoicePayload.Build(receipt, settings.PosProfile, settings, OpeningName(receipt.ShiftClientId), receipt.CashierUser,
                    till, returnAgainst).Doc;
                break;
            case OutboxKind.Opening when ctx.Shifts.Get(p.Id) is { } shift && ShiftSettings(p.Id) is { } settings:
                payload = OpeningShiftPayload.Build(shift.Opening, settings.PosProfile, settings.Company, "(the till's ERPNext user)");
                break;
            case OutboxKind.Closing when ctx.Shifts.Get(p.Id) is { Closing: { } closing } shift && ShiftSettings(p.Id) is { } settings:
                var invoices = ctx.Receipts.Outbox(p.Id).Select(e => (e.Sync.ErpName ?? $"(not uploaded yet: {e.Receipt.ClientId})", e.Receipt)).ToList();
                payload = ClosingShiftPayload.Build(shift.Opening, closing, OpeningName(p.Id), invoices, settings.PosProfile, settings.Company,
                    "(the till's ERPNext user)", settings.Customer,
                    settings.TaxesAndCharges is { Length: > 0 } taxes ? ctx.TaxTemplates(taxes) : null, settings.Currency);
                break;
            case OutboxKind.Approval when ctx.Approvals.Get(p.Id) is { } approval:
                payload = ApprovalPayload.Build(approval, till, approval.ShiftClientId.Length > 0 ? OpeningName(approval.ShiftClientId) : null,
                    approval.ReceiptClientId is { } r ? ErpNameOf(r) : null);
                break;
        }
        return header + (payload is null
            ? "The POS settings of this counter are not on the till, so the document cannot be shown."
            : ErpFormat.Json(payload, indented: true));
    }

    private PosSettings? ShiftSettings(string shiftId) =>
        ctx.Shifts.Get(shiftId) is { } shift && !string.IsNullOrWhiteSpace(shift.Opening.Counter)
            ? ctx.Kv.LoadPosSettings(shift.Opening.Counter)
            : ctx.Kv.LoadPosSettings(ctx.DefaultCounter.PosProfile) ?? ctx.Kv.LoadPosSettings();

    private string OpeningName(string shiftId) => ctx.Shifts.SyncInfo(shiftId)?.ErpOpeningName ?? $"(not uploaded yet: {shiftId})";

    /// <summary>The ERPNext name of a bill of this till, or null; a bill of another till is known by its ERPNext name.</summary>
    private string? ErpNameOf(string receiptId)
    {
        try
        {
            return ctx.Receipts.SyncInfo(receiptId).ErpName;
        }
        catch (KeyNotFoundException)
        {
            return ClientIds.IsTillId(receiptId) ? null : receiptId;
        }
    }
}
