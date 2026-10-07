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

    [Fact]
    public void Code128_sets_height_width_and_text_below_then_prints_code_set_b() =>
        Assert.Equal(new byte[]
            {
                0x1D, 0x68, 0x3C,                                  // GS h 60
                0x1D, 0x77, 0x02,                                  // GS w 2
                0x1D, 0x48, 0x02,                                  // GS H 2: text below
                0x1D, 0x6B, 0x49, 0x06, 0x7B, 0x42, 0x54, 0x49, 0x4C, 0x4C,   // GS k 73 6 {B TILL
            },
            new EscPos().Barcode128("TILL").ToArray());

    [Fact]
    public void Code128_packs_digit_runs_in_code_set_c_so_an_invoice_number_fits_80mm_paper()
    {
        var bytes = new EscPos().Barcode128("TILL2-20261006153000-000001").ToArray();

        var data = new byte[]
        {
            0x7B, 0x42, (byte)'T', (byte)'I', (byte)'L', (byte)'L', (byte)'2', (byte)'-',
            0x7B, 0x43, 20, 26, 10, 6, 15, 30, 0,
            0x7B, 0x42, (byte)'-',
            0x7B, 0x43, 0, 0, 1,
        };
        Assert.Equal(new byte[] { 0x1D, 0x6B, 0x49, (byte)data.Length }.Concat(data), bytes[9..]);
    }

    [Fact]
    public void Code128_keeps_an_odd_leading_digit_in_code_set_b_and_escapes_braces()
    {
        Assert.Equal(new byte[] { 0x7B, 0x42, (byte)'A', (byte)'1', 0x7B, 0x43, 23, 45 }, new EscPos().Barcode128("A12345").ToArray()[13..]);
        Assert.Equal(new byte[] { 0x7B, 0x42, (byte)'A', 0x7B, 0x7B, (byte)'B' }, new EscPos().Barcode128("A{B").ToArray()[13..]);
        Assert.Equal(new byte[] { 0x7B, 0x42, (byte)'1', (byte)'2', (byte)'3' }, new EscPos().Barcode128("123").ToArray()[13..]);
    }

    [Fact]
    public void Code128_module_width_can_be_narrowed() =>
        Assert.Equal(new byte[] { 0x1D, 0x77, 0x01 }, new EscPos().Barcode128("A", moduleWidth: 1).ToArray()[3..6]);

    [Theory]
    [InlineData("")]
    [InlineData("TILLÄ2")]
    [InlineData("A\nB")]
    public void Code128_refuses_anything_but_printable_ascii(string data) =>
        Assert.Throws<ArgumentException>(() => new EscPos().Barcode128(data));

    [Fact]
    public void Code128_refuses_data_too_long_for_one_command() =>
        Assert.Throws<ArgumentException>(() => new EscPos().Barcode128(new string('A', 254)));

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
