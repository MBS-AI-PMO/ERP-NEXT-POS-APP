using TillPOS.Core.Sales;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ScaleLabelTests
{
    /// <summary>Appends the EAN-13 check digit to 12 digits.</summary>
    public static string Ean(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (10 - sum % 10) % 10;
    }

    [Fact]
    public void Parses_the_cucumber_label_from_the_shop()
    {
        Assert.Equal("2000089007400", Ean("200008900740"));
        var label = ScaleLabel.TryParse("2000089007400")!;
        Assert.Equal(740, label.Value);
        Assert.Equal(new[] { "2000089", "000089" }, label.LookupKeys);
    }

    [Theory]
    [InlineData("2000089007401")]   // wrong check digit
    [InlineData("200008900000")]    // 12 digits
    [InlineData("1000089007400")]   // not starting with 2
    [InlineData("20000890074A0")]   // not all digits
    public void Rejects_codes_that_are_not_valid_scale_labels(string code) => Assert.Null(ScaleLabel.TryParse(code));

    [Fact]
    public void Zero_weight_is_not_a_valid_label() => Assert.Null(ScaleLabel.TryParse(Ean("200008900000")));

    [Fact]
    public void Shape_check_does_not_look_at_the_check_digit()
    {
        Assert.True(ScaleLabel.IsScaleLabelShape("2000089007401"));
        Assert.False(ScaleLabel.IsScaleLabelShape("6291105656948"));
    }
}
