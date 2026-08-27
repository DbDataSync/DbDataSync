using System.Data.Common;
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

    /// <summary>SQL Server caps a request at 2100 parameters.</summary>
    public override int MaxParametersPerStatement => 2100;

    /// <summary>The spellings the shared list does not carry. Everything else falls through to the
    /// base classification, so this is SQL Server's additions rather than a restatement.</summary>
    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "money" or "smallmoney" => BucketableKind.Numeric,
        "smalldatetime" or "datetime2" => BucketableKind.DateTime,
        "datetimeoffset" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>Explicit values for an IDENTITY column need the session flag, which SQL Server permits
    /// on one table at a time — so it is always turned back off, including when the write fails.</summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) =>
        MsSqlIdentityInsert.RunAsync(connection, transaction, qualifiedTable, overrideRequired, write, cancellationToken);
}
