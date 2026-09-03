namespace DbDataSync.Core.Sql;

/// <summary>
/// The intermediate a column's native type is translated through so that supporting N engines costs
/// 2N translations (native → canonical, canonical → native) rather than N² (every engine pair
/// directly). See <see cref="SqlDialect.ToCanonicalType"/> and
/// <see cref="SqlDialect.RenderColumnType"/>.
/// <para>
/// In <c>DbDataSync.Core</c> alongside <see cref="SqlDialect"/> since phase 63. It used to live in
/// <c>DbDataSync.Drivers.Abstractions</c>, which was the lowest project both the dialects and
/// <c>IProvisioner</c> could see; once the dialects moved down to <c>Core</c> so that
/// <c>DbDataSync.State</c> could reach them, leaving this behind would have meant a type in <c>Core</c>
/// referencing one in a project above it — exactly the layering the move exists to avoid.
/// </para>
/// </summary>
public enum CanonicalTypeKind
{
    Boolean,
    Int8,
    Int16,
    Int32,
    Int64,
    Decimal,
    Float,
    Double,
    String,
    Binary,
    Date,
    Time,
    Timestamp,
    TimestampTz,
    Guid,
    Json,
    Xml,

    /// <summary>No canonical translation exists — <c>sql_variant</c>, <c>hierarchyid</c>,
    /// <c>geography</c>, an array type, an enum. A provisioning plan that hits this reports
    /// <c>Unsupported</c> naming the column and its native type; nothing ever guesses a rendering
    /// for it.</summary>
    Unmappable,
}

/// <param name="Length">Character/byte length for <see cref="CanonicalTypeKind.String"/> and
/// <see cref="CanonicalTypeKind.Binary"/>; null when <paramref name="IsMax"/> is true or the kind
/// carries no length.</param>
/// <param name="Precision">Total digits, for <see cref="CanonicalTypeKind.Decimal"/>.</param>
/// <param name="Scale">Digits after the point for <see cref="CanonicalTypeKind.Decimal"/>, or
/// sub-second fractional digits for <see cref="CanonicalTypeKind.Time"/>/<see cref="CanonicalTypeKind.Timestamp"/>/
/// <see cref="CanonicalTypeKind.TimestampTz"/>.</param>
/// <param name="IsUnicode">Only meaningful for <see cref="CanonicalTypeKind.String"/>: SQL Server
/// distinguishes <c>varchar</c> from <c>nvarchar</c>; an engine with one string type (Postgres) always
/// reports true so a round trip through it never narrows.</param>
/// <param name="IsMax">The type has no length bound (<c>nvarchar(max)</c>, <c>varbinary(max)</c>,
/// Postgres <c>text</c>/<c>bytea</c>).</param>
/// <param name="SourceNote">A caveat already known at the moment the *native* type was collapsed into
/// this canonical shape — set by <c>ToCanonicalType</c>, not by the renderer. SQL Server's
/// <c>money</c> is the motivating case: it maps to <see cref="CanonicalTypeKind.Decimal"/> like any
/// other fixed-point type, but the collapse itself is where its currency semantics are lost, before
/// any target has been chosen. <see cref="RenderedColumnType.Fidelity"/> folds this in with whatever
/// the render side finds, so a caller only ever has one warning to read rather than two places to
/// check.</param>
/// <param name="IsFixed">Only meaningful for <see cref="CanonicalTypeKind.Binary"/>: the source column
/// is a fixed-length <c>binary(n)</c> (SQL Server's <c>rowversion</c>/<c>timestamp</c> included — it is
/// always exactly 8 bytes) rather than a variable-length <c>varbinary(n)</c>. Only SQL Server has a
/// real fixed-length binary type distinct from its variable-length one, so this is set by
/// <c>MsSqlDialect.ToCanonicalType</c> and read by <c>MsSqlDialect.RenderColumnType</c> alone — every
/// other dialect ignores it and keeps rendering its one binary type, exactly as before this existed.</param>
public sealed record CanonicalType(
    CanonicalTypeKind Kind,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsUnicode,
    bool IsMax,
    string? SourceNote = null,
    bool IsFixed = false);

/// <param name="Sql">The DDL-ready type spec for the target dialect, e.g. <c>"varchar(50)"</c>,
/// <c>"numeric(18,2)"</c>. Never populated for <see cref="CanonicalTypeKind.Unmappable"/> — a caller
/// must check <see cref="CanonicalType.Kind"/> before rendering, the same way a provisioning plan
/// checks it before ever calling <c>RenderColumnType</c>.</param>
/// <param name="Fidelity">Null when the mapping is faithful. Otherwise a sentence naming exactly what
/// is lost — this is the output that stops a silent truncation, so it is a return value, not a doc
/// comment.</param>
public sealed record RenderedColumnType(string Sql, string? Fidelity);
