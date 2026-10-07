using CommunityToolkit.Mvvm.ComponentModel;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

public sealed class SessionState : ObservableObject
{
    private Cashier? cashier;
    private ShiftOpening? shift;
    private CounterSettings? counter;

    public Cashier? Cashier { get => cashier; set => SetProperty(ref cashier, value); }

    /// <summary>The open shift; clearing it (shift closed) also clears <see cref="Counter"/>.</summary>
    public ShiftOpening? Shift
    {
        get => shift;
        set
        {
            SetProperty(ref shift, value);
            if (value is null) Counter = null;
        }
    }

    /// <summary>The counter of the open shift (set when the shift is opened or joined), shown in the header.</summary>
    public CounterSettings? Counter { get => counter; set => SetProperty(ref counter, value); }
}
