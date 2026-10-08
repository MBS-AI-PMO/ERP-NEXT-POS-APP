using CommunityToolkit.Mvvm.ComponentModel;
using TillPOS.Sync.Upload;

namespace TillPOS.Presentation;

/// <summary>The window frame: which screen is showing, plus the header (shop, till and counter, cashier, sync status, clock).</summary>
public sealed class ShellViewModel : ObservableObject, INavigator
{
    private object? current;
    private string shopName = "";
    private string tillName = "";
    private string syncStatus = "Starting…";
    private string? syncNotes;
    private bool online;
    private int pendingUploads;
    private string clock = "";
    private string version = "";
    private UploadMode upload;
    private int failedUploads;
    private IReadOnlyList<string> uploadProblems = [];

    public ShellViewModel()
    {
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionState.Counter)) return;
            OnPropertyChanged(nameof(CounterName));
            OnPropertyChanged(nameof(TillHeader));
        };
    }

    public SessionState Session { get; } = new();
    public object? Current { get => current; private set => SetProperty(ref current, value); }
    public string ShopName { get => shopName; set => SetProperty(ref shopName, value); }
    public string TillName
    {
        get => tillName;
        set { if (SetProperty(ref tillName, value)) OnPropertyChanged(nameof(TillHeader)); }
    }

    /// <summary>The open shift's counter label, or "" with no shift.</summary>
    public string CounterName => Session.Counter?.DisplayName ?? "";

    /// <summary>The header badge: "Till 2 · Counter 1" while a shift is open, otherwise "Till 2".</summary>
    public string TillHeader => CounterName.Length == 0 ? TillName : $"{TillName} · {CounterName}";

    public string SyncStatus { get => syncStatus; set => SetProperty(ref syncStatus, value); }

    /// <summary>Notes of the last catalog pull that did not fail it (e.g. a counter that could not be read), one per line, or
    /// null: the sync status' tooltip.</summary>
    public string? SyncNotes { get => syncNotes; set => SetProperty(ref syncNotes, value); }
    public bool Online { get => online; set => SetProperty(ref online, value); }
    /// <summary>Documents waiting for upload (bills, shift documents, approvals): the header's "N waiting".</summary>
    public int PendingUploads { get => pendingUploads; set => SetProperty(ref pendingUploads, value); }

    /// <summary>Documents ERPNext refused (retried after their backoff): the header's "M failed".</summary>
    public int FailedUploads { get => failedUploads; set => SetProperty(ref failedUploads, value); }

    /// <summary>What the last upload run reported (failures and what is waiting, and why).</summary>
    public IReadOnlyList<string> UploadProblems
    {
        get => uploadProblems;
        set { if (SetProperty(ref uploadProblems, value)) OnPropertyChanged(nameof(UploadProblemsText)); }
    }

    /// <summary>The problems one per line (the header counts' tooltip), or null when there are none.</summary>
    public string? UploadProblemsText => uploadProblems.Count == 0 ? null : string.Join(Environment.NewLine, uploadProblems);
    public string Clock { get => clock; set => SetProperty(ref clock, value); }
    /// <summary>The build (e.g. "0.3.1-field"), shown on the login screen so field feedback can name it.</summary>
    public string Version { get => version; set => SetProperty(ref version, value); }

    /// <summary>The till's upload mode (from the settings; changing it restarts the till).</summary>
    public UploadMode Upload
    {
        get => upload;
        set { if (SetProperty(ref upload, value)) OnPropertyChanged(nameof(UploadBadge)); }
    }

    /// <summary>The header badge: "LIVE UPLOAD" (red) when bills are written to ERPNext, "DRY RUN" (amber) when they are only
    /// previewed, "" when upload is off.</summary>
    public string UploadBadge => upload switch
    {
        UploadMode.Live => "LIVE UPLOAD",
        UploadMode.DryRun => "DRY RUN",
        _ => "",
    };

    /// <summary>Shows a screen; the login screen's upload problem counts are read again whenever it is shown.</summary>
    public void Show(object viewModel)
    {
        Current = viewModel;
        (viewModel as LoginViewModel)?.RefreshUploadProblems();
    }

    /// <summary>An upload run finished (its counts are in the header): the login screen, if shown, reads its counts again.</summary>
    public void UploadsUpdated() => (Current as LoginViewModel)?.RefreshUploadProblems();
}
