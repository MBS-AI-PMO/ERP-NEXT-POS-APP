using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Data;

/// <summary>Upload state of a shift document or an approval (bills use <see cref="ReceiptSyncStatus"/>, same meaning):
/// Pending = waiting for upload, Failed = ERPNext refused it (tried again after its backoff, or at once after a Retry),
/// Synced = in ERPNext.</summary>
public enum UploadStatus { Pending, Synced, Failed }

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
    DateTimeOffset? NextAttemptAt);

/// <summary>A shift that still has something to upload.</summary>
public sealed record ShiftOutboxEntry(ShiftOpening Opening, ShiftClosing? Closing, ShiftSyncInfo Sync);

/// <summary>An approval that is not in ERPNext yet, with its upload state.</summary>
public sealed record ApprovalOutboxEntry(
    ApprovalRecord Record,
    UploadStatus Status,
    string? ErpName,
    string? LastError,
    int Attempts,
    DateTimeOffset? NextAttemptAt);
