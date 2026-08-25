namespace DataSync.Drivers.MsSql;

/// <summary>Bracket-quotes a SQL Server identifier. Table/schema/column names originate from
/// introspected metadata or validated config, never raw end-user input (see
/// architecture/detailed-design.md §6) — this guards against the identifier containing a `]`.</summary>
internal static class SqlIdentifier
{
    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
