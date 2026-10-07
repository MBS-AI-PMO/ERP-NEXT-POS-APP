using System.Globalization;
using System.Security.Cryptography;

namespace TillPOS.Core.Security;

/// <summary>The action needs a supervisor's PIN (spec §13b.5); the caller shows the supervisor challenge and retries.</summary>
public sealed class ApprovalRequiredException(string reason) : Exception(reason);

public enum ApprovalAction { LineVoid, BillVoid, ReturnWithoutReceipt, ReturnOverLimit, NoSaleDrawerOpen, FailedSupervisorPin, SettingsChange, HeldBillDelete, ShiftVariance, ShiftCount,
    ReturnOldReceipt, UploadModeChange, UploadIncludeHistory }

public sealed record Cashier(string Id, string Name, string? User, string PinHash, bool IsSupervisor, bool Enabled);

public sealed record ApprovalRecord(
    string Id,
    ApprovalAction Action,
    string CashierId,
    string SupervisorId,
    string ShiftClientId,
    string? ReceiptClientId,
    string? ItemCode,
    decimal Amount,
    string? Reason,
    DateTimeOffset At);

/// <summary>Salted PBKDF2-SHA256 PIN hashes: "pbkdf2-sha256$iterations$salt$hash" (Base64). PINs are 4–6 digits.</summary>
public static class PinHasher
{
    private const int Iterations = 10_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool IsValidPin(string pin) => pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        if (!IsValidPin(pin)) throw new ArgumentException("A PIN is 4 to 6 digits.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations is < 1 or > 1_000_000)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length == 0 || expected.Length != HashBytes) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>PIN login against the synced cashier list. A PIN shared by two enabled cashiers logs nobody in.</summary>
public sealed class Authenticator(Func<IReadOnlyList<Cashier>> cashiers)
{
    public Cashier? Login(string pin)
    {
        if (!PinHasher.IsValidPin(pin)) return null;
        var matches = cashiers().Where(c => c.Enabled && PinHasher.Verify(pin, c.PinHash)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public Cashier? Supervisor(string pin) => Login(pin) is { IsSupervisor: true } supervisor ? supervisor : null;
}
