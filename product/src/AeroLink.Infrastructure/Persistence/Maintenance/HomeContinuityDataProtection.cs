using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>
/// Reads old protected values during an attended HOME move while every new value is protected by the
/// destination provider.  The legacy application name is deliberately opt-in: a missing value means that
/// the operator has not captured the source discriminator, so the destination never guesses one.
/// </summary>
public sealed class HomeContinuityDataProtection : IDisposable
{
    private readonly IDataProtectionProvider? legacyProvider;

    private HomeContinuityDataProtection(IDataProtectionProvider currentProvider, IDataProtectionProvider? legacyProvider)
    {
        CurrentProvider = currentProvider ?? throw new ArgumentNullException(nameof(currentProvider));
        this.legacyProvider = legacyProvider;
    }

    public IDataProtectionProvider CurrentProvider { get; }

    public static HomeContinuityDataProtection Create(IDataProtectionProvider currentProvider, IConfiguration configuration)
    {
        var legacyName = configuration["DataProtection:LegacyApplicationName"]?.Trim();
        var ringPath = configuration["DataProtection:KeyRingPath"]?.Trim();
        if (string.IsNullOrWhiteSpace(legacyName))
            return new HomeContinuityDataProtection(currentProvider, null);
        if (string.IsNullOrWhiteSpace(ringPath))
            throw new InvalidOperationException(
                "DataProtection:LegacyApplicationName requires the captured DataProtection:KeyRingPath.");
        if (!Directory.Exists(ringPath))
            throw new InvalidOperationException(
                "DataProtection:LegacyApplicationName requires an existing destination key ring.");

        var legacy = DataProtectionProvider.Create(new DirectoryInfo(ringPath), builder =>
            builder.SetApplicationName(legacyName).DisableAutomaticKeyGeneration());
        return new HomeContinuityDataProtection(currentProvider, legacy);
    }

    public IDataProtector CreateProtector(params string[] purposes) =>
        new FallbackProtector(CurrentProvider.CreateProtector(purposes), legacyProvider?.CreateProtector(purposes));

    public void Dispose() => (legacyProvider as IDisposable)?.Dispose();

    private sealed class FallbackProtector(IDataProtector current, IDataProtector? legacy) : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => current.Protect(plaintext);

        public byte[] Unprotect(byte[] protectedData)
        {
            try { return current.Unprotect(protectedData); }
            catch (CryptographicException) when (legacy is not null)
            { return legacy.Unprotect(protectedData); }
        }

        public IDataProtector CreateProtector(string purpose) =>
            new FallbackProtector(current.CreateProtector(purpose), legacy?.CreateProtector(purpose));
    }
}
