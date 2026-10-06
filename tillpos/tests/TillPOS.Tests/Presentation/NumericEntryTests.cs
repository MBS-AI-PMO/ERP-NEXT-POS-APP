using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public class NumericEntryTests
{
    [Fact]
    public void Digits_dot_and_backspace()
    {
        var e = new NumericEntry();
        foreach (var c in "025") e.Digit(c);
        e.Dot();
        e.Digit('5');
        Assert.Equal("25.5", e.Text);
        Assert.Equal(M("25.5"), e.Value);
        e.Backspace();
        e.Backspace();
        Assert.Equal("25", e.Text);
    }

    [Fact]
    public void At_most_three_decimals_and_one_dot()
    {
        var e = new NumericEntry();
        e.Digit('1');
        e.Dot();
        e.Dot();
        foreach (var c in "2345") e.Digit(c);
        Assert.Equal("1.234", e.Text);
    }

    [Fact]
    public void Typed_text_is_validated()
    {
        var e = new NumericEntry { Text = "12.50" };
        e.Text = "12a";
        Assert.Equal("12.50", e.Text);
        e.Text = "";
        Assert.Null(e.Value);
    }

    [Fact]
    public void Set_and_change_notification()
    {
        var e = new NumericEntry();
        var changes = 0;
        e.Changed += () => changes++;
        e.Set(50m);
        Assert.Equal("50", e.Text);
        Assert.Equal(1, changes);
    }
}
