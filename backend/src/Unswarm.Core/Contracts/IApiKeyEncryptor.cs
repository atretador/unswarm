namespace Unswarm.Core.Contracts;

/// <summary>
/// Encrypt/decrypt secrets at rest. Implemented in the Api layer
/// using ASP.NET DataProtection (machine-scoped, persisted key ring).
/// </summary>
public interface IApiKeyEncryptor
{
    /// <summary>Encrypt a plaintext secret.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypt a previously-encrypted blob. Throws on decryption failure.</summary>
    string Unprotect(string ciphertext);
}
