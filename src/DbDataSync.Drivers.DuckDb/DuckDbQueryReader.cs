using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.DuckDb;

/// <summary>
/// DuckDB's own query-as-source reader — now a thin, unmodified-behavior subclass of the engine-neutral
/// <see cref="RawQueryReader"/>, which generalized this reader's own design to every driver that can
/// name a <see cref="SqlDialect"/>. Kept as its own class, under its own <see cref="ReaderKind"/>
/// ("DuckDbQuery"), rather than folded into the shared registration every other driver gets under the
/// neutral "Query" Kind (see <c>RawQueryRegistration</c>) — for two reasons: existing mappings already
/// have "DuckDbQuery" saved on disk, and DuckDB already offering the same capability twice under two
/// names would be a confusing, redundant choice in its own reader-kind picker.
/// </summary>
public sealed class DuckDbQueryReader() : RawQueryReader(ReaderKind, DuckDbDialect.Instance)
{
    public const string ReaderKind = "DuckDbQuery";
}
