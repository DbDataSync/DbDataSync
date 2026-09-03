namespace DbDataSync.Core.Config;

/// <summary>How a segmenting strategy produces its candidates — see phase 58.</summary>
public enum SegmentingStrategyKind
{
    /// <summary>
    /// SQL against an ephemeral, in-memory DuckDB instance that is connected to nothing.
    /// <para>
    /// The default, and the one worth reaching for first. An admin who already knows a table's write
    /// pattern can compute the whole segment list from the calendar with <c>generate_series</c> and
    /// date arithmetic, touching neither database. That makes it free to preview, free to re-run on a
    /// schedule, and impossible to get wrong in a way that costs the source anything.
    /// </para>
    /// </summary>
    DuckDb,

    /// <summary>SQL in the source's own dialect, run against the source connection — segmenting
    /// derived from what is actually in the table.</summary>
    SourceSql,

    /// <summary>
    /// The same, against the target.
    /// <para>
    /// Worth offering because operators often already maintain the answer over there: a control table
    /// recording which periods need reloading, or a rollup cheaper to query than the live source.
    /// </para>
    /// </summary>
    TargetSql,

    /// <summary>A bound script implementing <c>ISegmentingStrategy</c>. Strictly more powerful than
    /// either SQL path — it is handed both connections and can use either, or neither.</summary>
    Script,
}

/// <summary>
/// A named, reusable way of dividing a table for reload, referenced from a mapping's default
/// segmenting or picked ad hoc at backfill time.
/// <para>
/// Shaped like <see cref="VerificationCheckConfig"/> deliberately: this is the same problem phase 43
/// already solved — "how does an operator author one of these" — and an operator who has written a
/// verification check should recognise every field here.
/// </para>
/// <para>
/// **All four kinds return the same four things per row**: a label, a start, an end, and whether the
/// candidate is selected by default. Bounds are half-open — start inclusive, end exclusive — which is
/// the convention <see cref="RangeSegment"/> already uses everywhere else, so a strategy's output
/// tiles a value space without gaps or overlaps the same way an <c>Auto</c> expansion does.
/// </para>
/// </summary>
public sealed class SegmentingStrategyConfig
{
    public required string Name { get; set; }

    public SegmentingStrategyKind Kind { get; set; } = SegmentingStrategyKind.DuckDb;

    /// <summary>
    /// The query, for <see cref="SegmentingStrategyKind.DuckDb"/>,
    /// <see cref="SegmentingStrategyKind.SourceSql"/> and <see cref="SegmentingStrategyKind.TargetSql"/>.
    /// <para>
    /// Must return the four columns named in <see cref="SegmentingStrategyColumns"/>. <c>selected</c>
    /// may be omitted, in which case nothing is pre-selected — a strategy that does not say which
    /// candidates matter is proposing, not deciding, and unattended it does nothing rather than
    /// everything.
    /// </para>
    /// </summary>
    public string? Sql { get; set; }

    /// <summary>The script implementing <c>ISegmentingStrategy</c>, for
    /// <see cref="SegmentingStrategyKind.Script"/>.</summary>
    public string? ScriptName { get; set; }

    /// <summary>
    /// Which column the produced ranges are over.
    /// <para>
    /// Required for every kind, and not inferable from the query: the SQL returns bounds, not the
    /// thing they bound. A DuckDB strategy generating month boundaries has no idea which of the
    /// source's date columns it is generating them for — only the operator does.
    /// </para>
    /// </summary>
    public string? Column { get; set; }

    /// <summary>What the binding supplies for a script's declared parameters (phase 42).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// Whether running this strategy touches a real database connection.
    /// <para>
    /// The one thing the UI has to say out loud. A strategy bound as a mapping's *default* runs
    /// unattended on the replication's own schedule — every pass, forever — and whether that is
    /// acceptable is the operator's judgement about their own tables, not DbDataSync's to make for them.
    /// What DbDataSync owes is that the consequence is visible before they choose it, which is the same
    /// bargain phase 41 struck for live script testing.
    /// </para>
    /// </summary>
    public bool RunsAgainstAConnection => Kind is not SegmentingStrategyKind.DuckDb;
}

/// <summary>The four columns every strategy's query returns, named in one place so the runner, the
/// validator and the UI's help text cannot describe different contracts.</summary>
public static class SegmentingStrategyColumns
{
    public const string Label = "label";
    public const string RangeStart = "range_start";
    public const string RangeEnd = "range_end";
    public const string Selected = "selected";

    public static IReadOnlyList<string> Required { get; } = [Label, RangeStart, RangeEnd];
}
