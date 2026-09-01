using System.Data.Common;

namespace DataSync.State;

/// <summary>What a notification is about. Stored as text, not as an enum ordinal, so an unknown value
/// written by a newer build reads back as itself rather than as whichever kind happens to share its
/// number.</summary>
public static class NotificationKinds
{
    /// <summary>A run ended <see cref="RunStatus.Failed"/> — phase 77.</summary>
    public const string RunFailed = "RunFailed";

    /// <summary>Somebody held a replication — phase 80. Pauses only; a resume is not news.</summary>
    public const string ReplicationPaused = "ReplicationPaused";

    /// <summary>A run failed because the source discarded the history its position pointed into —
    /// phase 80. A run failure with a known fix, so it is its own kind rather than a
    /// <see cref="RunFailed"/> row whose message happens to say so.</summary>
    public const string PositionExpired = "PositionExpired";
}

/// <param name="Kind">One of <see cref="NotificationKinds"/> — but read as an open set: a row written
/// by a newer build carries a kind this one has never heard of, and rendering it as its message is
/// always better than dropping it.</param>
/// <param name="TaskName">What the notification is about, where that is a replication. Null for a
/// kind that is not about one.</param>
public sealed record NotificationRecord(
    long Id,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    string? TaskName,
    string? MappingName,
    Guid? RunId,
    string Message);

/// <param name="LastSeenNotificationId">Where this caller's cursor is, or null if it has never been
/// advanced — and also null for every caller when the deployment does not authenticate, which
/// <paramref name="Personalized"/> is what distinguishes.</param>
/// <param name="Personalized">
/// Whether the read state in this answer belongs to somebody. False when authentication is disabled
/// and there is no user id to key a cursor to: every notification reads as unread, permanently, for
/// everyone at that deployment. Said out loud in the payload because a badge that never clears is
/// otherwise indistinguishable from one that is broken.
/// </param>
public sealed record NotificationFeed(
    IReadOnlyList<NotificationRecord> Notifications,
    long? LastSeenNotificationId,
    int UnreadCount,
    bool Personalized);

/// <summary>
/// The global notification feed and the per-user cursors into it — see phase 77.
/// <para>
/// **One append-only table everyone reads, in <c>Logs</c>' shape.** A caller polls with the highest
/// <c>Id</c> it holds and is handed everything above it; nothing here is per-consumer except the
/// cursor, and the cursor is one number. That is what makes a missed poll, a repeated poll and a
/// second browser tab all cost nothing.
/// </para>
/// <para>
/// **The producers are not here.** A notification about a failed run is written inside the same
/// transaction that records the failure, by the store that owns that table — see
/// <see cref="TaskRunStore.CompleteRun"/> and <see cref="TaskRunStore.SetPaused"/> — because a
/// notification that could land without the event it describes, or the event without it, is worse
/// than either alone. <see cref="Insert"/> is the shared statement those call sites write through.
/// </para>
/// </summary>
public sealed class NotificationStore(StateDatabase database)
{
    /// <summary>
    /// Writes one notification on a connection somebody else owns, so it can join a transaction that
    /// is already recording the thing being announced.
    /// <para>
    /// Internal, and deliberately the only way a row gets in: every producer this project has is a
    /// store already writing the underlying event, and a public "notify about anything" entry point
    /// would invite a second write from a caller with nothing to be atomic with.
    /// </para>
    /// </summary>
    internal static void Insert(
        StateDatabase database, DbConnection connection, DbTransaction? transaction,
        string kind, string message, string? taskName, string? mappingName, Guid? runId)
    {
        using var cmd = database.Command(connection, transaction, """
            INSERT INTO Notifications (Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message)
            VALUES ($kind, $now, $taskName, $mappingName, $runId, $message);
            """);
        cmd.Bind(database, "kind", kind);
        cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Bind(database, "taskName", (object?)taskName ?? DBNull.Value);
        cmd.Bind(database, "mappingName", (object?)mappingName ?? DBNull.Value);
        cmd.Bind(database, "runId", (object?)runId?.ToString() ?? DBNull.Value);
        cmd.Bind(database, "message", message);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Everything newer than <paramref name="sinceId"/>, oldest first. Null is the initial load —
    /// the whole table, which retention already bounds.
    /// </summary>
    public IReadOnlyList<NotificationRecord> List(long? sinceId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, sinceId is null
                ? "SELECT Id, Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message FROM Notifications ORDER BY Id;"
                : "SELECT Id, Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message FROM Notifications WHERE Id > $sinceId ORDER BY Id;");
            if (sinceId is not null)
                cmd.Bind(database, "sinceId", sinceId.Value);

            using var reader = cmd.ExecuteReader();
            var results = new List<NotificationRecord>();
            while (reader.Read())
                results.Add(new NotificationRecord(
                    reader.Int64(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    reader.GetString(6)));

            return (IReadOnlyList<NotificationRecord>)results;
        });

    /// <summary>How many rows sit above a cursor. Null counts the whole table, which is what an
    /// unauthenticated deployment and a user who has never acknowledged anything both mean.</summary>
    public int UnreadCount(long? lastSeenId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, lastSeenId is null
                ? "SELECT COUNT(*) FROM Notifications;"
                : "SELECT COUNT(*) FROM Notifications WHERE Id > $lastSeenId;");
            if (lastSeenId is not null)
                cmd.Bind(database, "lastSeenId", lastSeenId.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    /// <summary>Where this user's cursor is. Null for a user who has never marked anything seen —
    /// which is not the same as a cursor at 0, since 0 would also be the answer for a user who
    /// acknowledged an empty feed.</summary>
    public long? GetCursor(string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT LastSeenNotificationId FROM NotificationReadState WHERE UserId = $userId;");
            cmd.Bind(database, "userId", userId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.NullableInt64(0) : null;
        });

    /// <summary>
    /// Advances this user's cursor, and only ever forwards.
    /// <para>
    /// Two tabs polling the same feed will report different high-water marks, and the older one
    /// arriving second must not resurrect notifications the newer one already cleared. Monotonic here
    /// rather than in the caller, because every caller would have to get it right and the store is
    /// where the invariant can actually be enforced.
    /// </para>
    /// </summary>
    public void MarkSeen(string userId, long lastSeenNotificationId)
    {
        // Read before the write connection is opened, not inside it: nesting a second connection
        // inside an open one is how a SQLite writer deadlocks against its own reader.
        var highWaterMark = Math.Max(lastSeenNotificationId, GetCursor(userId) ?? 0);

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "NotificationReadState",
                "UserId, LastSeenNotificationId, UpdatedAtUtc",
                "$userId, $lastSeen, $now",
                "UserId",
                "LastSeenNotificationId = EXCLUDED.LastSeenNotificationId, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "userId", userId);
            cmd.Bind(database, "lastSeen", highWaterMark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes notifications older than <paramref name="maxAge"/>. Null keeps everything, matching how
    /// every other retention cap in this project reads a configured 0.
    /// <para>
    /// Cursors are left alone. A cursor is one small row per user that names an Id which may no longer
    /// exist, and an Id above every surviving row is exactly right — it means "nothing unread", which
    /// is what somebody who has read everything should still see after the rows they read are gone.
    /// </para>
    /// </summary>
    public int PruneNotifications(TimeSpan? maxAge) =>
        database.Retry(() =>
        {
            if (maxAge is null)
                return 0;

            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "DELETE FROM Notifications WHERE CreatedAtUtc < $cutoff;");
            cmd.Bind(database, "cutoff", DateTimeOffset.UtcNow.Subtract(maxAge.Value).ToString("O"));
            return cmd.ExecuteNonQuery();
        });
}
