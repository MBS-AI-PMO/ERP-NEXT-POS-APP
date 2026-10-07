using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>One counter button on the Open Shift screen. A counter whose POS settings are not on the till (never downloaded,
/// e.g. its POS Profile cannot be read by this till's ERPNext user) is shown but cannot be chosen.</summary>
public sealed class CounterChoice(CounterSettings counter, bool available) : ObservableObject
{
    private bool isSelected;

    public CounterSettings Counter { get; } = counter;
    public bool Available { get; } = available;
    public string Name => Counter.DisplayName;
    public string Detail => Available ? Counter.CashMode : "Not available — settings not downloaded";
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
}

/// <summary>Counter choice and opening float → POS Opening Shift (uploaded in Plan 2b). The counter belongs to the shift:
/// its cash mode takes the float, and it stays until the shift is closed. The last used counter is preselected.</summary>
public sealed class OpenShiftViewModel : ObservableObject
{
    /// <summary>The kv key holding the POS Profile of the counter the last shift was opened at.</summary>
    public const string LastCounterKey = "last_counter";

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
        Counters = ctx.Counters.Select(c => new CounterChoice(c, CanSellAt(c))).ToList();
        var last = CounterSettings.Find(ctx.Counters, LastCounter());
        SelectedCounter = Counters.FirstOrDefault(c => c.Available && c.Counter == last) ?? Counters.FirstOrDefault(c => c.Available);

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

    /// <summary>The choice is only shown when there is more than one counter.</summary>
    public bool ShowCounters => Counters.Count > 1;

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

    public void SelectCounter(CounterChoice? choice)
    {
        if (choice is null) return;
        if (!choice.Available)
        {
            Message = $"{choice.Name} is not available: its settings are not downloaded yet. Choose another counter or call a supervisor.";
            return;
        }
        SelectedCounter = choice;
        Message = "";
    }

    public void Open()
    {
        if (SelectedCounter is not { Available: true } choice)
        {
            Message = "Choose your counter.";
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
            [new ReceiptPayment(counter.CashMode, amount)]) { CounterName = counter.DisplayName };
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

    /// <summary>A counter can be chosen when a sale context can be made for it (its POS settings and taxes are on the till).</summary>
    private bool CanSellAt(CounterSettings counter)
    {
        try
        {
            ctx.NewSaleContextFor(counter.PosProfile);
            return true;
        }
        catch (Exception)
        {
            return false;
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
