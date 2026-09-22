using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// <see cref="KnownLibraries.TryGetByIdOrPackageId"/> — added after
/// architecture/planning/todo/follow-up-installed-catalog-libraries-stopped-showing-as-curated.md found
/// that the bare <see cref="KnownLibraries.TryGetById"/> only recognizes a library keyed by its catalog
/// id, silently stopped recognizing it the moment the id-divergence fix made a real install key by
/// package id instead. These pin both shapes so that regression can't come back through this method.
/// </summary>
public sealed class KnownLibrariesTests
{
    [Fact]
    public void ResolvesByCatalogId()
    {
        var entry = KnownLibraries.TryGetByIdOrPackageId("mysql-connector");

        Assert.NotNull(entry);
        Assert.Equal("MySqlConnector", entry.PackageId);
    }

    [Fact]
    public void ResolvesByPackageId()
    {
        var entry = KnownLibraries.TryGetByIdOrPackageId("MySqlConnector");

        Assert.NotNull(entry);
        Assert.Equal("mysql-connector", entry.Id);
    }

    [Fact]
    public void IsCaseInsensitiveInBothShapes()
    {
        Assert.NotNull(KnownLibraries.TryGetByIdOrPackageId("MYSQL-CONNECTOR"));
        Assert.NotNull(KnownLibraries.TryGetByIdOrPackageId("MYSQLCONNECTOR"));
    }

    [Fact]
    public void NullForAnIdThatIsNeitherShape()
    {
        Assert.Null(KnownLibraries.TryGetByIdOrPackageId("System.Data.SqlClient"));
    }
}
