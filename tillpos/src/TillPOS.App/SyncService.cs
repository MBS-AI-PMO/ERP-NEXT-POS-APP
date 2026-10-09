using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;
using TillPOS.Sync.Upload;

namespace TillPOS.App;

/// <summary>Every interval: check ERPNext is reachable, pull catalog changes (incl. cashiers), upload the outbox (in the till's
/// upload mode) and update the header. A sale, return or shift change asks for an upload sooner (<see cref="RequestUploadNow"/>).
/// Between full syncs, prices changed in ERPNext are fetched every <see cref="PriceInterval"/> (weighed items change price during
/// the day), and "Update prices (F10)" fetches them at once (<see cref="RefreshPricesNowAsync"/>). When new prices arrive,
/// <paramref name="pricesChanged"/> runs on the UI thread (the open bill is re-priced). Only one download or upload runs at a
/// time. Runs on a background thread; never blocks the cashier.</summary>
/// <param name="newPricePuller">A pull of the Item Price feed only (incremental: only rows changed since the last pull).</param>
public sealed class SyncService(Func<CatalogPuller> newPuller, Func<CatalogPuller> newPricePuller, IErpClient erp, Uploader uploader,
    ShellViewModel shell, Dispatcher dispatcher, TimeSpan interval, Action pricesChanged, Action<Exception> logError)
{
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PriceInterval = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private int uploadRequested;

    /// <summary>Upload within a few seconds instead of at the next full sync (safe from any thread).</summary>
    public void RequestUploadNow() => Interlocked.Exchange(ref uploadRequested, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Check);
        Stopwatch? sinceFull = null;
        var sincePrices = Stopwatch.StartNew();
        do
        {
            var full = sinceFull is null || sinceFull.Elapsed >= interval;
            var uploadOnly = Interlocked.Exchange(ref uploadRequested, 0) == 1;
            if (full)
            {
                sinceFull = Stopwatch.StartNew();
                sincePrices.Restart();                     // a full sync pulls prices too
                if (!await CycleAsync(true, ct)) break;
                continue;
            }
            if (sincePrices.Elapsed >= PriceInterval)
            {
                sincePrices.Restart();
                if (await PullPricesAsync(ct) is { ChangedPrices: > 0 }) await dispatcher.InvokeAsync(RunPricesChanged);
            }
            if (uploadOnly && !await CycleAsync(false, ct)) break;
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>"Update prices (F10)": fetches the prices changed in ERPNext now (waiting for a sync already running). Offline
    /// (or on any failure to reach ERPNext) it reports Online = false with the time prices were last downloaded.</summary>
    public async Task<PriceRefresh> RefreshPricesNowAsync(CancellationToken ct) =>
        await PullPricesAsync(ct) ?? new PriceRefresh(false, 0, shell.LastSyncAt);

    /// <summary>One price-only pull under the gate; null when ERPNext could not be reached (logged) or the till is stopping.</summary>
    private async Task<PriceRefresh?> PullPricesAsync(CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            var report = await newPricePuller().RunAsync(ct);
            if (!report.Ok) return null;                   // the feed failed: offline, or ERPNext refused
            return new PriceRefresh(true, report.Feeds.Sum(f => f.Rows), DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logError(ex);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private void RunPricesChanged()
    {
        try
        {
            pricesChanged();
        }
        catch (Exception ex)
        {
            logError(ex);
        }
    }

    /// <summary>One sync (full: with the catalog pull), under the gate. Returns false when the till is shutting down.</summary>
    private async Task<bool> CycleAsync(bool full, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            return await CycleUnderGateAsync(full, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> CycleUnderGateAsync(bool full, CancellationToken ct)
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
                // New prices in a full sync re-price the open bill too.
                if (pulled?.Feeds.FirstOrDefault(f => f.Feed == "Item Price") is { Rows: > 0, Error: null }) RunPricesChanged();
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
