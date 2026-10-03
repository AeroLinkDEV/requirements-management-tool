using System.Security.Cryptography;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class PasswordHashPolicyTests
{
    [Fact]
    public void New_password_hash_meets_selected_cost_and_uses_an_independent_random_salt()
    {
        const string password = "FixturePassword!2026";
        var first = IdentityService.HashPassword(password).Split('$');
        var second = IdentityService.HashPassword(password).Split('$');

        Assert.Equal("pbkdf2-sha256", first[0]);
        Assert.Equal("600000", first[1]);
        var salt = Convert.FromBase64String(first[2]);
        var hash = Convert.FromBase64String(first[3]);
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(first[2], second[2]);

        // The policy is fixed independently of the encoded cost and the production verifier.
        var expected = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        Assert.Equal(expected, hash);
        Assert.True(IdentityService.VerifyPassword(password, string.Join('$', first)));
        Assert.False(IdentityService.VerifyPassword("WrongFixturePassword!2026", string.Join('$', first)));
    }
}
