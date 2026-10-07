using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using TillPOS.Sync.Upload;

namespace TillPOS.Tests.Presentation;

/// <summary>The ERPNext check of another till's bill: succeeds (and changes nothing) unless <see cref="OnRefresh"/> says otherwise
/// (return false = not in ERPNext; throw = offline).</summary>
public sealed class FakeRemoteReturnsCheck : TillPOS.Sync.IRemoteReturnsCheck
{
    public Func<string, bool>? OnRefresh { get; set; }
    public List<string> Checked { get; } = [];

    public Task<bool> RefreshAsync(string erpName, CancellationToken ct)
    {
        Checked.Add(erpName);
        return Task.FromResult(OnRefresh?.Invoke(erpName) ?? true);
    }
}

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

    /// <summary>When set, the next PIN prompt stays open until the test completes this (then it is cleared).</summary>
    public TaskCompletionSource<string?>? PendingPin { get; set; }

    public Task<string?> AskPinAsync(string title, string reason)
    {
        PinRequests++;
        if (PendingPin is { } pending) { PendingPin = null; return pending.Task; }
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

    /// <summary>The upload-mode change the next setup reports (null: none).</summary>
    public (UploadMode From, UploadMode To)? SetupUploadChange { get; set; }

    /// <summary>What the upload-mode callback answered last (false: the old mode would be saved back).</summary>
    public bool? SetupUploadAccepted { get; private set; }

    public async Task<bool> ShowSetupAsync(Func<UploadMode, UploadMode, Task<bool>> uploadModeChanged)
    {
        SetupRequests++;
        if (SetupUploadChange is { } change) SetupUploadAccepted = await uploadModeChanged(change.From, change.To);
        return SetupResults.Count > 0 && SetupResults.Dequeue();
    }

    /// <summary>Answers to yes/no questions (none left: No); the questions asked.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();
    public List<string> Confirms { get; } = [];

    public List<(string Title, string Text)> Texts { get; } = [];

    /// <summary>Answers to text prompts (none left: cancelled); the prompts asked.</summary>
    public Queue<string?> TextAnswers { get; } = new();
    public List<string> TextPrompts { get; } = [];

    public Task<string?> AskTextAsync(string title, string prompt)
    {
        TextPrompts.Add(prompt);
        return Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    }
    public void ShowText(string title, string text) => Texts.Add((title, text));

    public bool Confirm(string title, string message)
    {
        Confirms.Add(message);
        return ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue();
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

    public List<(ShiftOpening Opening, ShiftClosing Closing, string CashierName, string? ApprovedBy, decimal? FirstCountDifference)>
        ShiftReports { get; } = [];

    public void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy,
        decimal? firstCountDifference)
    {
        if (Fail) throw new InvalidOperationException("Printer offline");
        ShiftReports.Add((opening, closing, cashierName, approvedBy, firstCountDifference));
    }
}
