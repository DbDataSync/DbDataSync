using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>SQL Server's dialect, restated here rather than referenced. These tests exist to pin the
/// generic layer's output against more than one dialect, and depending on the MSSQL driver to do it
/// would make a change there silently change what "generic" means.</summary>
internal sealed class BracketDialect : SqlDialect
{
    public static BracketDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";
    public override string ParameterReference(string name) => $"@{name}";
}

/// <summary>The other common shape: double-quoted identifiers and colon-prefixed placeholders whose
/// *bound* name drops the colon — Oracle's, and the reason
/// <see cref="SqlDialect.ParameterName"/> exists separately from
/// <see cref="SqlDialect.ParameterReference"/>.</summary>
internal sealed class ColonDialect : SqlDialect
{
    public static ColonDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    public override string ParameterReference(string name) => $":{name}";
    public override string ParameterName(string name) => name;
}

/// <summary>Records what it was asked to bind. Segment binding is provider-specific by design, so the
/// generic tests assert the names and raw values handed to the binder, not the resulting DbParameter's
/// provider type.</summary>
internal sealed class RecordingBinder : ISegmentValueBinder
{
    public List<(string Name, string Value, string Column)> Calls { get; } = [];

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        Calls.Add((name, rawValue, column.Name));
        return new FakeDbParameter { ParameterName = name, Value = rawValue };
    }
}

/// <summary>A DbParameter with no provider behind it — enough to satisfy the contract, since these
/// tests never execute anything.</summary>
internal sealed class FakeDbParameter : DbParameter
{
    public override System.Data.DbType DbType { get; set; }
    public override System.Data.ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}
