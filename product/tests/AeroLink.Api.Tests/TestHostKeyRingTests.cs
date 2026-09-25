using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// #1130: every API test host used the runner user's shared DataProtection-Keys folder, and parallel classes on a
// fresh runner raced to create its first key; one host's CSRF read then failed with a 500. Test hosts now keep their
// key rings in memory. A payload one host protects is therefore unreadable by another: with a shared folder both
// hosts hold the same key and this test fails.
public sealed class TestHostKeyRingTests
{
    [Fact]
    public void Two_test_hosts_do_not_share_a_data_protection_key_ring()
    {
        using var first = new AeroLinkApiFactory();
        using var second = new AeroLinkApiFactory();
        const string purpose = "AeroLink.Tests.KeyRingIsolation";
        var protectedByFirst = first.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose).Protect("csrf-shaped payload");

        Assert.Equal("csrf-shaped payload",
            first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(protectedByFirst));
        Assert.ThrowsAny<CryptographicException>(() =>
            second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(protectedByFirst));
    }
}
