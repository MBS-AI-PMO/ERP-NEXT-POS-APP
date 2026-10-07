using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>One counter button on the Open Shift screen. A counter the till cannot sell at is shown with the reason
/// (<see cref="Problem"/>) and cannot be chosen.</summary>
public sealed class CounterChoice(CounterSettings counter) : ObservableObject
{
    private string? problem;
    private bool isSelected;

    public CounterSettings Counter { get; } = counter;
    public string Name => Counter.DisplayName;

    /// <summary>Why the counter cannot be chosen (e.g. its settings are not downloaded), or null when it can.</summary>
    public string? Problem
    {
        get => problem;
        set
        {
            if (!SetProperty(ref problem, value)) return;
            OnPropertyChanged(nameof(Available));
            OnPropertyChanged(nameof(Detail));
        }
    }

    public bool Available => Problem is null;
    public string Detail => Problem is null ? Counter.CashMode : $"Not available — {Problem}";
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
}

/// <summary>Counter choice and opening float → POS Opening Shift (uploaded in Plan 2b). The counter belongs to the shift:
/// its cash mode takes the float, the shift saves its label and payment modes, and it stays until the shift is closed. The last
/// used counter is preselected. A counter can only be chosen when the till can sell there with the shared catalog: its POS
/// settings are downloaded and it has the default counter's price list and company (prices and offers are synced for those only).</summary>
public sealed class OpenShiftViewModel : ObservableObject
{
    /// <summary>The kv key holding the POS Profile of the counter the last shift was opened at.</summary>
    public const string LastCounterKey = "last_counter";

    public const string NotDownloaded = "settings not downloaded";
    public const string OtherPriceList = "different price list";
    public const string OtherCompany = "different company";

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly Func<object> newSale;
    private CounterChoice? selectedCounter;
    private string message = "";

    public OpenShiftViewModel(TillContext ctx, SessionState session, Func<object> newSale)
    {
        this.ctx = ctx;
        this.session = session;
        this.newSale = newSale;
        Counters = ctx.Counters.Select(c => new CounterChoice(c)).ToList();
        CheckCounters();
        var last = CounterSettings.Find(ctx.Counters, LastCounter());
        SelectedCounter = Counters.FirstOrDefault(c => c.Available && c.Counter == last) ?? Counters.FirstOrDefault(c => c.Available);
        if (SelectedCounter is null) Message = NoCounterMessage();

        OpenCommand = new RelayCommand(Open);
        SelectCounterCommand = new RelayCommand<CounterChoice>(SelectCounter);
        KeyCommand = new RelayCommand<string>(key =>
        {
            if (key == ".") OpeningCash.Dot();
            else if (key == "⌫") OpeningCash.Backspace();
            else if (key is { Length: 1 }) OpeningCash.Digit(key[0]);
        });
    }

    public IReadOnlyList<CounterChoice> Counters { get; }

    /// <summary>The choice is shown when there is more than one counter, or when the only one cannot be chosen (so the
    /// reason is on screen).</summary>
    public bool ShowCounters => Counters.Count > 1 || Counters.Any(c => !c.Available);

    public CounterChoice? SelectedCounter
    {
        get => selectedCounter;
        private set
        {
            if (selectedCounter is { } old) old.IsSelected = false;
            SetProperty(ref selectedCounter, value);
            if (value is not null) value.IsSelected = true;
        }
    }

    public NumericEntry OpeningCash { get; } = new();
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public RelayCommand OpenCommand { get; }
    public RelayCommand<CounterChoice> SelectCounterCommand { get; }
    public RelayCommand<string> KeyCommand { get; }

    /// <summary>Chooses a counter; availability is checked again first (a sync may have changed it since the screen opened).</summary>
    public void SelectCounter(CounterChoice? choice)
    {
        if (choice is null) return;
        CheckCounters();
        if (!choice.Available)
        {
            if (SelectedCounter is { Available: false }) SelectedCounter = Counters.FirstOrDefault(c => c.Available);
            Message = $"{choice.Name} is not available ({choice.Problem}). Choose another counter or call a supervisor.";
            return;
        }
        SelectedCounter = choice;
        Message = "";
        OnPropertyChanged(nameof(ShowCounters));
    }

    public void Open()
    {
        if (SelectedCounter is not { Available: true } choice)
        {
            Message = SelectedCounter is null && Counters.All(c => !c.Available) ? NoCounterMessage() : "Choose your counter.";
            return;
        }
        if (OpeningCash.Value is not { } amount || amount < 0m)
        {
            Message = "Enter the cash in the drawer.";
            return;
        }
        var counter = choice.Counter;
        var now = ctx.Clock.Now;
        var shift = new ShiftOpening(ClientIds.Shift(ctx.TillNumber, now), session.Cashier!.Id, counter.PosProfile, now,
            [new ReceiptPayment(counter.CashMode, amount)])
        {
            CounterName = counter.DisplayName,
            CashMode = counter.CashMode,
            CardMode = counter.CardMode,
        };
        ctx.Shifts.Open(shift);
        session.Shift = shift;
        session.Counter = counter;
        try
        {
            ctx.Kv.SetValue(LastCounterKey, counter.PosProfile);
        }
        catch (Exception)
        {
            // Only a convenience for the next shift; the shift is open.
        }
        ctx.Navigator.Show(newSale());
    }

    private string NoCounterMessage() =>
        Counters.Count == 1
            ? $"{Counters[0].Name} is not available ({Counters[0].Problem}). Call a supervisor."
            : "No counter is available. Call a supervisor.";

    /// <summary>Works out each counter's problem, if any: a sale context must be possible (POS settings and taxes on the till),
    /// with the default counter's price list and company.</summary>
    private void CheckCounters()
    {
        var reference = TryContext(ctx.DefaultCounter);
        foreach (var choice in Counters)
        {
            choice.Problem = ProblemOf(TryContext(choice.Counter), reference);
        }
        OnPropertyChanged(nameof(ShowCounters));
    }

    private static string? ProblemOf(SaleContext? sc, SaleContext? reference)
    {
        if (sc is null) return NotDownloaded;
        if (reference is null) return null;
        if (!Same(sc.PriceList, reference.PriceList)) return OtherPriceList;
        return sc.Company is not null && reference.Company is not null && !Same(sc.Company, reference.Company) ? OtherCompany : null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private SaleContext? TryContext(CounterSettings counter)
    {
        try
        {
            return ctx.NewSaleContextFor(counter.PosProfile);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? LastCounter()
    {
        try
        {
            return ctx.Kv.GetValue(LastCounterKey);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
