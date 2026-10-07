using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TillPOS.Sync;

namespace TillPOS.Presentation;

public enum DownloadState { Waiting, Running, Done, Failed }

/// <summary>One feed on the first-start download screen.</summary>
public sealed class DownloadStep(string name) : ObservableObject
{
    private string name = name;
    private DownloadState state;
    private string detail = "";

    public string Name { get => name; set => SetProperty(ref name, value); }
    public DownloadState State { get => state; set => SetProperty(ref state, value); }
    /// <summary>Rows so far while running, "12,014 items · 20.5 s" when done, the error when failed.</summary>
    public string Detail { get => detail; set => SetProperty(ref detail, value); }
}

/// <summary>The first start: downloads the catalog from ERPNext before the till can sell. Fed by the puller's
/// <see cref="PullProgress"/> (Apply), a 1 s clock (Tick) and, between attempts, the retry countdown.</summary>
public sealed class DownloadViewModel : ObservableObject
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, string> Friendly = new()
    {
        ["POS Profile"] = "Shop settings",
        ["Item Group"] = "Item groups",
        ["Item Tax Template"] = "Item tax templates",
        ["Sales Taxes and Charges Template"] = "Sales tax templates",
        ["Pricing Rule"] = "Offers and discounts",
        ["Item"] = "Items and barcodes",
        ["Item Price"] = "Prices",
        ["Deleted Document"] = "Removed items",
        ["Reconcile"] = "Daily check",
        ["POS Cashier"] = "Cashiers",
    };

    /// <summary>What a finished feed's row count means; feeds not listed show their rows as "rows".</summary>
    private static readonly Dictionary<string, string?> Units = new()
    {
        ["POS Profile"] = null, // always one profile: only the time is shown
        ["Item Group"] = "groups",
        ["Item Tax Template"] = "templates",
        ["Sales Taxes and Charges Template"] = "templates",
        ["Pricing Rule"] = "offers",
        ["Item"] = "items",
        ["Item Price"] = "prices",
        ["Deleted Document"] = "removed",
        ["Reconcile"] = "removed",
        ["POS Cashier"] = "cashiers",
    };

    private string currentText = "Connecting to ERPNext…";
    private string stepText = "";
    private double progress;
    private bool isIndeterminate = true;
    private string elapsed = "0:00";
    private string errorText = "";
    private string retryText = "";

    /// <param name="feedNames">The puller's feeds in order (CatalogPuller.FeedNames), so every step shows from the start.</param>
    public DownloadViewModel(IEnumerable<string>? feedNames = null)
    {
        foreach (var feed in feedNames ?? []) Steps.Add(new DownloadStep(FriendlyName(feed)));
    }

    public string Title => "Setting up this till";
    public string Subtitle => "Downloading items, prices and settings from ERPNext";
    public ObservableCollection<DownloadStep> Steps { get; } = [];

    /// <summary>"Items and barcodes: 4,350 of 12,014".</summary>
    public string CurrentText { get => currentText; private set => SetProperty(ref currentText, value); }
    /// <summary>"Step 6 of 10".</summary>
    public string StepText { get => stepText; private set => SetProperty(ref stepText, value); }
    /// <summary>Overall 0..100.</summary>
    public double Progress { get => progress; private set => SetProperty(ref progress, value); }
    /// <summary>True while the running feed has no expected row count.</summary>
    public bool IsIndeterminate { get => isIndeterminate; private set => SetProperty(ref isIndeterminate, value); }
    /// <summary>"0:42" since the first start began.</summary>
    public string Elapsed { get => elapsed; private set => SetProperty(ref elapsed, value); }

    public string ErrorText
    {
        get => errorText;
        private set
        {
            if (SetProperty(ref errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public string RetryText { get => retryText; private set => SetProperty(ref retryText, value); }
    public bool HasError => ErrorText.Length > 0;

    public static string FriendlyName(string feed) => Friendly.GetValueOrDefault(feed, feed);

    public void Apply(PullProgress p)
    {
        ErrorText = "";
        RetryText = "";
        while (Steps.Count < p.Steps) Steps.Add(new DownloadStep(""));

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (i < p.Done.Count)
            {
                var done = p.Done[i];
                step.Name = FriendlyName(done.Feed);
                step.State = done.Error is null ? DownloadState.Done : DownloadState.Failed;
                step.Detail = done.Error ?? DoneDetail(done);
            }
            else if (i == p.Step - 1)
            {
                step.Name = FriendlyName(p.Feed);
                step.State = DownloadState.Running;
                step.Detail = p.Rows > 0 ? N(p.Rows) : "";
            }
            else
            {
                step.State = DownloadState.Waiting;
                step.Detail = "";
            }
        }

        var name = FriendlyName(p.Feed);
        var finished = p.Done.Count >= p.Step;
        CurrentText = finished || p.ExpectedRows is null
            ? p.Rows > 0 ? $"{name}: {N(p.Rows)}" : finished ? name : $"{name}…"
            : $"{name}: {N(p.Rows)} of {N(p.ExpectedRows.Value)}";
        StepText = $"Step {p.Step} of {p.Steps}";
        IsIndeterminate = !finished && p.ExpectedRows is null;

        var fraction = finished ? 1.0
            : p.ExpectedRows is int expected and > 0 ? Math.Min(1.0, (double)p.Rows / expected)
            : 0.5;
        Progress = p.Steps <= 0 ? 0 : Math.Clamp((p.Step - 1 + fraction) / p.Steps * 100, 0, 100);
    }

    public void Tick(TimeSpan sinceStart) =>
        Elapsed = $"{(int)sinceStart.TotalMinutes}:{sinceStart.Seconds.ToString("00", Invariant)}";

    /// <summary>The attempt failed; the next one starts after <paramref name="retryIn"/> (shown by CountdownTick).</summary>
    public void Failed(string problem, TimeSpan retryIn)
    {
        ErrorText = $"Cannot reach ERPNext ({problem}). The first start needs the internet.";
        CountdownTick(retryIn);
    }

    public void CountdownTick(TimeSpan left)
    {
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        RetryText = seconds > 0 ? $"Retrying in {seconds} s" : "Retrying now…";
    }

    private static string DoneDetail(FeedResult done)
    {
        var time = done.Duration.TotalSeconds.ToString("0.0", Invariant) + " s";
        var unit = Units.TryGetValue(done.Feed, out var u) ? u : "rows";
        return unit is null ? time : $"{N(done.Rows)} {unit} · {time}";
    }

    private static string N(int value) => value.ToString("N0", Invariant);
}
