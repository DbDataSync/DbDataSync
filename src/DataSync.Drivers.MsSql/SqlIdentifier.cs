namespace DataSync.Drivers.MsSql;

/// <summary>Bracket-quotes a SQL Server identifier. A shorthand for
/// <see cref="MsSqlDialect.QuoteIdentifier"/> — which is the single definition — kept because this
/// driver quotes identifiers inside interpolated statement text on nearly every line.</summary>
internal static class SqlIdentifier
{
    public static string Quote(string identifier) => MsSqlDialect.Instance.QuoteIdentifier(identifier);
}
