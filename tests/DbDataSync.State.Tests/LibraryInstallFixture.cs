using DbDataSync.Libraries;

namespace DbDataSync.State.Tests;

/// <summary>
/// Restores <c>microsoft-data-sqlclient</c> and <c>npgsql</c> once, into a scratch repo root, and
/// exposes the <see cref="LibraryRegistry"/> that resolves them — for any test in this assembly that
/// needs a real MsSql- or Postgres-backed <see cref="StateDatabase"/> since phase 109g moved
/// <see cref="MsSqlStateDialect"/>/<see cref="PostgresStateDialect"/> off a direct package reference.
/// <para>
/// A real restore — a <c>dotnet publish</c> under the hood, per <see cref="LibraryInstaller"/> — is not
/// something to repeat per test method (or even per test class): <c>xunit</c>'s <c>IClassFixture&lt;T&gt;</c>
/// constructs this once per class, the same "install once, share the registry" shape
/// <c>DbDataSync.Api.Tests</c>' <c>DescriptorDriverApiFactory</c> already uses for MySqlConnector.
/// </para>
/// </summary>
public sealed class LibraryInstallFixture : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-state-libs-").FullName;

    public LibraryRegistry Registry { get; }

    public LibraryInstallFixture()
    {
        Install(MsSqlStateDialect.LibraryId);
        Install(PostgresStateDialect.LibraryId);

        Registry = new LibraryRegistry(_repoRoot).LoadAll();
    }

    private void Install(string libraryId)
    {
        var entry = KnownLibraries.TryGetById(libraryId)
            ?? throw new InvalidOperationException($"'{libraryId}' is not in the KnownLibraries catalog.");

        LibraryInstaller.InstallAsync(
                _repoRoot, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType)
            .GetAwaiter().GetResult();
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);
}
