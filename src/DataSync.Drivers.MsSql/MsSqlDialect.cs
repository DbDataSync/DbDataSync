using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

/// <summary>SQL Server's answers to the mechanical variations in <see cref="SqlDialect"/>: bracket
/// quoting and <c>@</c>-prefixed parameters, both in statement text and when binding.</summary>
public sealed class MsSqlDialect : SqlDialect
{
    public static MsSqlDialect Instance { get; } = new();

    private MsSqlDialect() { }

    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public override string ParameterReference(string name) => $"@{name}";
}
