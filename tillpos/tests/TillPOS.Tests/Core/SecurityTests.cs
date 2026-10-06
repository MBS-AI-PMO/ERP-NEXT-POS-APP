using TillPOS.Core.Security;

namespace TillPOS.Tests.Core;

public class SecurityTests
{
    private static Cashier C(string id, string pin, bool supervisor = false, bool enabled = true) =>
        new(id, id, null, PinHasher.Hash(pin), supervisor, enabled);

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456", true)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    public void Pins_are_4_to_6_digits(string pin, bool valid) => Assert.Equal(valid, PinHasher.IsValidPin(pin));

    [Fact]
    public void Hash_is_salted_and_verifies()
    {
        var a = PinHasher.Hash("4321");
        var b = PinHasher.Hash("4321");
        Assert.NotEqual(a, b);
        Assert.True(PinHasher.Verify("4321", a));
        Assert.False(PinHasher.Verify("4322", a));
        Assert.DoesNotContain("4321", a);
    }

    [Fact]
    public void Login_finds_the_enabled_cashier_with_that_pin()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("ben", "2222"), C("old", "3333", enabled: false)]);
        Assert.Equal("ben", auth.Login("2222")!.Id);
        Assert.Null(auth.Login("3333"));
        Assert.Null(auth.Login("9999"));
    }

    [Fact]
    public void Only_supervisors_pass_the_supervisor_check()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("sup", "5555", supervisor: true)]);
        Assert.Null(auth.Supervisor("1111"));
        Assert.Equal("sup", auth.Supervisor("5555")!.Id);
    }

    [Fact]
    public void Duplicate_pin_logs_nobody_in()
    {
        var auth = new Authenticator(() => [C("ana", "1111"), C("ben", "1111")]);
        Assert.Null(auth.Login("1111"));
    }

    [Fact]
    public void Invalid_pin_is_not_hashed() => Assert.Throws<ArgumentException>(() => PinHasher.Hash("12"));

    [Theory]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$10000$not base64!$AAAA")]
    [InlineData("pbkdf2-sha256$10000$AAAAAAAAAAAAAAAAAAAAAA==$AAAA")]
    public void Malformed_stored_hash_never_verifies(string stored) => Assert.False(PinHasher.Verify("1234", stored));

    [Fact]
    public void A_corrupt_cashier_row_does_not_block_other_logins()
    {
        var auth = new Authenticator(() => [new Cashier("bad", "Bad", null, "pbkdf2-sha256$0$x$y", false, true), C("ben", "2222")]);
        Assert.Equal("ben", auth.Login("2222")!.Id);
    }
}
