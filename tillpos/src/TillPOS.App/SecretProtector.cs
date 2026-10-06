using System.Security.Cryptography;
using System.Text;

namespace TillPOS.App;

/// <summary>DPAPI (machine scope): the API secret in settings.json can only be decrypted on this PC.</summary>
public static class SecretProtector
{
    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine));

    public static string Unprotect(string protectedSecret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.LocalMachine));
}
