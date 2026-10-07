using TillPOS.Presentation;
using TillPOS.Sync.Upload;

namespace TillPOS.Tests.Presentation;

public class ShellUploadTests
{
    [Theory]
    [InlineData(UploadMode.Off, "")]
    [InlineData(UploadMode.DryRun, "DRY RUN")]
    [InlineData(UploadMode.Live, "LIVE UPLOAD")]
    public void Header_badge_names_the_upload_mode(UploadMode mode, string badge) =>
        Assert.Equal(badge, new ShellViewModel { Upload = mode }.UploadBadge);

    [Fact]
    public void Changing_the_mode_updates_the_badge()
    {
        var shell = new ShellViewModel();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.Upload = UploadMode.Live;

        Assert.Contains(nameof(ShellViewModel.UploadBadge), changed);
    }
}
