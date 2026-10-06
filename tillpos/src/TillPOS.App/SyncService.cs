using System.Globalization;
using System.Windows.Threading;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;

namespace TillPOS.App;

/// <summary>Every interval: check ERPNext is reachable, pull catalog changes (incl. cashiers), and update the header.
/// Runs on a background thread; never blocks the cashier.</summary>
public sealed class SyncService(Func<CatalogPuller> newPuller, IErpClient erp, ReceiptStore receipts, ShellViewModel shell,
    Dispatcher dispatcher, TimeSpan interval, Action<Exception> logError)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var online = false;
                var status = "Offline";
                try
                {
                    await erp.PingAsync(ct);
                    online = true;
                    status = "Sync error";
                    var report = await newPuller().RunAsync(ct);
                    status = report.Ok
                        ? $"Online · synced {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                        : $"Online · {report.Feeds.Count(f => f.Error is not null)} sync problem(s)";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logError(ex);
                }
                var pending = receipts.CountPending();
                await dispatcher.InvokeAsync(() =>
                {
                    shell.Online = online;
                    shell.SyncStatus = status;
                    shell.PendingUploads = pending;
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
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
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
