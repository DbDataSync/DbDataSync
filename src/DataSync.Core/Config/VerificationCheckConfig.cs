namespace DataSync.Core.Config;

/// <summary>How a check's two queries are produced.</summary>
public enum VerificationCheckKind
{
    /// <summary>Rows, optionally grouped. The cheapest check and the one that catches the most.</summary>
    RowCount,

    /// <summary>The sum of one or more measures, optionally grouped. Catches what a row count cannot:
    /// the right number of rows carrying the wrong values.</summary>
    Sum,

    /// <summary>SQL an operator wrote. One statement for both sides when the engines are close enough,
    /// or one per dialect when they are not.</summary>
    Sql,

    /// <summary>A bound script that generates the statements — for a check that has to look at the
    /// source's own metadata before it can say what to ask.</summary>
    Script,
}

/// <summary>
/// One comparison between a mapping's source and its target.
/// <para>
/// Lives on the table mapping because that is what it compares; the SQL and the C# behind a
/// <see cref="VerificationCheckKind.Sql"/> or <see cref="VerificationCheckKind.Script"/> check live in
/// the script registry, which is what makes them reusable across mappings while the mapping supplies
/// the tables and the parameters.
/// </para>
/// </summary>
public sealed class VerificationCheckConfig
{
    public required string Name { get; set; }

    public VerificationCheckKind Kind { get; set; } = VerificationCheckKind.RowCount;

    /// <summary>
    /// Which result columns identify a row, so the two sides can be lined up.
    /// <para>
    /// Named by their **target** column names, and translated to the source's through the mapping —
    /// which is what makes an aliased column (<c>cust_id</c> on one side, <c>CustomerId</c> on the
    /// other) one selection rather than two things to keep in step. For a built-in check these also
    /// drive what is generated; for a hand-written one they say what the statement already returns.
    /// </para>
    /// </summary>
    public List<string> GroupBy { get; set; } = new();

    /// <summary>The numbers being compared. Same naming rule as <see cref="GroupBy"/>.</summary>
    public List<string> Measures { get; set; } = new();

    /// <summary>
    /// The statement, for <see cref="VerificationCheckKind.Sql"/>. Run against **both** sides when
    /// <see cref="TargetSql"/> is null — generic SQL, which is the operator's judgement that the two
    /// engines are close enough here, not something inferred for them.
    /// </summary>
    public string? SourceSql { get; set; }

    /// <summary>The target's own statement, when generic SQL will not do.</summary>
    public string? TargetSql { get; set; }

    /// <summary>The script that generates the statements, for <see cref="VerificationCheckKind.Script"/>.</summary>
    public string? ScriptName { get; set; }

    /// <summary>What the binding supplies for the script's declared parameters (phase 42).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// Narrows both sides, in the same shape as <c>SourceTableSpec.Filter</c> — an operator limiting a
    /// check to a quiet window uses a pattern they already know rather than a second filter mechanism.
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// How far apart two measures may be before the difference is worth pointing at, as a fraction of
    /// the larger side. Zero means any difference at all.
    /// <para>
    /// A threshold rather than "anything nonzero" because the premise of the whole feature is that a
    /// replication is behind by design: a check run mid-pass will differ, and a screen that paints
    /// that red teaches an operator to ignore red.
    /// </para>
    /// </summary>
    public double DifferenceThreshold { get; set; }
}
