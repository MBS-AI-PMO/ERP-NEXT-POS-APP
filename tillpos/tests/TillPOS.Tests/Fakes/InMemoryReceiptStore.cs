using TillPOS.Core.Sales;

namespace TillPOS.Tests.Fakes;

public sealed class InMemoryReceiptStore : IReceiptStore
{
    private long sequence;
    public List<Receipt> Saved { get; } = [];

    public long NextSequence() => ++sequence;
    public void Save(Receipt receipt)
    {
        if (Saved.Any(r => r.ClientId == receipt.ClientId)) throw new InvalidOperationException("duplicate client id");
        Saved.Add(receipt);
    }
    public Receipt? Get(string clientId) => Saved.FirstOrDefault(r => r.ClientId == clientId);
    public IReadOnlyList<Receipt> ReturnsAgainst(string clientId) => Saved.Where(r => r.ReturnAgainst == clientId).ToList();
    public IReadOnlyList<Receipt> ByShift(string shiftClientId) => Saved.Where(r => r.ShiftClientId == shiftClientId).ToList();
}
