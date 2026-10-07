using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Data;

/// <summary>Upload state of a shift document or an approval (bills use <see cref="ReceiptSyncStatus"/>, same meaning):
/// Pending = waiting for upload, Failed = ERPNext refused it (tried again after its backoff, or at once after a Retry),
/// Synced = in ERPNext, Excluded = taken before the till first went Live (test data): never uploaded unless a supervisor
/// includes it, Handled = a supervisor dealt with it by hand in ERPNext (never uploaded, not counted).</summary>
public enum UploadStatus { Pending, Synced, Failed, Excluded, Handled }

/// <summary>The two ERPNext documents of a till shift.</summary>
public enum ShiftDocument { Opening, Closing }

/// <summary>Upload state of a shift's POS Opening Shift and POS Closing Shift. LastError, Attempts and NextAttemptAt belong to
/// the document being uploaded (the opening until it is in ERPNext, then the closing); they are reset when one is uploaded.</summary>
public sealed record ShiftSyncInfo(
    UploadStatus OpeningStatus,
    string? ErpOpeningName,
    UploadStatus ClosingStatus,
    string? ErpClosingName,
    string? LastError,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    int UnknownAttempts = 0);

/// <summary>A shift that still has something to upload.</summary>
public sealed record ShiftOutboxEntry(ShiftOpening Opening, ShiftClosing? Closing, ShiftSyncInfo Sync);

/// <summary>What kind of document an upload problem is about.</summary>
public enum OutboxKind { Opening, Bill, Closing, Approval }

/// <summary>A document the supervisor may have to deal with (Failed, or Excluded before Live), for the Upload problems screen.
/// ShiftId is the shift it belongs to ("" for an approval made outside a shift).</summary>
public sealed record OutboxProblem(OutboxKind Kind, string Id, string ShiftId, DateTimeOffset Created, UploadStatus Status, string? Error,
    int Attempts);

/// <summary>An approval that is not in ERPNext yet, with its upload state.</summary>
public sealed record ApprovalOutboxEntry(
    ApprovalRecord Record,
    UploadStatus Status,
    string? ErpName,
    string? LastError,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    int UnknownAttempts = 0);
