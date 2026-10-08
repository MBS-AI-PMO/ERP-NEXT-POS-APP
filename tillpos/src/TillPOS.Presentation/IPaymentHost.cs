using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>What the payment screen pays: the sale screen's bill, or a delivery being paid.</summary>
public interface IPaymentHost
{
    Cart Cart { get; }
    IReadOnlyList<SaleLine> Lines { get; }
    string ItemCount { get; }
    string Discount { get; }
    string Vat { get; }
    MoneySettings Money { get; }
    /// <summary>Cash starts with the exact amount due in the cash box, so no change is given (deliveries).</summary>
    bool PrefillExactCash { get; }
    /// <summary>Saves the paid bill and empties the cart; throws when it cannot (nothing is then saved).</summary>
    Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift);
    /// <summary>After the bill is saved and printed (or the print failed).</summary>
    void Completed(Receipt receipt, string? printError);
    /// <summary>Back (Esc) from the payment screen.</summary>
    void BackFromPayment();
}
