using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class FakeDialogs : IDialogs
{
    public Queue<string?> Pins { get; } = new();
    public Queue<decimal?> Numbers { get; } = new();
    public List<string> Infos { get; } = [];
    public int PinRequests { get; private set; }
    public List<(Receipt Receipt, string? PrintError)> Receipts { get; } = [];
    public Queue<string?> ReceiptScans { get; } = new();
    public Func<string?>? LastReprint { get; private set; }
    public int SetupRequests { get; private set; }
    public Queue<bool> SetupResults { get; } = new();

    /// <summary>Plays the price-check dialog: gets the view model, returns the pick (default: closed without adding).</summary>
    public Func<PriceCheckViewModel, PriceCheckPick?> OnPriceCheck { get; set; } = _ => null;
    public int PriceCheckRequests { get; private set; }

    /// <summary>Plays the held-bills dialog: gets the view model, returns the id to recall (default: closed).</summary>
    public Func<HeldBillsViewModel, string?> OnHeldBills { get; set; } = _ => null;
    public int HeldBillsRequests { get; private set; }

    public Task<string?> AskPinAsync(string title, string reason)
    {
        PinRequests++;
        return Task.FromResult(Pins.Count > 0 ? Pins.Dequeue() : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt) => Task.FromResult(Numbers.Count > 0 ? Numbers.Dequeue() : null);
    public void Info(string message) => Infos.Add(message);

    public string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint)
    {
        Receipts.Add((receipt, printError));
        LastReprint = reprint;
        return ReceiptScans.Count > 0 ? ReceiptScans.Dequeue() : null;
    }

    public bool ShowSetup()
    {
        SetupRequests++;
        return SetupResults.Count > 0 && SetupResults.Dequeue();
    }

    public PriceCheckPick? ShowPriceCheck(PriceCheckViewModel vm)
    {
        PriceCheckRequests++;
        return OnPriceCheck(vm);
    }

    public string? ShowHeldBills(HeldBillsViewModel vm)
    {
        HeldBillsRequests++;
        return OnHeldBills(vm);
    }
}

public sealed class FakeNavigator : INavigator
{
    public object? Current { get; private set; }
    public void Show(object viewModel) => Current = viewModel;
}

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;
}

public sealed class FakeOutput : IReceiptOutput
{
    public List<(Receipt Receipt, bool OpenDrawer, bool Copy)> Printed { get; } = [];
    public bool Fail { get; set; }
    public Action? OnPrint { get; set; }

    public void Print(Receipt receipt, bool openDrawer, bool copy = false)
    {
        OnPrint?.Invoke();
        if (Fail) throw new InvalidOperationException("Printer offline");
        Printed.Add((receipt, openDrawer, copy));
    }

    public List<(ShiftOpening Opening, ShiftClosing Closing, string CashierName, string? ApprovedBy)> ShiftReports { get; } = [];

    public void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy)
    {
        if (Fail) throw new InvalidOperationException("Printer offline");
        ShiftReports.Add((opening, closing, cashierName, approvedBy));
    }
}
