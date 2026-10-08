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
    public void Upload_problems_are_listed_one_per_line_for_the_header_tooltip()
    {
        var shell = new ShellViewModel();
        Assert.Null(shell.UploadProblemsText);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.UploadProblems = ["Bill TILL2-A: Item RICE5 is disabled", "Return TILL2-R waits for its original sale TILL2-A to upload."];

        Assert.Equal($"Bill TILL2-A: Item RICE5 is disabled{Environment.NewLine}Return TILL2-R waits for its original sale TILL2-A to upload.",
            shell.UploadProblemsText);
        Assert.Contains(nameof(ShellViewModel.UploadProblemsText), changed);
    }

    [Fact]
    public void The_dev_badge_follows_the_environment()
    {
        var shell = new ShellViewModel();
        Assert.False(shell.IsDev);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.IsDev = true;

        Assert.Contains(nameof(ShellViewModel.IsDev), changed);
    }

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
