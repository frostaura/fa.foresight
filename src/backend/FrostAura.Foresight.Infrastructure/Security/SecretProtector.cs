using Microsoft.AspNetCore.DataProtection;

namespace FrostAura.Foresight.Infrastructure.Security;

/// <summary>
/// Symmetric protect/unprotect for platform-connection secrets (wallet private key, API secret).
/// Backed by ASP.NET Core Data Protection with DB-persisted keys, so ciphertext written on one boot
/// is readable after a restart. Plaintext secrets are never logged and never persisted.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypt <paramref name="plaintext"/>. Null/empty in ⇒ null out (nothing to store).</summary>
    string? Protect(string? plaintext);

    /// <summary>Decrypt <paramref name="ciphertext"/>. Null/empty in ⇒ null out (no secret configured).</summary>
    string? Unprotect(string? ciphertext);
}

public sealed class SecretProtector : ISecretProtector
{
    /// <summary>Purpose string scopes the protector — secrets are only decryptable with the same purpose.</summary>
    public const string Purpose = "platform-connection-secret";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
        => string.IsNullOrEmpty(ciphertext) ? null : _protector.Unprotect(ciphertext);
}
