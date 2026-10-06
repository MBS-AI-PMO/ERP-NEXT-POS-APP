using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class LoginViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object saleMarker = new();

    public void Dispose() => f.Dispose();

    private LoginViewModel Login() => new(f.Ctx, f.Session, () => saleMarker);

    private static void Enter(LoginViewModel vm, string pin)
    {
        foreach (var c in pin) vm.DigitCommand.Execute(c.ToString());
        vm.LoginCommand.Execute(null);
    }

    [Fact]
    public void Wrong_pin_shows_a_message_and_clears()
    {
        var vm = Login();
        Enter(vm, "4321");
        Assert.Equal("Wrong PIN.", vm.Message);
        Assert.Equal("", vm.Pin);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Five_wrong_pins_lock_login_even_for_the_right_pin()
    {
        var vm = Login();
        for (var i = 0; i < 5; i++) Enter(vm, "4321");
        Enter(vm, "1111");
        Assert.StartsWith("Too many wrong PINs", vm.Message);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Right_pin_without_an_open_shift_goes_to_open_shift()
    {
        Enter(Login(), "1111");
        Assert.Equal("simran", f.Session.Cashier!.Id);
        Assert.IsType<OpenShiftViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Right_pin_with_an_open_shift_goes_to_the_sale_screen()
    {
        f.LogInWithOpenShift();
        f.Session.Cashier = null;
        f.Session.Shift = null;

        Enter(Login(), "1111");

        Assert.Same(saleMarker, f.Navigator.Current);
        Assert.Equal("TILL2-SHIFT-20261007080000", f.Session.Shift!.ClientId);
    }

    [Fact]
    public void Pin_is_at_most_six_digits_and_masked()
    {
        var vm = Login();
        foreach (var c in "12345678") vm.DigitCommand.Execute(c.ToString());
        Assert.Equal("123456", vm.Pin);
        Assert.Equal("●●●●●●", vm.PinMask);
    }

    [Fact]
    public void Opening_a_shift_records_the_float_and_goes_to_sale()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpeningCash.Text = "200";

        vm.OpenCommand.Execute(null);

        var shift = f.Ctx.Shifts.Current()!;
        Assert.Equal("TILL2-SHIFT-20261007100000", shift.ClientId);
        Assert.Equal(200m, Assert.Single(shift.OpeningAmounts).Amount);
        Assert.Equal("Cash Counter 2", shift.OpeningAmounts[0].ModeOfPayment);
        Assert.Same(saleMarker, f.Navigator.Current);
    }

    [Fact]
    public void Opening_a_shift_needs_an_amount()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpenCommand.Execute(null);
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal("Enter the cash in the drawer.", vm.Message);
    }
}
