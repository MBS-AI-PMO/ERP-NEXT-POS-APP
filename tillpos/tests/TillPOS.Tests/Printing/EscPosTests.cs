using TillPOS.Printing;

namespace TillPOS.Tests.Printing;

public class EscPosTests
{
    [Fact]
    public void Init_text_and_cut_produce_the_expected_bytes() =>
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x41, 0x42, 0x0A, 0x1D, 0x56, 0x42, 0x00 },
            new EscPos().Init().Line("AB").Cut().ToArray());

    [Fact]
    public void Characters_the_printer_cannot_show_become_question_marks() =>
        Assert.Equal(new byte[] { 0x3F, 0x0A }, new EscPos().Line("أ").ToArray());

    [Fact]
    public void Drawer_kick_is_esc_p_0_25_250() =>
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, new EscPos().KickDrawer().ToArray());

    [Fact]
    public void Qr_stores_the_data_and_prints_it()
    {
        var bytes = new EscPos().Qr("ABC").ToArray();

        var store = new byte[] { 0x1D, 0x28, 0x6B, 0x06, 0x00, 0x31, 0x50, 0x30, 0x41, 0x42, 0x43 };
        Assert.True(Contains(bytes, store));
        Assert.Equal(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }, bytes[^8..]);
    }

    [Fact]
    public void Alignment_bold_and_size_commands()
    {
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x01 }, new EscPos().Align(Alignment.Center).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x45, 0x01 }, new EscPos().Bold(true).ToArray());
        Assert.Equal(new byte[] { 0x1D, 0x21, 0x11 }, new EscPos().Size(true, true).ToArray());
    }

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
