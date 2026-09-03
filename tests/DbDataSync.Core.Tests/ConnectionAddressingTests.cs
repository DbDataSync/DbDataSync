using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A connection addresses its engine one way or the other, and a credential never reaches the file that
/// gets committed.
/// </summary>
public sealed class ConnectionAddressingTests
{
    private static void Validate(string? host, string? connectionString) =>
        ConfigValidation.ValidateAddressing(host, connectionString, "prod-src");

    [Fact]
    public void AHost_IsEnough() => Validate("sql01", null);

    [Fact]
    public void AConnectionString_IsEnough() =>
        Validate(null, "Server=sql01;Failover Partner=sql02;Initial Catalog=App");

    [Fact]
    public void Neither_IsRejected()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Validate(null, null));
        Assert.Contains("either a host or a connection string", ex.Message);
    }

    [Fact]
    public void Both_IsRejectedBecauseNothingDecidesWhichWins()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Validate("sql01", "Server=sql02"));
        Assert.Contains("both", ex.Message);
    }

    [Fact]
    public void WhitespaceIsNotAnAddress() =>
        Assert.Throws<ConfigValidationException>(() => Validate("   ", "  "));

    [Theory]
    [InlineData("Server=sql01;Password=hunter2")]
    [InlineData("Server=sql01;PWD=hunter2")]
    [InlineData("Server=sql01;pass_word=hunter2")]
    [InlineData("Server=sql01; Password = hunter2 ")]
    [InlineData("DSN=legacy;UID=svc;PWD=hunter2")]
    [InlineData("Server=sql01;Secret=hunter2")]
    public void ACredentialInTheString_IsRejected(string connectionString)
    {
        // Config is git-committed and diffed in the UI, so a password here would be committed, pushed
        // and visible in the Version Control tab forever — which is the whole reason
        // CredentialSecretRef exists.
        var ex = Assert.Throws<ConfigValidationException>(() => Validate(null, connectionString));
        Assert.Contains("secret store", ex.Message);
    }

    [Theory]
    // A value that mentions a password is not a password.
    [InlineData("Server=sql01;Initial Catalog=PasswordVault")]
    // Carries no secret, despite the name.
    [InlineData("Server=sql01;Persist Security Info=True")]
    // A JDBC URL, which has no key/value segments at all before its query string.
    [InlineData("jdbc:postgresql://db01:5432/app?ssl=true")]
    [InlineData("Driver={ODBC Driver 18 for SQL Server};Server=sql01;Encrypt=yes")]
    public void SomethingThatMerelyLooksLikeOne_IsNot(string connectionString) => Validate(null, connectionString);

    [Fact]
    public void TheRejectionNamesTheKeyItFound()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Validate(null, "Server=sql01;PWD=x"));
        Assert.Contains("PWD", ex.Message);
    }
}
