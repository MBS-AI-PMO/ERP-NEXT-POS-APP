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
    Dispatcher dispatcher, TimeSpan interval)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            bool online;
            string status;
            try
            {
                await erp.PingAsync(ct);
                var report = await newPuller().RunAsync(ct);
                online = true;
                status = report.Ok
                    ? $"Online · synced {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                    : $"Online · {report.Feeds.Count(f => f.Error is not null)} sync problem(s)";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                online = false;
                status = "Offline";
            }
            var pending = receipts.CountPending();
            await dispatcher.InvokeAsync(() =>
            {
                shell.Online = online;
                shell.SyncStatus = status;
                shell.PendingUploads = pending;
            });
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
