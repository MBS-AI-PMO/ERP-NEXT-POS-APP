using TillPOS.App;
using TillPOS.Core.Shifts;
using TillPOS.Sync.Upload;

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

    [Fact]
    public void ToString_lists_the_sample_qr_and_upload_settings_last()
    {
        Assert.EndsWith(", SetupDone = False, SampleQr = True, Upload = Off }", new TillSettings("https://erp.example", "k", SampleQr: true).ToString());
        Assert.EndsWith(", Upload = Live }", new TillSettings("https://erp.example", "k", Upload: UploadMode.Live).ToString());
        Assert.False(new TillSettings("https://erp.example", "k").SampleQr);
    }

    [Fact]
    public void Test_builds_are_recognised_and_never_live()
    {
        var production = new TillSettings("https://erp.example", "k", Upload: UploadMode.Live);
        Assert.False(production.IsTestBuild);
        Assert.Equal(UploadMode.Live, production.EffectiveUpload);

        foreach (var test in new[]
        {
            production with { SampleQr = true },
            production with { LocalTestCashiers = [new LocalTestCashier("c", "C", "1234", false)] },
        })
        {
            Assert.True(test.IsTestBuild);
            Assert.Equal(UploadMode.Off, test.EffectiveUpload);
            Assert.Equal(UploadMode.DryRun, (test with { Upload = UploadMode.DryRun }).EffectiveUpload);
        }
        Assert.False((production with { LocalTestCashiers = [] }).IsTestBuild);
    }

    [Fact]
    public void Upload_is_off_by_default() => Assert.Equal(UploadMode.Off, new TillSettings("https://erp.example", "k").Upload);

    [Fact]
    public void Without_counters_the_single_profile_is_the_only_counter() =>
        Assert.Equal([new CounterSettings("Test Counter", "", "Cash Counter 2", "Credit Card")],
            new TillSettings("https://erp.example", "k", PosProfile: "Test Counter", CashMode: "Cash Counter 2", CardMode: "Credit Card")
                .EffectiveCounters());

    [Fact]
    public void Configured_counters_are_used_in_order_without_blanks_or_repeats()
    {
        var settings = new TillSettings("https://erp.example", "k", PosProfile: "Test Counter", CashMode: "Cash Counter 2", CardMode: "Credit Card",
            Counters:
            [
                new CounterSettings("Test Counter", "Test Counter", "Cash Counter 2", "Credit Card"),
                new CounterSettings(" ", "Nothing"),
                new CounterSettings(" Al Ain Counter 1 ", "Counter 1", "Cash Counter 1", ""),
                new CounterSettings("test counter", "Again", "X", "Y"),
            ]);

        Assert.Equal(
        [
            new CounterSettings("Test Counter", "Test Counter", "Cash Counter 2", "Credit Card"),
            new CounterSettings("Al Ain Counter 1", "Counter 1", "Cash Counter 1", "Credit Card"),   // blank card mode: the till's
        ], settings.EffectiveCounters());
    }

    [Fact]
    public void Counters_with_json_nulls_do_not_break()
    {
        var settings = new TillSettings("https://erp.example", "k", PosProfile: "P", CashMode: "Cash", CardMode: "Card",
            Counters: [new CounterSettings("Q", null!, null!, null!), null!]);
        Assert.Equal([new CounterSettings("Q", "", "Cash", "Card")], settings.EffectiveCounters());
    }

    [Fact]
    public void ToString_lists_the_counters()
    {
        var text = new TillSettings("https://erp.example", "k", Counters: [new CounterSettings("Al Ain Counter 1", "Counter 1", "Cash Counter 1", "Credit Card")])
            .ToString();
        Assert.Contains("Al Ain Counter 1", text);
        Assert.Contains("Cash Counter 1", text);
    }

    private static readonly TillSettings Single =
        new("https://erp.example", "k", PosProfile: "Test Counter", CashMode: "Cash Counter 2", CardMode: "Credit Card");

    [Fact]
    public void Edited_counters_are_saved_and_the_first_becomes_the_single_profile_too()
    {
        var rows = new[]
        {
            new CounterSettings(" Al Ain Counter 1 ", " Counter 1 ", "Cash Counter 1", ""),
            new CounterSettings("", "", "", ""),                                 // an empty row is ignored
            new CounterSettings("Al Ain Counter 2", "Counter 2", "Cash Counter 2", "Credit Card"),
        };

        Assert.Null(TillSettings.CounterProblem(rows));
        var saved = Single.WithCounters(rows);

        Assert.Equal(
        [
            new CounterSettings("Al Ain Counter 1", "Counter 1", "Cash Counter 1", ""),
            new CounterSettings("Al Ain Counter 2", "Counter 2", "Cash Counter 2", "Credit Card"),
        ], saved.Counters!);
        Assert.Equal(("Al Ain Counter 1", "Cash Counter 1", "Credit Card"), (saved.PosProfile, saved.CashMode, saved.CardMode));
        Assert.Equal("Credit Card", saved.EffectiveCounters()[0].CardMode);
    }

    [Fact]
    public void Removing_every_counter_row_keeps_the_single_profile()
    {
        var saved = Single with { Counters = [new CounterSettings("X", "", "Cash X", "Card")] };
        Assert.Empty(saved.WithCounters([]).Counters!);
        Assert.Equal("Test Counter", Assert.Single(saved.WithCounters([]).EffectiveCounters()).PosProfile);
        Assert.Equal("Test Counter", saved.WithCounters([]).PosProfile);
    }

    [Theory]
    [InlineData("", "Counter 1", "Cash Counter 1", "POS Profile")]
    [InlineData("Al Ain Counter 1", "Counter 1", "", "cash mode")]
    public void A_counter_needs_its_profile_and_cash_mode(string profile, string label, string cash, string expected) =>
        Assert.Contains(expected, TillSettings.CounterProblem([new CounterSettings(profile, label, cash, "Credit Card")]));

    [Fact]
    public void A_profile_can_only_be_one_counter() =>
        Assert.Contains("twice", TillSettings.CounterProblem(
        [
            new CounterSettings("Al Ain Counter 1", "A", "Cash 1", ""),
            new CounterSettings("al ain counter 1", "B", "Cash 2", ""),
        ]));

    [Fact]
    public void An_added_row_with_only_its_prefilled_card_mode_counts_as_empty()
    {
        var rows = new[]
        {
            new CounterSettings("Al Ain Counter 1", "Counter 1", "Cash Counter 1", "Credit Card"),
            new CounterSettings("", "", "", "Credit Card"),
        };
        Assert.Null(TillSettings.CounterProblem(rows));
        Assert.Single(Single.WithCounters(rows).Counters!);
    }
}
