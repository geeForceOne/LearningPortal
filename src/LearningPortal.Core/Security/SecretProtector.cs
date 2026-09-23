using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LearningPortal.Core.Security;

public enum SecretStatus
{
    NotSet,
    Set,
    // Stored, but can no longer be decrypted (e.g. the Data Protection key ring was lost).
    Unreadable,
}

public readonly record struct SecretReadResult(SecretStatus Status, string? Value);

// Encrypts user API keys at rest. A value that fails to decrypt is reported as Unreadable,
// never thrown and never silently dropped, so Settings can ask the user to re-enter it.
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("LearningPortal.UserApiKeys.v1");

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
