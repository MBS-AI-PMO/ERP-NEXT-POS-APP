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
    private readonly ErpClient erp;
    private readonly SyncContext syncContext;
    private readonly TillContext ctx;
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
        erp = ErpClient.Create(new ErpConnection(new Uri(settings.BaseUrl), settings.ApiKey, ApiSecret(settings, settingsPath)),
            TimeSpan.FromSeconds(60));
        syncContext = new SyncContext(erp, store, new KeysetPager(erp, new KvSyncStateStore(store)), settings.PosProfile);

        Shell.TillName = $"Till {settings.TillNumber}";
        Output = new ReceiptOutput(settings, store);
        ctx = new TillContext(
            settings.TillNumber, new TenderModes(settings.CashMode, settings.CardMode),
            () => SaleContext.Create(catalog, store.LoadPosSettings()!, catalog.FindSalesTaxTemplate, settings.Precision, settings.Rounding,
                () => DateOnly.FromDateTime(DateTime.Now)),
            text => catalog.Search(text),
            new Authenticator(LoginCashiers(cashiers, settings)), new PinAttemptLimiter(() => DateTimeOffset.Now), new PinAttemptLimiter(() => DateTimeOffset.Now),
            new ShiftStore(db), new ReceiptStore(db), new ApprovalStore(db), store,
            new SystemClock(), Output, Shell, dialogs, settings.ShowReceiptPreview);
    }

    public ShellViewModel Shell { get; } = new();

    /// <summary>The receipt printer; its header also feeds the on-screen invoice.</summary>
    public ReceiptOutput Output { get; }

    public async Task StartAsync()
    {
        // CatalogPuller.RunAsync reports feed failures in its PullReport instead of throwing.
        while (store.LoadPosSettings() is null)
        {
            Shell.Show(new StatusViewModel("Downloading items and prices from ERPNext…"));
            string problem;
            try
            {
                var report = await Task.Run(() => NewPuller().RunAsync());
                if (store.LoadPosSettings() is not null) break;
                problem = report.Feeds.FirstOrDefault(f => f.Error is not null)?.Error ?? "POS profile not found";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            Shell.Show(new StatusViewModel($"Cannot reach ERPNext ({problem}). The first start needs the internet — retrying in 30 seconds."));
            await Task.Delay(TimeSpan.FromSeconds(30));
        }

        Shell.ShopName = store.LoadPosSettings()!.CompanyName;
        Shell.Show(NewLogin());

        var sync = new SyncService(NewPuller, erp, ctx.Receipts, Shell, dispatcher, TimeSpan.FromSeconds(settings.SyncIntervalSeconds), logError);
        _ = Task.Run(() => sync.RunAsync(stop.Token)).ContinueWith(t => logError(t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop() => stop.Cancel();

    public object NewLogin() => new LoginViewModel(ctx, Shell.Session, NewSale);

    private object NewSale() =>
        new SaleViewModel(ctx, Shell.Session, new SupervisorGate(ctx, Shell.Session),
            (sale, kind) => new PaymentViewModel(ctx, Shell.Session, sale, kind));

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
