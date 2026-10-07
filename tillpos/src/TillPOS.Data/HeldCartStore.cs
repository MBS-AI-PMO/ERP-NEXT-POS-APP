using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

public sealed class HeldCartStore(TillDb db)
{
    public HeldCart Hold(Cart cart, string label, DateTimeOffset at)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("There is nothing to hold.");
        var held = new HeldCart(Guid.NewGuid().ToString("N"), label, at, cart.Snapshot());
        Put(held);
        cart.Clear();
        return held;
    }

    /// <summary>Stores a held bill as it is (same id, label and time).</summary>
    public void Put(HeldCart held)
    {
        using var c = db.Open();
        c.Exec(null, "INSERT INTO held_cart (id, label, held_at, json) VALUES (@id, @l, @at, @j)",
            ("@id", held.Id), ("@l", held.Label), ("@at", held.HeldAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(held)));
    }

    public IReadOnlyList<HeldCart> List()
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM held_cart ORDER BY held_at", r => JsonSerializer.Deserialize<HeldCart>(r.GetString(0))!);
    }

    /// <summary>Removes and returns a held bill, or null if another action already took it.</summary>
    public HeldCart? Take(string id)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var json = c.Scalar(tx, "SELECT json FROM held_cart WHERE id = @id", ("@id", id)) as string;
        if (json is null) return null;
        c.Exec(tx, "DELETE FROM held_cart WHERE id = @id", ("@id", id));
        tx.Commit();
        return JsonSerializer.Deserialize<HeldCart>(json);
    }
}
