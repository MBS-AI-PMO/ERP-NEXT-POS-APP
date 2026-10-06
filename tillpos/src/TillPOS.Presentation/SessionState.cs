using CommunityToolkit.Mvvm.ComponentModel;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

public sealed class SessionState : ObservableObject
{
    private Cashier? cashier;
    private ShiftOpening? shift;

    public Cashier? Cashier { get => cashier; set => SetProperty(ref cashier, value); }
    public ShiftOpening? Shift { get => shift; set => SetProperty(ref shift, value); }
}
