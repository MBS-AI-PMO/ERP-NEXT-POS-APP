using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public class ScanBufferTests
{
    private DateTimeOffset now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private string? Type(ScanBuffer buffer, string text, int msBetween)
    {
        string? result = null;
        foreach (var c in text)
        {
            now = now.AddMilliseconds(msBetween);
            result = buffer.OnChar(c) ?? result;
        }
        return result;
    }

    [Fact]
    public void Fast_burst_ending_in_enter_is_a_scan() =>
        Assert.Equal("6291105656948", Type(new ScanBuffer(() => now), "6291105656948\r", 10));

    [Fact]
    public void Slow_typing_is_not_a_scan() =>
        Assert.Null(Type(new ScanBuffer(() => now), "1234\r", 200));

    [Fact]
    public void Too_short_burst_is_not_a_scan() =>
        Assert.Null(Type(new ScanBuffer(() => now), "12\r", 10));

    [Fact]
    public void Scan_burst_is_detected_regardless_of_slow_typing_before_it()
    {
        var buffer = new ScanBuffer(() => now);
        Assert.Null(Type(buffer, "mil", 250));
        now = now.AddMilliseconds(300);
        Assert.Equal("2000089007400", Type(buffer, "2000089007400\r", 8));
    }

    [Fact]
    public void Enter_long_after_the_burst_is_not_a_scan()
    {
        var buffer = new ScanBuffer(() => now);
        Type(buffer, "6291105656948", 10);
        now = now.AddMilliseconds(500);
        Assert.Null(buffer.OnChar('\r'));
    }
}
