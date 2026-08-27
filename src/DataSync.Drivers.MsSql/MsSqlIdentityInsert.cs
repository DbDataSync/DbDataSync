using System.Data.Common;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Brackets a write with <c>SET IDENTITY_INSERT ... ON/OFF</c> when the target's identity column is
/// among the columns being written. The setting is session-scoped and SQL Server permits it on only
/// one table at a time, so it's always turned back off — including when the write fails.
/// </summary>
internal static class MsSqlIdentityInsert
{
    public static async Task<T> RunAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string quotedTarget,
        bool requiresIdentityInsert,
        Func<Task<T>> write,
        CancellationToken cancellationToken)
    {
        if (!requiresIdentityInsert)
            return await write();

        await SetAsync(connection, transaction, quotedTarget, on: true, cancellationToken);
        try
        {
            return await write();
        }
        finally
        {
            // Best effort: if the connection is already broken there's nothing to reset, and letting
            // that failure replace the real one would hide why the write failed.
            try
            {
                await SetAsync(connection, transaction, quotedTarget, on: false, CancellationToken.None);
            }
            catch (DbException)
            {
            }
        }
    }

    private static async Task SetAsync(
        DbConnection connection, DbTransaction? transaction, string quotedTarget, bool on, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SET IDENTITY_INSERT {quotedTarget} {(on ? "ON" : "OFF")};";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
