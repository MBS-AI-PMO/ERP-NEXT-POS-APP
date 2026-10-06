using TillPOS.App;

namespace TillPOS.Tests.App;

public class TillSettingsTests
{
    [Fact]
    public void ToString_masks_the_key_the_secrets_and_the_pins()
    {
        var settings = new TillSettings("https://erp.example", "key-12345", "PROTECTED-BLOB", "Till 1", 1, "Cash", "Card",
            LocalTestCashiers: [new LocalTestCashier("c1", "Test Cashier", "4821", false)], ApiSecret: "plain-secret");

        var text = settings.ToString();

        Assert.Contains("https://erp.example", text);
        Assert.Contains("Test Cashier", text);
        Assert.Contains("***", text);
        foreach (var hidden in new[] { "key-12345", "PROTECTED-BLOB", "plain-secret", "4821" }) Assert.DoesNotContain(hidden, text);
        Assert.DoesNotContain("4821", new LocalTestCashier("c1", "Test Cashier", "4821", false).ToString());
    }
}
