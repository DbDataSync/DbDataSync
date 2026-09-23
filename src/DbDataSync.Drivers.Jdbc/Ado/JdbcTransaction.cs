using System.Data;
using System.Data.Common;

namespace DbDataSync.Drivers.Jdbc.Ado;

/// <summary>
/// Phase 172V. Thin — <c>java.sql.Connection</c> already carries every bit of the real transaction state
/// (<c>autoCommit</c>, the open/committed/rolled-back-ness of it); this exists only because ADO.NET wants
/// a <see cref="DbTransaction"/> object handed back from <see cref="DbConnection.BeginTransaction()"/> and
/// assigned to <see cref="DbCommand.Transaction"/>, not because there is real per-object state to hold
/// beyond "has this already been completed."
/// </summary>
internal sealed class JdbcTransaction(JdbcConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    private bool _completed;

    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => isolationLevel;

    public override void Commit()
    {
        connection.JavaSqlConnection.commit();
        Complete();
    }

    public override void Rollback()
    {
        connection.JavaSqlConnection.rollback();
        Complete();
    }

    /// <summary>
    /// ADO.NET convention: a transaction disposed without <see cref="Commit"/>/<see cref="Rollback"/>
    /// rolls back — every generic writer's own <c>await using var transaction = ...</c> relies on exactly
    /// this for the "threw partway through" case, the same way every other engine's writer already does.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
            Rollback();
        base.Dispose(disposing);
    }

    private void Complete()
    {
        if (_completed)
            return;
        _completed = true;
        connection.JavaSqlConnection.setAutoCommit(true);
    }
}
