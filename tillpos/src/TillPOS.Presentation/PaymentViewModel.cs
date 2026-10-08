using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;

namespace TillPOS.Presentation;

/// <summary>Payment screen: cash (rounded to the currency fraction), card (exact) or split. Completing saves the bill first,
/// then prints; the drawer opens only when cash was taken. A printer failure never loses the bill. In Card mode there is
/// nothing to type: the screen shows the exact amount to charge (<see cref="CardAmountText"/>), and the keypad and quick-cash
/// buttons do nothing.</summary>
public sealed class PaymentViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly IPaymentHost host;
    private readonly PaymentCalculator calculator;
    private readonly decimal grandTotal;
    private TenderKind kind;
    private bool editCard;
    private bool completed;
    private PaymentPlan? plan;
    private string message = "";

    public PaymentViewModel(TillContext ctx, SessionState session, IPaymentHost host, TenderKind initialKind)
    {
        this.ctx = ctx;
        this.session = session;
        this.host = host;
        calculator = new PaymentCalculator(host.Money);
        grandTotal = host.Cart.Totals().GrandTotal;
        // The bill as it is paid (a copy: the sale's list empties when the bill is completed).
        Lines = host.Lines.ToList();
        LineCount = host.ItemCount;
        Discount = host.Discount;
        Vat = host.Vat;
        Cash.Changed += Recalculate;
        Card.Changed += Recalculate;

        SetKindCommand = new RelayCommand<string>(k => { if (Enum.TryParse<TenderKind>(k, out var parsed)) Kind = parsed; });
        QuickCashCommand = new RelayCommand<decimal>(amount => { if (!IsCard) Cash.Set(amount); });
        KeyCommand = new RelayCommand<string>(Key);
        CompleteCommand = new RelayCommand(Complete, () => !completed);
        BackCommand = new RelayCommand(host.BackFromPayment);

        kind = initialKind;
        Recalculate();
        PrefillCash();
    }

    /// <summary>Deliveries: the cash box gets the exact amount due when Cash is chosen and nothing was typed yet.</summary>
    private void PrefillCash()
    {
        if (!host.PrefillExactCash || kind != TenderKind.Cash || Cash.Value is not null) return;
        Cash.Set(calculator.Plan(grandTotal, Tender.Cash(0m)).AmountDue);
    }

    /// <summary>The bill's lines (read-only on this screen), its line count, offers and VAT, as the sale screen showed them.</summary>
    public IReadOnlyList<SaleLine> Lines { get; }
    public string LineCount { get; }
    public string Discount { get; }
    public string Vat { get; }
    public bool HasOffers => Lines.Any(l => !string.IsNullOrEmpty(l.Offer));

    public NumericEntry Cash { get; } = new();
    public NumericEntry Card { get; } = new();
    public IReadOnlyList<decimal> QuickCash { get; } = [10m, 20m, 50m, 100m, 200m, 500m];
    public string GrandTotal => Format.Money(grandTotal);

    public TenderKind Kind
    {
        get => kind;
        set
        {
            if (!SetProperty(ref kind, value)) return;
            OnPropertyChanged(nameof(IsSplit));
            OnPropertyChanged(nameof(IsCard));
            OnPropertyChanged(nameof(ShowsCashEntry));
            OnPropertyChanged(nameof(CardAmountText));
            Recalculate();
            PrefillCash();
        }
    }

    public bool IsSplit => kind == TenderKind.Split;

    /// <summary>Card only: the whole bill goes on the card machine.</summary>
    public bool IsCard => kind == TenderKind.Card;

    /// <summary>The "Cash received" box, quick-cash buttons and Change band show (Cash and Split; not Card).</summary>
    public bool ShowsCashEntry => !IsCard;

    /// <summary>Card mode: the exact bill total to charge on the card machine (never rounded); "" otherwise.</summary>
    public string CardAmountText => IsCard && Plan is { } p ? Format.Money(p.CardAmount) : "";
    public bool EditCard { get => editCard; set => SetProperty(ref editCard, value); }
    public PaymentPlan? Plan { get => plan; private set => SetProperty(ref plan, value); }
    public string AmountDue => Plan is null ? "" : Format.Money(Plan.AmountDue);
    public string Change => Plan is null ? "" : Format.Money(Plan.Change);
    public string Shortfall => Plan is null ? "" : Format.Money(Plan.Shortfall);
    public string Message { get => message; private set => SetProperty(ref message, value); }

    public RelayCommand<string> SetKindCommand { get; }
    public RelayCommand<decimal> QuickCashCommand { get; }
    public RelayCommand<string> KeyCommand { get; }
    public RelayCommand CompleteCommand { get; }
    public RelayCommand BackCommand { get; }

    public void Complete()
    {
        if (completed || Plan is not { } p) return;
        if (!p.IsComplete)
        {
            Message = $"Still to pay {Format.Money(p.Shortfall)}";
            return;
        }

        if (session.Cashier is not { } cashier || session.Shift is not { } shift)
        {
            Message = "No open shift — log in again.";
            return;
        }

        Receipt receipt;
        try
        {
            receipt = host.Record(p, cashier, shift);
        }
        catch (Exception ex)
        {
            Message = $"Could not save the bill: {ex.Message}";
            return;
        }
        completed = true;
        CompleteCommand.NotifyCanExecuteChanged();
        string? printError = null;
        try
        {
            ctx.Output.Print(receipt, openDrawer: p.CashTendered > 0m);
        }
        catch (Exception ex)
        {
            printError = ex.Message;
        }
        host.Completed(receipt, printError);
    }

    private void Key(string? key)
    {
        if (IsCard) return;                       // nothing to type: the card amount is the exact total
        var entry = IsSplit && EditCard ? Card : Cash;
        if (key == ".") entry.Dot();
        else if (key == "⌫") entry.Backspace();
        else if (key == "C") entry.Clear();
        else if (key is { Length: 1 }) entry.Digit(key[0]);
    }

    private void Recalculate()
    {
        try
        {
            Plan = kind switch
            {
                TenderKind.Card => calculator.Plan(grandTotal, Tender.Card()),
                TenderKind.Split => calculator.Plan(grandTotal, Tender.Split(Card.Value ?? 0m, Cash.Value ?? 0m)),
                _ => calculator.Plan(grandTotal, Tender.Cash(Cash.Value ?? 0m)),
            };
            Message = "";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Plan = null;
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        OnPropertyChanged(nameof(AmountDue));
        OnPropertyChanged(nameof(CardAmountText));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(Shortfall));
    }
}
