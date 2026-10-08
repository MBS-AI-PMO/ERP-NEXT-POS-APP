using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

/// <summary>Delivery bills on this till (never uploaded while open). Paying writes the receipt and closes the delivery in one
/// transaction, so a crash cannot leave a paid bill still listed, or a paid delivery without its receipt.</summary>
public sealed class DeliveryStore(TillDb db, ReceiptStore receipts)
{
    public void Add(Delivery d)
    {
        using var c = db.Open();
        c.Exec(null, "INSERT INTO delivery (client_id, status, created_at, json) VALUES (@id, @s, @at, @j)",
            ("@id", d.ClientId), ("@s", d.Status.ToString()), ("@at", Utc(d.CreatedAt)), ("@j", JsonSerializer.Serialize(d, ReceiptStore.Json)));
    }

    public Delivery? Get(string clientId)
    {
        using var c = db.Open();
        return c.Scalar(null, "SELECT json FROM delivery WHERE client_id = @id", ("@id", clientId)) is string json ? Read(json) : null;
    }

    /// <summary>The deliveries still out, oldest first.</summary>
    public IReadOnlyList<Delivery> Open()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM delivery WHERE status = 'Open' ORDER BY created_at", r => Read(r.GetString(0)));
    }

    /// <summary>Saves a changed open delivery; false when it is no longer open.</summary>
    public bool Update(Delivery d)
    {
        using var c = db.Open();
        return c.Exec(null, "UPDATE delivery SET json = @j WHERE client_id = @id AND status = 'Open'",
            ("@id", d.ClientId), ("@j", JsonSerializer.Serialize(d with { Status = DeliveryStatus.Open }, ReceiptStore.Json))) > 0;
    }

    public bool Cancel(string clientId, DateTimeOffset at, string by, string reason) =>
        Close(clientId, DeliveryStatus.Cancelled, at, by, reason, null);

    /// <summary>Saves the paid receipt (its ClientId is the delivery's) and marks the delivery paid, together; false (and nothing
    /// saved) when it is no longer open.</summary>
    public bool Pay(Receipt receipt, DateTimeOffset at, string by)
    {
        if (!Close(receipt.ClientId, DeliveryStatus.Paid, at, by, null, receipt)) return false;
        receipts.RaiseSaved();
        return true;
    }

    private bool Close(string clientId, DeliveryStatus status, DateTimeOffset at, string by, string? reason, Receipt? paid)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (c.Scalar(tx, "SELECT json FROM delivery WHERE client_id = @id AND status = 'Open'", ("@id", clientId)) is not string json)
            return false;
        var closed = Read(json) with { Status = status, ClosedAt = at, ClosedBy = by, Reason = reason };
        if (paid is not null) ReceiptStore.Insert(c, tx, paid);
        c.Exec(tx, "UPDATE delivery SET status = @s, json = @j WHERE client_id = @id",
            ("@id", clientId), ("@s", status.ToString()), ("@j", JsonSerializer.Serialize(closed, ReceiptStore.Json)));
        tx.Commit();
        return true;
    }

    private static Delivery Read(string json) =>
        JsonSerializer.Deserialize<Delivery>(json, ReceiptStore.Json) ?? throw new InvalidDataException("Stored delivery is empty.");

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
