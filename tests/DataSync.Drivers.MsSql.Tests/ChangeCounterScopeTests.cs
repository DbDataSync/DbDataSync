using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// That a database-wide change counter is an answer about one database, and that asking for it means
/// putting the connection into that database first — see phase 75.
/// <para>
/// The scheduler's polling gate is the second caller of this statement, after the reader, and it
/// exists to predict what that reader will find. The reader opens with
/// <c>ChangeDatabase(source.Database)</c>; a gate that skipped that would ask the connection's
/// default catalog instead and compare its answer against a watermark measured somewhere else. The
/// failure would be silent and one-directional — a quiet default catalog suppressing a mapping that
/// has work — so it is worth one test that the two contexts really do answer differently.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChangeCounterScopeTests(MsSqlTestDatabase database) : IClassFixture<MsSqlTestDatabase>
{
    [Fact]
    public async Task TheChangeTrackingVersion_IsAnAnswerAboutTheCurrentDatabase()
    {
        var table = $"Scoped_{Guid.NewGuid():N}";

        await using (var setup = database.OpenConnection())
        {
            await ExecuteAsync(setup, $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50));");
            await ExecuteAsync(setup, $"ALTER TABLE dbo.[{table}] ENABLE CHANGE_TRACKING;");
            await ExecuteAsync(setup, $"INSERT INTO dbo.[{table}] (Id, Name) VALUES (1, 'one'), (2, 'two');");
        }

        // Opened without an initial catalog, so this lands in master — where Change Tracking is not
        // enabled and CHANGE_TRACKING_CURRENT_VERSION() has nothing to report. Zero, not an error,
        // which is exactly what makes reading the wrong database dangerous rather than loud.
        await using var connection = new SqlConnection(MsSqlTestDatabase.ServerConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0, await MsSqlChangeTrackingReader.GetCurrentVersionAsync(connection, CancellationToken.None));

        connection.ChangeDatabase(database.DatabaseName);

        // The same connection, the same statement, a different database, a version that has moved
        // because rows were written — which is the whole reason the gate switches database before it
        // asks, and the difference a gate that did not would never see.
        Assert.True(await MsSqlChangeTrackingReader.GetCurrentVersionAsync(connection, CancellationToken.None) > 0);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
