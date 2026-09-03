namespace DbDataSync.Benchmarks;

public enum ColumnKind { Int, Text, Money, Timestamp }

/// <summary>
/// A synthetic wide table for the change-representation benchmark. Column types cycle so the mix
/// resembles a real replicated table rather than one uniform type — the cost of a representation
/// depends heavily on how many of its values are boxable structs.
/// <para>
/// Every variant writes byte-identical data, and the string is a shared instance: string allocation
/// would otherwise be the largest term and is the same whichever representation holds it, so sharing
/// it keeps the comparison about the representation.
/// </para>
/// </summary>
public static class BenchmarkSchema
{
    public const string SharedText = "Customer name value";

    public static ColumnKind KindOf(int column) => (column % 5) switch
    {
        0 => ColumnKind.Int,
        3 => ColumnKind.Money,
        4 => ColumnKind.Timestamp,
        _ => ColumnKind.Text,
    };

    public static string NameOf(int column) => $"Col{column}";

    public static Type ClrTypeOf(int column) => KindOf(column) switch
    {
        ColumnKind.Int => typeof(int),
        ColumnKind.Money => typeof(decimal),
        ColumnKind.Timestamp => typeof(DateTime),
        _ => typeof(string),
    };

    private static string SqlTypeOf(int column) => KindOf(column) switch
    {
        ColumnKind.Int => "INT",
        ColumnKind.Money => "DECIMAL(18,2)",
        ColumnKind.Timestamp => "DATETIME2(3)",
        _ => "NVARCHAR(50)",
    };

    public static int IntValue(int row, int column) => row + column;
    public static decimal MoneyValue(int row, int column) => (row % 1000) + (column * 0.01m);
    public static DateTime TimestampValue(int row) => DateTime.UnixEpoch.AddSeconds(row);

    public static string CreateTableSql(string table, int columns)
    {
        var definitions = string.Join(",\n    ",
            Enumerable.Range(0, columns).Select(c => $"[{NameOf(c)}] {SqlTypeOf(c)} NULL"));
        return $"CREATE TABLE dbo.[{table}] (\n    {definitions}\n);";
    }
}
