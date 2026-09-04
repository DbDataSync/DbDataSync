using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.State;

/// <summary>Where one mapping's last completed pass got to, and when the source says that position
/// committed — see phase 87.</summary>
/// <param name="Watermark">
/// Null for a mapping that has an intent stored but has not yet completed a pass under it — phase 100's
/// <c>ChangesFromEarliest</c>, say, before its first read. Every existing caller already treats "no
/// applied position" as "nothing to measure from" (see <c>ReaderLagService</c>), which is exactly the
/// right reading here too.
/// </param>
/// <param name="WatermarkTimeUtc">
/// Null for three distinguishable-in-cause but identical-in-answer reasons: the row was written
/// before phase 87 added the column and the mapping has not run since, its reader has no
/// position-to-time mapping at all, or the engine declined to place the position on the pass that
/// stored it. All three mean the same thing to a report — no figure yet — which is why they are one
/// null rather than a state code.
/// </param>
public sealed record AppliedPosition(string? Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>
/// A mapping's full stored row — what it should do next, whether it is allowed to, and where it got to —
/// read together for the reason <see cref="AppliedPosition"/> already pairs a position with its time:
/// three separate reads of the same row could straddle a pass that ran between them. See phase 100.
/// </summary>
/// <param name="Intent">This mapping's own stored intent. There is no fallback here — a mapping with no
/// row at all (nothing in <see cref="ChangeWatermarkStore.GetReadState"/>) is the case
/// <c>ReadIntentResolution.Default</c> answers instead; once a row exists, its own value always wins.</param>
public sealed record MappingReadState(ReadIntent Intent, ReadHold Hold, string? Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>
/// Where each table mapping's incremental read got to — one current value per
/// <c>(TaskName, MappingName, SourceTable)</c>.
/// <para>
/// **The mapping is part of the key, not the table alone.** A replication may have any number of table
/// mappings pointing at the same physical source table, and nothing requires them to read it the same
/// way. Two that disagree — one on Change Tracking and one on the generic Watermark reader, or two
/// generic ones with different <c>watermarkColumn</c> options — were previously one row, each pass
/// overwriting a position the other could not interpret. The second case is the worse of the two,
/// because nothing fails: it just tracks the wrong place. A mapping's own identity settles it without
/// anyone having to reason per-reader about which options change watermark semantics. See phase 74.
/// </para>
/// <para>
/// The table stays in the key beside it. It is not needed to disambiguate mappings, but it is what
/// makes a mapping repointed at a different source table start from the beginning rather than resume
/// from a position belonging to a table it no longer reads.
/// </para>
/// </summary>
public sealed class ChangeWatermarkStore(StateDatabase database)
{
    /// <param name="watermarkTimeUtc">
    /// When the source says <paramref name="watermark"/> committed, where the reader that produced it
    /// could say — see phase 87. **In this call rather than a call of its own**, because a position
    /// and its commit time are one fact: written separately they could end up describing two
    /// different passes, and a lag figure computed from a mismatched pair is confidently wrong rather
    /// than merely absent. Overwritten on every pass including a null one, so a reader that stops
    /// being able to state a time clears the stale one instead of leaving it to be read as current.
    /// </param>
    public void SetWatermark(
        string taskName,
        string mappingName,
        string sourceTable,
        string watermark,
        DateTimeOffset? watermarkTimeUtc = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, Watermark, UpdatedAtUtc, WatermarkTimeUtc",
                "$task, $mapping, $table, $watermark, $now, $watermarkTime",
                "TaskName, MappingName, SourceTable",
                "Watermark = EXCLUDED.Watermark, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc, "
                    + "WatermarkTimeUtc = EXCLUDED.WatermarkTimeUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "watermark", watermark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "watermarkTime", (object?)watermarkTimeUtc?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// The stored position and the source's own time for it, together — what a lag report reads, and
    /// the reason it needs no connection to the source at all (phase 87).
    /// <para>
    /// One row read rather than <see cref="GetWatermark"/> plus a second lookup, for the reason the
    /// two are written together: a pair fetched in two queries can straddle a pass that ran between
    /// them, and the mismatched result would be a lag figure that is wrong rather than missing.
    /// </para>
    /// </summary>
    /// <returns>Null when the mapping has never completed a pass. A row with a null
    /// <c>WatermarkTimeUtc</c> is a different answer: it has a position, and nothing has yet been able
    /// to say when that position committed.</returns>
    public AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT Watermark, WatermarkTimeUtc FROM ChangeWatermarks "
                    + "WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);

            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new AppliedPosition(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)))
                : null;
        });

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT Watermark FROM ChangeWatermarks WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            return cmd.ExecuteScalar() as string;
        });

    /// <summary>
    /// This mapping's stored intent, hold and position, together — see <see cref="MappingReadState"/>
    /// for why one read rather than three.
    /// </summary>
    /// <returns>Null when the mapping has no row at all: it has never completed a pass and nobody has
    /// ever set an intent or a hold on it ahead of one. Resolving what that absence means is
    /// <c>ReadIntentResolution.Default</c>'s job, not this store's — a store that invented a default
    /// intent for a missing row would have nowhere to record "this default came from config" if a later
    /// caller needed to know.</returns>
    public MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT ReadIntent, ReadHold, Watermark, WatermarkTimeUtc FROM ChangeWatermarks "
                    + "WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);

            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new MappingReadState(
                    Enum.Parse<ReadIntent>(reader.GetString(0)),
                    Enum.Parse<ReadHold>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)))
                : null;
        });

    /// <summary>
    /// What the next pass over this mapping is meant to do — set on its own, independent of
    /// <see cref="SetReadHold"/> and of the watermark itself, so a caller that only knows one of these
    /// facts never has to restate the other two to change it.
    /// <para>
    /// Upserts rather than requiring an existing row: an intent can be set ahead of a mapping's first
    /// pass (<c>ChangesFromEarliest</c> on a brand-new mapping is exactly this), and the row that
    /// creates leaves <c>Watermark</c> null and <c>ReadHold</c> at its schema default — neither of
    /// which this call has any business deciding.
    /// </para>
    /// </summary>
    public void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, ReadIntent, UpdatedAtUtc",
                "$task, $mapping, $table, $intent, $now",
                "TaskName, MappingName, SourceTable",
                "ReadIntent = EXCLUDED.ReadIntent, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "intent", intent.ToString());
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>Why this mapping's next <c>Primary</c> pass is not going to run, set independently of
    /// <see cref="SetReadIntent"/> for the reason <see cref="ReadHold"/>'s own doc gives: a hold must
    /// preserve whatever intent sits underneath it. See that method for the upsert shape.</summary>
    public void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, ReadHold, UpdatedAtUtc",
                "$task, $mapping, $table, $hold, $now",
                "TaskName, MappingName, SourceTable",
                "ReadHold = EXCLUDED.ReadHold, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "hold", hold.ToString());
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Both in the one statement, for the operator recovering from a hold: setting a fresh intent and
    /// clearing the hold are one act, and writing them as two separate calls would leave a window where
    /// the hold has already cleared and the old intent is still what a pass would honour. See phase 100.
    /// </summary>
    public void SetReadIntentAndHold(
        string taskName, string mappingName, string sourceTable, ReadIntent intent, ReadHold hold) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, ReadIntent, ReadHold, UpdatedAtUtc",
                "$task, $mapping, $table, $intent, $hold, $now",
                "TaskName, MappingName, SourceTable",
                "ReadIntent = EXCLUDED.ReadIntent, ReadHold = EXCLUDED.ReadHold, "
                    + "UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "intent", intent.ToString());
            cmd.Bind(database, "hold", hold.ToString());
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Forgets where a table got to, so the next pass reads it from the beginning.
    /// <para>
    /// The recovery for a position the source no longer retains — see
    /// <c>PositionExpiredException</c>. Deleting the row rather than writing a sentinel, because every
    /// reader already treats "no watermark" as "read everything and start tracking from here", and a
    /// sentinel would be a second thing meaning the same.
    /// </para>
    /// </summary>
    /// <returns>False when there was nothing stored, so a repeat reads as "already cleared".</returns>
    public bool ClearWatermark(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "DELETE FROM ChangeWatermarks WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $source;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "source", sourceTable);
            return cmd.ExecuteNonQuery() == 1;
        });
}
