using TillPOS.Core.Sales;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class FakeDialogs : IDialogs
{
    public Queue<string?> Pins { get; } = new();
    public Queue<decimal?> Numbers { get; } = new();
    public List<string> Infos { get; } = [];
    public int PinRequests { get; private set; }

    public Task<string?> AskPinAsync(string title, string reason)
    {
        PinRequests++;
        return Task.FromResult(Pins.Count > 0 ? Pins.Dequeue() : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt) => Task.FromResult(Numbers.Count > 0 ? Numbers.Dequeue() : null);
    public void Info(string message) => Infos.Add(message);
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
    public List<(Receipt Receipt, bool OpenDrawer)> Printed { get; } = [];
    public bool Fail { get; set; }

    public void Print(Receipt receipt, bool openDrawer)
    {
        if (Fail) throw new InvalidOperationException("Printer offline");
        Printed.Add((receipt, openDrawer));
    }
}
