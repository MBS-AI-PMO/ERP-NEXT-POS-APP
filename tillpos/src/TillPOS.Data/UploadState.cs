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

/// <summary>Upload state of a shift's POS Opening Shift and POS Closing Shift. Attempts, NextAttemptAt and UnknownAttempts belong
/// to the document being uploaded (the opening until it is in ERPNext, then the closing); they are reset when one is uploaded.
/// OpeningError and ClosingError are each document's own error (or handled note); LastError is the latest of the two.</summary>
public sealed record ShiftSyncInfo(
    UploadStatus OpeningStatus,
    string? ErpOpeningName,
    UploadStatus ClosingStatus,
    string? ErpClosingName,
    string? LastError,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    int UnknownAttempts = 0,
    string? OpeningError = null,
    string? ClosingError = null);

/// <summary>A shift that still has something to upload.</summary>
public sealed record ShiftOutboxEntry(ShiftOpening Opening, ShiftClosing? Closing, ShiftSyncInfo Sync);

/// <summary>What kind of document an upload problem is about.</summary>
public enum OutboxKind { Opening, Bill, Closing, Approval }

/// <summary>A document the supervisor may have to deal with (Failed, or Excluded before Live), for the Upload problems screen.
/// ShiftId is the shift it belongs to ("" for an approval made outside a shift).</summary>
public sealed record OutboxProblem(OutboxKind Kind, string Id, string ShiftId, DateTimeOffset Created, UploadStatus Status, string? Error,
    int Attempts);

/// <summary>A document that reached ERPNext (inserted, or found there and adopted) at <paramref name="SyncedAt"/>, for the Sync
/// status window. Amount: a bill's grand total (negative for a return), an opening's float, a closing's sales total, an
/// approval's amount (null when it has none).</summary>
public sealed record SyncedDocument(OutboxKind Kind, string Id, string? ErpName, DateTimeOffset SyncedAt, decimal? Amount, bool IsReturn = false);

/// <summary>An approval that is not in ERPNext yet, with its upload state.</summary>
public sealed record ApprovalOutboxEntry(
    ApprovalRecord Record,
    UploadStatus Status,
    string? ErpName,
    string? LastError,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    int UnknownAttempts = 0);
