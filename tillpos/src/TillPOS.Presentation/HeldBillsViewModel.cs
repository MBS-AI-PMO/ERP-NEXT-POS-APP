using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Security;

namespace TillPOS.Presentation;

public sealed record HeldBillRow(string Id, string Label, string HeldAt);

/// <summary>The bills on hold (recall F7). Recalling is done by the sale screen with the chosen <see cref="Selected"/> id;
/// deleting a held bill needs a supervisor and is logged.</summary>
public sealed class HeldBillsViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SupervisorGate gate;
    private HeldBillRow? selected;
    private string message = "";

    public HeldBillsViewModel(TillContext ctx, SessionState session, SupervisorGate gate)
    {
        _ = session; // the gate logs the cashier from the same session
        this.ctx = ctx;
        this.gate = gate;
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        Reload();
    }

    public ObservableCollection<HeldBillRow> Bills { get; } = [];
    public HeldBillRow? Selected { get => selected; set => SetProperty(ref selected, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public AsyncRelayCommand DeleteSelectedCommand { get; }

    /// <summary>Lists the held bills again; a store failure leaves the list empty with a message (never throws).</summary>
    public void Reload()
    {
        if (LoadBills() is { } error) Message = error;
    }

    /// <summary>Fills <see cref="Bills"/>; returns the problem when the store failed (the list is then empty).</summary>
    private string? LoadBills()
    {
        var selectedId = Selected?.Id;
        string? error = null;
        Bills.Clear();
        try
        {
            foreach (var held in ctx.Held.List())
                Bills.Add(new HeldBillRow(held.Id, held.Label, held.HeldAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex)
        {
            Bills.Clear();
            error = $"Could not read the bills on hold: {ex.Message}";
        }
        Selected = Bills.FirstOrDefault(b => b.Id == selectedId) ?? Bills.FirstOrDefault();
        return error;
    }

    /// <summary>Deletes the selected held bill after a supervisor approves (logged as <see cref="ApprovalAction.HeldBillDelete"/>).
    /// If the store fails the bill stays on hold and the message says so.</summary>
    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } bill) return;
        if (await gate.ApproveAsync(ApprovalAction.HeldBillDelete, $"Delete held bill {bill.Label}", null, null, 0m) is null) return;
        string result;
        try
        {
            result = ctx.Held.Take(bill.Id) is null ? "That bill was already recalled" : "Held bill deleted";
        }
        catch (Exception)
        {
            result = "Could not delete the bill — it is still on hold";
        }
        Selected = null;
        Message = LoadBills() is { } error ? $"{result}. {error}" : result;
    }
}
