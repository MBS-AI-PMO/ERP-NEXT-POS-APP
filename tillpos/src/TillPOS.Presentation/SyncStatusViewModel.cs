using System.Globalization;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Sync;
using TillPOS.Sync.Upload;

namespace TillPOS.Presentation;

/// <summary>One catalog feed of the last download: OK (with its note, if any) or its problem in plain words.</summary>
public sealed record SyncFeedRow(string Feed, string Rows, bool Ok, string Text);

/// <summary>A document uploaded today: shop time, kind, the till's id, its ERPNext name and amount.</summary>
public sealed record UploadedRow(string Time, string Kind, string Id, string ErpName, string Amount);

/// <summary>A document waiting for upload, and why.</summary>
public sealed record WaitingRow(string Kind, string Id, string Reason);

/// <summary>A document ERPNext refused (or whose totals differ), with the error.</summary>
public sealed record FailedRow(string Kind, string Id, string Error);

/// <summary>The Sync status window (click the header's sync pill, on any screen; no PIN): the connection and last sync, what the
/// last catalog download did per feed (the "N sync problem(s)" in plain words), and the upload state: today's uploads, what is
/// waiting and why, and what failed. Read once when opened. "Open Upload problems (supervisor)" goes on to the Upload problems
/// screen behind the supervisor PIN (<see cref="OpenAsync"/>).</summary>
public sealed class SyncStatusViewModel
{
    public SyncStatusViewModel(TillContext ctx, ShellViewModel shell)
    {
        var now = ctx.Clock.Now;
        Connection = shell.Online ? "Online" : "Offline";
        SyncStatus = shell.SyncStatus;
        LastSync = shell.LastSyncAt is { } at ? Time(at, now) : "not yet";
        UploadModeText = shell.Upload switch
        {
            UploadMode.Live => "Live: bills and shifts are sent to ERPNext",
            UploadMode.DryRun => "Dry run: bills are checked and previewed, nothing is sent to ERPNext",
            _ => "Off: nothing is sent to ERPNext",
        };
        CanOpenUploadProblems = shell.Current is not DownloadViewModel;

        if (shell.LastPull is { } pull)
        {
            Feeds = pull.Feeds.Select(Feed).ToList();
            var problems = pull.Feeds.Count(f => f.Error is not null);
            FeedsSummary = problems == 0
                ? $"All {Count(pull.Feeds.Count)} feeds OK in the last download"
                : $"{Count(problems)} sync problem(s) in the last download";
        }
        else
        {
            Feeds = [];
            FeedsSummary = "No download since the till started";
        }

        try
        {
            var since = StartOfDay(now);
            UploadedToday =
            [
                .. new[] { ctx.Receipts.SyncedSince(since), ctx.Shifts.SyncedSince(since), ctx.Approvals.SyncedSince(since) }
                    .SelectMany(list => list)
                    .OrderByDescending(d => d.SyncedAt).ThenBy(d => d.Kind).ThenBy(d => d.Id, StringComparer.Ordinal)
                    .Select(d => new UploadedRow(Time(d.SyncedAt, now), KindName(d.Kind, d.IsReturn), d.Id, d.ErpName ?? "",
                        d.Amount is { } amount ? Format.Money(amount) : "")),
            ];
            var runProblems = shell.UploadProblemDetails
                .Where(p => p.DocId.Length > 0)
                .GroupBy(p => p.DocId)
                .ToDictionary(g => g.Key, g => g.First().Message);
            Waiting =
            [
                .. new[] { ctx.Shifts.Waiting(), ctx.Receipts.Waiting(), ctx.Approvals.Waiting() }
                    .SelectMany(list => list)
                    .OrderBy(p => p.Created).ThenBy(p => p.Kind).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new WaitingRow(KindName(p, ctx), p.Id, WaitingReason(p, runProblems, shell.Upload))),
            ];
            Failed =
            [
                .. new[] { ctx.Shifts.Problems(), ctx.Receipts.Problems(), ctx.Approvals.Problems() }
                    .SelectMany(list => list)
                    .Where(p => p.Status == UploadStatus.Failed)
                    .OrderBy(p => p.Created).ThenBy(p => p.Kind).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new FailedRow(KindName(p, ctx), p.Id, p.Error ?? "")),
            ];
        }
        catch (Exception ex)
        {
            UploadedToday = [];
            Waiting = [];
            Failed = [];
            Message = $"Could not read the upload state: {ex.Message}";
        }
    }

    public string Connection { get; }

    /// <summary>The header's sync text (e.g. "Online · synced 09:25").</summary>
    public string SyncStatus { get; }

    /// <summary>Shop time of the last full sync (catalog download and upload), or "not yet".</summary>
    public string LastSync { get; }

    public string UploadModeText { get; }

    /// <summary>"All 12 feeds OK in the last download", "2 sync problem(s) in the last download", or none yet.</summary>
    public string FeedsSummary { get; }

    public IReadOnlyList<SyncFeedRow> Feeds { get; }
    public IReadOnlyList<UploadedRow> UploadedToday { get; }
    public IReadOnlyList<WaitingRow> Waiting { get; }
    public IReadOnlyList<FailedRow> Failed { get; }
    public int UploadedTodayCount => UploadedToday.Count;
    public int WaitingCount => Waiting.Count;
    public int FailedCount => Failed.Count;

    /// <summary>A store failure while reading the lists, or "".</summary>
    public string Message { get; } = "";

    /// <summary>False on the first-start download screen (the till is not ready for the Upload problems screen yet).</summary>
    public bool CanOpenUploadProblems { get; }

    /// <summary>Midnight of <paramref name="now"/>'s day, in its offset: "today" for "uploaded today".</summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset now) => new(now.Date, now.Offset);

    /// <summary>Shows the window; when "Open Upload problems (supervisor)" was pressed, opens that screen behind the supervisor
    /// PIN (logged with the session's cashier and shift), and its Back returns to the screen shown before.</summary>
    public static async Task OpenAsync(TillContext ctx, ShellViewModel shell)
    {
        var vm = new SyncStatusViewModel(ctx, shell);
        if (!ctx.Dialogs.ShowSyncStatus(vm) || !vm.CanOpenUploadProblems) return;
        var previous = shell.Current;
        var gate = new SupervisorGate(ctx, shell.Session);
        if (await gate.ApproveAsync(ApprovalAction.SettingsChange, "Open Upload problems") is null) return;
        ctx.Navigator.Show(new UploadProblemsViewModel(ctx, gate, () => previous ?? new object()));
    }

    /// <summary>A feed's error in plain words (the raw text follows in the window).</summary>
    public static string PlainFeedError(string error)
    {
        bool Has(params string[] parts) => parts.Any(p => error.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("HTTP 401", "AuthenticationError", "Unauthorized", "Invalid API key")) return "ERPNext did not accept the till's API key";
        if (Has("HTTP 403", "PermissionError", "Not permitted", "Insufficient Permission", "Forbidden"))
            return "ERPNext refused: the till's login may not read this";
        if (Has("HTTP 404", "DoesNotExistError", "not found")) return "Not found in ERPNext";
        if (Has("Timeout", "timed out")) return "ERPNext did not answer in time";
        if (Has("HTTP 500", "HTTP 502", "HTTP 503", "HTTP 504", "Internal Server Error", "Bad Gateway", "Service Unavailable"))
            return "ERPNext had a server error; it is tried again at the next sync";
        if (Has("No such host", "actively refused", "network", "connection", "SSL", "unreachable"))
            return "Could not reach ERPNext (network)";
        return "The download failed";
    }

    private static SyncFeedRow Feed(FeedResult f) =>
        f.Error is { } error
            ? new SyncFeedRow(f.Feed, Count(f.Rows), false, $"{PlainFeedError(error)}. ({Short(error)})")
            : new SyncFeedRow(f.Feed, Count(f.Rows), true, f.Note is { Length: > 0 } note ? $"OK - {note}" : "OK");

    private static string WaitingReason(OutboxProblem p, Dictionary<string, string> runProblems, UploadMode mode) =>
        mode switch
        {
            UploadMode.Off => "Upload is off on this till",
            UploadMode.DryRun => "Dry run: checked and previewed only, never sent",
            _ when runProblems.TryGetValue(p.Id, out var message) => message,
            _ when !string.IsNullOrWhiteSpace(p.Error) => p.Error!,
            _ => "Waiting for the next upload",
        };

    private static string KindName(OutboxKind kind, bool isReturn) => kind switch
    {
        OutboxKind.Opening => "Shift opening",
        OutboxKind.Closing => "Shift closing",
        OutboxKind.Approval => "Approval",
        _ => isReturn ? "Return" : "Bill",
    };

    private static string KindName(OutboxProblem p, TillContext ctx) =>
        KindName(p.Kind, p.Kind == OutboxKind.Bill && IsReturn(ctx, p.Id));

    private static bool IsReturn(TillContext ctx, string id)
    {
        try
        {
            return ctx.Receipts.Get(id)?.Kind == TillPOS.Core.Sales.ReceiptKind.Return;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Time(DateTimeOffset at, DateTimeOffset now) => at.ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
