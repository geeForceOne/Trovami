using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Trovami.Core.Security;

public enum SecretStatus
{
    NotSet,
    Set,
    // Stored, but can no longer be decrypted (e.g. the Data Protection key ring was lost).
    Unreadable,
}

public readonly record struct SecretReadResult(SecretStatus Status, string? Value);

// Encrypts secrets at rest under one purpose. A value that fails to decrypt is reported as
// Unreadable, never thrown and never silently dropped, so the UI can ask for it again.
public sealed class SecretProtector(IDataProtectionProvider provider, string purpose)
{
    private readonly IDataProtector _protector = provider.CreateProtector(purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public SecretReadResult Read(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return new(SecretStatus.NotSet, null);

        try
        {
            return new(SecretStatus.Set, _protector.Unprotect(protectedValue));
        }
        catch (CryptographicException)
        {
            return new(SecretStatus.Unreadable, null);
        }
    }
}
