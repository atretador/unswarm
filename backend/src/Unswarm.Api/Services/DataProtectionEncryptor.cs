using Microsoft.AspNetCore.DataProtection;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Services;

/// <summary>
/// DataProtection-backed implementation of <see cref="IApiKeyEncryptor"/>.
/// Machine-scoped with a persisted key ring for stability across restarts.
/// </summary>
public sealed class DataProtectionEncryptor : IApiKeyEncryptor
{
    private const string Purpose = "Unswarm.CloudProviderApiKey";
    private readonly IDataProtector _protector;

    public DataProtectionEncryptor(IDataProtectionProvider dp)
    {
        _protector = dp.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
