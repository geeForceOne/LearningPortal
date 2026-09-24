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

// Encrypts secrets at rest: user API keys by default, or another kind of secret under its own
// purpose. A value that fails to decrypt is reported as Unreadable, never thrown and never
// silently dropped, so the UI can ask for it to be entered again.
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider) : this(provider, "LearningPortal.UserApiKeys.v1") { }

    public SecretProtector(IDataProtectionProvider provider, string purpose) =>
        _protector = provider.CreateProtector(purpose);

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
