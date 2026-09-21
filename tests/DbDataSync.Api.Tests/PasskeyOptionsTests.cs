using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The relying-party id is where passkeys go wrong, and the failure is a browser API refusing a
/// ceremony with a message that names neither the id nor the origin. Checking it at startup is the
/// difference between a deployment that says what is wrong and one that just does not work.
/// </summary>
public sealed class PasskeyOptionsTests
{
    private static PasskeyOptions Options(string id, params string[] origins) => new()
    {
        RelyingPartyId = id,
        RelyingPartyName = "DbDataSync",
        Origins = origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void AMatchingHostAndOrigin_IsFine() =>
        Assert.Null(Options("dbdatasync.corp.example", "https://dbdatasync.corp.example").Problem());

    /// <summary>A subdomain is under the relying party, which is the whole point of the id being a
    /// domain rather than an origin.</summary>
    [Fact]
    public void ASubdomainOrigin_IsUnderTheRelyingParty() =>
        Assert.Null(Options("corp.example", "https://dbdatasync.corp.example").Problem());

    [Fact]
    public void AUrlAsTheId_IsRefusedWithTheReason() =>
        Assert.Contains("bare domain", Options("https://dbdatasync.corp.example").Problem());

    /// <summary>WebAuthn requires a domain name. A deployment reached only by IP cannot use passkeys at
    /// all, and it should be told that at startup rather than by a browser.</summary>
    [Fact]
    public void AnIpAddressAsTheId_IsRefusedWithTheReason() =>
        Assert.Contains("IP address", Options("10.0.0.5", "https://10.0.0.5").Problem());

    /// <summary>The mismatch that actually happens: a deployment moved from localhost to a hostname and
    /// only half the configuration followed.</summary>
    [Fact]
    public void AnOriginNotUnderTheRelyingParty_IsRefusedAndNamed()
    {
        var problem = Options("dbdatasync.corp.example", "https://dbdatasync.corp.example", "http://localhost:5080")
            .Problem();

        Assert.Contains("http://localhost:5080", problem);
        Assert.Contains("dbdatasync.corp.example", problem);
    }

    /// <summary>Defaults have to work for a freshly installed tool, which runs at localhost.</summary>
    [Fact]
    public void TheDefaults_AreConsistent()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var options = PasskeyOptions.FromConfiguration(configuration, ApiOptions.FromConfiguration(configuration));

        Assert.Equal("localhost", options.RelyingPartyId);
        Assert.Null(options.Problem());
    }
}
