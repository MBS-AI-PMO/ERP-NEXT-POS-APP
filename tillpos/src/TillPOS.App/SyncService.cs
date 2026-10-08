using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;
using TillPOS.Sync.Upload;

namespace TillPOS.App;

/// <summary>Every interval: check ERPNext is reachable, pull catalog changes (incl. cashiers), upload the outbox (in the till's
/// upload mode) and update the header. A sale, return or shift change asks for an upload sooner (<see cref="RequestUploadNow"/>):
/// the loop looks every 10 s and then only uploads. Runs on a background thread; never blocks the cashier.</summary>
public sealed class SyncService(Func<CatalogPuller> newPuller, IErpClient erp, Uploader uploader, ShellViewModel shell,
    Dispatcher dispatcher, TimeSpan interval, Action<Exception> logError)
{
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(10);
    private int uploadRequested;

    /// <summary>Upload within about 10 s instead of at the next full sync (safe from any thread).</summary>
    public void RequestUploadNow() => Interlocked.Exchange(ref uploadRequested, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Check);
        Stopwatch? sinceFull = null;
        do
        {
            var full = sinceFull is null || sinceFull.Elapsed >= interval;
            var uploadOnly = Interlocked.Exchange(ref uploadRequested, 0) == 1;
            if (!full && !uploadOnly) continue;
            if (full) sinceFull = Stopwatch.StartNew();
            if (!await CycleAsync(full, ct)) break;
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One sync (full: with the catalog pull). Returns false when the till is shutting down.</summary>
    private async Task<bool> CycleAsync(bool full, CancellationToken ct)
    {
        try
        {
            var online = false;
            string? status = "Offline";
            string? notes = null;
            UploadReport? upload = null;
            PullReport? pulled = null;
            try
            {
                await erp.PingAsync(ct);
                online = true;
                status = null; // an upload-only cycle keeps the last sync status
                if (full)
                {
                    status = "Sync error";
                    var report = await newPuller().RunAsync(ct);
                    pulled = report;
                    var noted = report.Notes;
                    notes = noted.Count == 0 ? "" : string.Join(Environment.NewLine, noted);
                    status = report.Ok
                        ? $"Online · synced {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}" + (noted.Count > 0 ? $" · {noted.Count} note(s)" : "")
                        : $"Online · {report.Feeds.Count(f => f.Error is not null)} sync problem(s)";
                }
                upload = await uploader.RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logError(ex);
            }
            upload ??= uploader.Counts();
            var uploadedToday = UploadedToday();
            var syncedAt = DateTimeOffset.Now;
            await dispatcher.InvokeAsync(() =>
            {
                if (pulled is not null)
                {
                    shell.LastPull = pulled;
                    shell.LastSyncAt = syncedAt;
                }
                if (uploadedToday is { } count) shell.UploadedToday = count;
                shell.UploadProblemDetails = upload.Problems;
                shell.Online = online;
                if (status is not null) shell.SyncStatus = status;
                if (notes is not null) shell.SyncNotes = notes.Length == 0 ? null : notes;
                shell.PendingUploads = upload.Waiting;
                shell.FailedUploads = upload.Failed;
                shell.UploadProblems = upload.Problems.Select(p => p.Message).ToList();
                shell.UploadsUpdated();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // The loop must never die silently: log, show Offline if possible, and try again next tick.
            try
            {
                logError(ex);
                await dispatcher.InvokeAsync(() =>
                {
                    shell.Online = false;
                    shell.SyncStatus = "Offline";
                });
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }
        return true;
    }

    /// <summary>Documents uploaded since local midnight, or null when the till's database could not say (logged).</summary>
    private int? UploadedToday()
    {
        try
        {
            return uploader.UploadedSince(SyncStatusViewModel.StartOfDay(DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            logError(ex);
            return null;
        }
    }
}
