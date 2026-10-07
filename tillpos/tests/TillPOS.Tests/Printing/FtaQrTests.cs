using System.Text;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class FtaQrTests
{
    internal static Dictionary<int, string> Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var fields = new Dictionary<int, string>();
        for (var i = 0; i < bytes.Length;)
        {
            int tag = bytes[i], length = bytes[i + 1];
            fields[tag] = Encoding.UTF8.GetString(bytes, i + 2, length);
            i += 2 + length;
        }
        return fields;
    }

    [Fact]
    public void Encodes_the_five_tlv_fields()
    {
        var qr = FtaQr.Encode("AL AIN MARKETING L.L.C", "100000000000003",
            new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4)), M("9.994"), M("0.476"));

        var f = Decode(qr);
        Assert.Equal("AL AIN MARKETING L.L.C", f[1]);
        Assert.Equal("100000000000003", f[2]);
        Assert.Equal("2026-10-06T11:30:00Z", f[3]);
        Assert.Equal("9.994", f[4]);
        Assert.Equal("0.476", f[5]);
    }

    [Fact]
    public void Two_decimal_amounts_keep_two_decimals()
    {
        var f = Decode(FtaQr.Encode("S", "1", DateTimeOffset.UnixEpoch, 10m, M("0.48")));
        Assert.Equal("10.00", f[4]);
        Assert.Equal("0.48", f[5]);
    }

    [Fact]
    public void A_field_longer_than_255_bytes_is_refused() =>
        Assert.Throws<ArgumentException>(() => FtaQr.Encode(new string('A', 256), "1", DateTimeOffset.UnixEpoch, 1m, 0m));
}
