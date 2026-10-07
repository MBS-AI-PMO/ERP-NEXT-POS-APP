using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Sync.Upload;

namespace TillPOS.App;

/// <summary>Builds the till: database, catalog, ERPNext client, background sync and the view models.</summary>
public sealed class AppHost
{
    private readonly TillSettings settings;
    private readonly Dispatcher dispatcher;
    private readonly Action<Exception> logError;
    private readonly CatalogStore store;
    private readonly SqliteCatalog catalog;
    private readonly CashierStore cashiers;
    private readonly IErpClient erp;
    private readonly SyncContext syncContext;
    private readonly TillContext ctx;
    private readonly Uploader uploader;
    private readonly CancellationTokenSource stop = new();

    /// <param name="settingsPath">The settings file the till was started from (named in setup error messages).</param>
    public AppHost(TillSettings settings, string settingsPath, Dispatcher dispatcher, IDialogs dialogs, Action<Exception> logError)
    {
        this.settings = settings;
        this.dispatcher = dispatcher;
        this.logError = logError;
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DbPath)!);
        var db = new TillDb(settings.DbPath);
        db.Migrate();
        store = new CatalogStore(db);
        catalog = new SqliteCatalog(db);
        cashiers = new CashierStore(db);
        // Everything reads through a read-only view of the client: its writer side is only handed out in Live upload mode.
        // 60 s per request: an upload whose answer does not come in time stays "in progress" for Uploader.InFlightHold (5 min),
        // longer than gunicorn's 120 s worker timeout, before it is looked up again, so a slow insert is never sent twice.
        var client = ErpClient.Create(new ErpConnection(new Uri(settings.BaseUrl), settings.ApiKey, ApiSecret(settings, settingsPath)),
            TimeSpan.FromSeconds(60));
        erp = new ReadOnlyErpClient(client);
        Shell.Upload = settings.EffectiveUpload;
        // The first counter is the default: its POS settings are also kept under the default key (receipt header, price list).
        var counters = settings.EffectiveCounters();
        syncContext = new SyncContext(erp, store, new KeysetPager(erp, new KvSyncStateStore(store)), counters[0].PosProfile)
        {
            Profiles = counters.Select(c => c.PosProfile).ToList(),
        };

        var shifts = new ShiftStore(db);
        var receipts = new ReceiptStore(db);
        var approvals = new ApprovalStore(db);
        // The write guard: a writer over the client is built only in Live mode of a production build (UploadPipeline.LiveWriter
        // is null otherwise). Shifts saved before counters existed (blank counter) belong to the default counter.
        var testBuild = settings.IsTestBuild;
        uploader = new Uploader(erp, UploadPipeline.LiveWriter(settings.EffectiveUpload, () => new ErpWriter(client), testBuild), settings.EffectiveUpload,
            shifts, receipts, approvals,
            profile => store.LoadPosSettings(string.IsNullOrWhiteSpace(profile) ? counters[0].PosProfile : profile),
            $"TILL{settings.TillNumber}", null, () => DateTimeOffset.Now, WritePreview, testBuild)
        {
            TaxTemplates = catalog.FindSalesTaxTemplate,
            LogError = logError,
        };
        // Live set by hand in settings.json (not through Settings): still never upload the history from before (no-op when the
        // till already went Live once).
        if (settings.EffectiveUpload == UploadMode.Live) UploadHistory.SwitchToLive(shifts, store, DateTimeOffset.Now, includeHistory: false);

        Shell.TillName = $"Till {settings.TillNumber}";
        Output = new ReceiptOutput(settings, store);
        ctx = new TillContext(
            settings.TillNumber, counters,
            profile => SaleContext.Create(catalog,
                store.LoadPosSettings(profile) ?? throw new InvalidOperationException($"The settings of counter '{profile}' are not downloaded yet."),
                catalog.FindSalesTaxTemplate, settings.Precision, settings.Rounding, () => DateOnly.FromDateTime(DateTime.Now)),
            text => catalog.Search(text),
            new Authenticator(LoginCashiers(cashiers, settings)), new PinAttemptLimiter(() => DateTimeOffset.Now), new PinAttemptLimiter(() => DateTimeOffset.Now),
            shifts, receipts, approvals, store,
            new SystemClock(), Output, Shell, dialogs, settings.ShowReceiptPreview, new HeldCartStore(db));
    }

    public ShellViewModel Shell { get; } = new();

    /// <summary>The receipt printer; its header also feeds the on-screen invoice.</summary>
    public ReceiptOutput Output { get; }

    public async Task StartAsync()
    {
        if (store.LoadPosSettings() is null) await FirstDownloadAsync();

        Shell.ShopName = store.LoadPosSettings()!.CompanyName;
        Shell.Show(NewLogin());

        var sync = new SyncService(NewPuller, erp, uploader, Shell, dispatcher, TimeSpan.FromSeconds(settings.SyncIntervalSeconds), logError);
        // A sale, a return, or a shift opened or closed is uploaded within about 10 s.
        ctx.Receipts.Saved += sync.RequestUploadNow;
        ctx.Shifts.Changed += sync.RequestUploadNow;
        _ = Task.Run(() => sync.RunAsync(stop.Token)).ContinueWith(t => logError(t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop() => stop.Cancel();

    /// <summary>The first start cannot sell before the POS profile and catalog are on the till: shows the download screen and
    /// pulls until the POS settings exist, retrying every 30 s with a visible countdown. CatalogPuller.RunAsync reports feed
    /// failures in its PullReport instead of throwing, so success is judged by the stored POS settings.</summary>
    private async Task FirstDownloadAsync()
    {
        var retryIn = TimeSpan.FromSeconds(30);
        var download = new DownloadViewModel(NewPuller().FeedNames);
        Shell.Show(download);
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => download.Tick(clock.Elapsed), dispatcher);
        timer.Start();
        try
        {
            while (true)
            {
                // Created on the UI thread, so the puller's reports are posted back to it (in order, before the await resumes).
                var progress = new Progress<PullProgress>(download.Apply);
                string problem;
                try
                {
                    var puller = NewPuller();
                    var report = await Task.Run(() => puller.RunAsync(default, progress));
                    if (store.LoadPosSettings() is not null) return;
                    problem = report.Feeds.FirstOrDefault(f => f.Error is not null)?.Error ?? "POS profile not found";
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                }

                download.Failed(problem, retryIn);
                for (var left = retryIn; left > TimeSpan.Zero;)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    left -= TimeSpan.FromSeconds(1);
                    download.CountdownTick(left);
                }
            }
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>DryRun: each payload ({key}.json) and the run summary (_summary.txt) go to the outbox-preview folder next to the
    /// database, for inspection before going Live.</summary>
    private void WritePreview(string name, string text)
    {
        var folder = Path.Combine(Path.GetDirectoryName(settings.DbPath)!, "outbox-preview");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
    }

    public object NewLogin() => new LoginViewModel(ctx, Shell.Session, NewSale);

    private object NewSale() =>
        new SaleViewModel(ctx, Shell.Session, new SupervisorGate(ctx, Shell.Session),
            (sale, kind) => new PaymentViewModel(ctx, Shell.Session, sale, kind), NewLogin);

    /// <summary>SettingsStore has already replaced a plain secret with the protected one; the plain one is only used if
    /// that could not happen. DPAPI (machine scope) cannot decrypt a value protected on another PC, e.g. when a used TillPOS
    /// folder was copied; the message says how to recover and never contains the protected value or the secret.</summary>
    private static string ApiSecret(TillSettings settings, string settingsPath)
    {
        if (!string.IsNullOrEmpty(settings.ApiSecretProtected))
        {
            try
            {
                return SecretProtector.Unprotect(settings.ApiSecretProtected);
            }
            catch (CryptographicException)
            {
                // SettingsStore adopts the plain secret of a freshly unzipped package, so unzipping again recovers.
                throw new InvalidOperationException(
                    $"The API secret in {settingsPath} was protected on another PC. Unzip the original TillPOS package again on this PC " +
                    "and start TillPOS from that folder.");
            }
        }
        return !string.IsNullOrEmpty(settings.ApiSecret) ? settings.ApiSecret : throw new InvalidOperationException("API secret missing in settings.json");
    }

    private CatalogPuller NewPuller() => CatalogPuller.CreateDefault(syncContext, catalog.Reload, null, new CashierFeed(syncContext, cashiers));

    /// <summary>The synced ERPNext cashiers; while none have synced yet, the settings' local test cashiers (hashed once,
    /// kept in memory only and never written to the database). They stop working as soon as ERPNext cashiers sync.</summary>
    private static Func<IReadOnlyList<Cashier>> LoginCashiers(CashierStore cashiers, TillSettings settings)
    {
        IReadOnlyList<Cashier> localHashed = (settings.LocalTestCashiers ?? [])
            .Where(c => PinHasher.IsValidPin(c.Pin))
            .Select(c => new Cashier(c.Id, c.Name, null, PinHasher.Hash(c.Pin), c.IsSupervisor, true))
            .ToList();
        return () =>
        {
            var synced = cashiers.All();
            return synced.Count > 0 ? synced : localHashed;
        };
    }
}
