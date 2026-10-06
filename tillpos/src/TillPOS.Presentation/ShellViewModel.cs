using CommunityToolkit.Mvvm.ComponentModel;

namespace TillPOS.Presentation;

/// <summary>The window frame: which screen is showing, plus the header (shop, till, cashier, sync status, clock).</summary>
public sealed class ShellViewModel : ObservableObject, INavigator
{
    private object? current;
    private string shopName = "";
    private string tillName = "";
    private string syncStatus = "Starting…";
    private bool online;
    private int pendingUploads;
    private string clock = "";

    public SessionState Session { get; } = new();
    public object? Current { get => current; private set => SetProperty(ref current, value); }
    public string ShopName { get => shopName; set => SetProperty(ref shopName, value); }
    public string TillName { get => tillName; set => SetProperty(ref tillName, value); }
    public string SyncStatus { get => syncStatus; set => SetProperty(ref syncStatus, value); }
    public bool Online { get => online; set => SetProperty(ref online, value); }
    public int PendingUploads { get => pendingUploads; set => SetProperty(ref pendingUploads, value); }
    public string Clock { get => clock; set => SetProperty(ref clock, value); }

    public void Show(object viewModel) => Current = viewModel;
}
