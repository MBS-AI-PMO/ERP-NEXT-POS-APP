using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Shifts;

namespace TillPOS.Data;

public sealed class ShiftStore(TillDb db)
{
    public void Open(ShiftOpening opening)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (c.Scalar(tx, "SELECT client_id FROM shift WHERE closed_at IS NULL") is string open)
            throw new InvalidOperationException($"Shift {open} is still open; close it first.");
        c.Exec(tx, "INSERT INTO shift (client_id, opened_at, opening_json) VALUES (@id, @at, @j)",
            ("@id", opening.ClientId), ("@at", opening.OpenedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(opening)));
        tx.Commit();
    }

    public ShiftOpening? Current()
    {
        using var c = db.Open();
        return c.Query("SELECT opening_json FROM shift WHERE closed_at IS NULL",
            r => JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!).FirstOrDefault();
    }

    public void Close(ShiftClosing closing)
    {
        using var c = db.Open();
        var changed = c.Exec(null, "UPDATE shift SET closed_at = @at, closing_json = @j WHERE client_id = @id AND closed_at IS NULL",
            ("@id", closing.ShiftClientId), ("@at", closing.ClosedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("@j", JsonSerializer.Serialize(closing)));
        if (changed == 0) throw new InvalidOperationException($"Shift {closing.ShiftClientId} is not open.");
    }

    public (ShiftOpening Opening, ShiftClosing? Closing)? Get(string clientId)
    {
        using var c = db.Open();
        var rows = c.Query("SELECT opening_json, closing_json FROM shift WHERE client_id = @id",
            r => (JsonSerializer.Deserialize<ShiftOpening>(r.GetString(0))!,
                  r.IsDBNull(1) ? null : JsonSerializer.Deserialize<ShiftClosing>(r.GetString(1))),
            ("@id", clientId));
        return rows.Count == 0 ? null : rows[0];
    }
}
