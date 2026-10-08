using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class PaymentHostTests : IDisposable
{
    private readonly PresentationFixture f = new();

    public PaymentHostTests() => f.LogInWithOpenShift();

    public void Dispose() => f.Dispose();

    private sealed class Host(Cart cart, bool prefill) : IPaymentHost
    {
        public Cart Cart => cart;
        public IReadOnlyList<SaleLine> Lines => SaleViewModel.LinesOf(cart);
        public string ItemCount => cart.Lines.Count.ToString();
        public string Discount => "0.00";
        public string Vat => "0.00";
        public MoneySettings Money => cart.Context.Money;
        public bool PrefillExactCash => prefill;
        public List<PaymentPlan> Recorded { get; } = [];
        public Receipt? Done { get; private set; }
        public int Backs { get; private set; }

        public Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift)
        {
            Recorded.Add(plan);
            return new Receipt("X-1", ReceiptKind.Sale, null, shift.ClientId, cashier.Id, DateTimeOffset.Now, [], 0m, 0m, 0m, plan.GrandTotal,
                false, 0m, 0m, [], plan.Change, 0m, null);
        }

        public void Completed(Receipt receipt, string? printError) => Done = receipt;
        public void BackFromPayment() => Backs++;
    }

    private Cart Milk()
    {
        var cart = new Cart(f.Ctx.SaleContextFor(f.Session));
        cart.AddBarcode("111");                                          // 6.79
        return cart;
    }

    [Fact]
    public void Exact_cash_is_prefilled_so_no_change_is_given()
    {
        var vm = new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: true), TenderKind.Cash);

        Assert.Equal("6.75", vm.Cash.Text);
        Assert.Equal("0.00", vm.Change);
        Assert.True(vm.Plan!.IsComplete);
    }

    [Fact]
    public void Switching_to_cash_later_prefills_too_but_never_overwrites_typed_cash()
    {
        var vm = new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: true), TenderKind.Card);
        vm.Kind = TenderKind.Cash;
        Assert.Equal("6.75", vm.Cash.Text);

        vm.Cash.Set(10m);
        vm.Kind = TenderKind.Card;
        vm.Kind = TenderKind.Cash;
        Assert.Equal("10", vm.Cash.Text);
    }

    [Fact]
    public void Without_prefill_the_cash_box_starts_empty() =>
        Assert.Equal("", new PaymentViewModel(f.Ctx, f.Session, new Host(Milk(), prefill: false), TenderKind.Cash).Cash.Text);

    [Fact]
    public void Completing_records_through_the_host_prints_and_hands_back()
    {
        var host = new Host(Milk(), prefill: true);
        var vm = new PaymentViewModel(f.Ctx, f.Session, host, TenderKind.Cash);

        vm.CompleteCommand.Execute(null);

        Assert.Single(host.Recorded);
        Assert.Equal("X-1", host.Done!.ClientId);
        Assert.Single(f.Output.Printed);
    }

    [Fact]
    public void Back_goes_to_the_host()
    {
        var host = new Host(Milk(), prefill: false);
        new PaymentViewModel(f.Ctx, f.Session, host, TenderKind.Cash).BackCommand.Execute(null);
        Assert.Equal(1, host.Backs);
    }
}
