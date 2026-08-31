using System.Data.Common;

namespace DataSync.State;

/// <param name="ExpiresAtUtc">When this stops being accepted. Sliding is deliberately not built:
/// a session that renews itself on every request never ends for a browser left open.</param>
public sealed record SessionRecord(string Id, string UserId, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Sign-ins, kept server-side.
/// <para>
/// A row rather than a signed token, because the thing that matters here is *revocation*: signing
/// somebody out, or disabling them, has to take effect on their next request rather than whenever a
/// token would have expired. There is no second service to present a bearer token to, so a token
/// would buy nothing and cost exactly that.
/// </para>
/// </summary>
public sealed class SessionStore(StateDatabase database)
{
    public static TimeSpan Lifetime { get; } = TimeSpan.FromHours(12);

    public SessionRecord Create(string userId)
    {
        var session = new SessionRecord(
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            userId,
            DateTimeOffset.UtcNow + Lifetime);

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO Sessions (Id, UserId, CreatedAtUtc, ExpiresAtUtc)
                VALUES ($id, $userId, $createdAt, $expiresAt);
                """);
            cmd.Bind(database, "id", session.Id);
            cmd.Bind(database, "userId", userId);
            cmd.Bind(database, "createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "expiresAt", session.ExpiresAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        });

        return session;
    }

    /// <summary>
    /// The user behind a session id, or null. Expiry is enforced here rather than by a sweep, so a
    /// session that outlives its row's deletion schedule is still refused.
    /// </summary>
    public UserRecord? Resolve(string sessionId, UserStore users)
    {
        var userId = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT UserId, ExpiresAtUtc FROM Sessions WHERE Id = $id;");
            cmd.Bind(database, "id", sessionId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var expiresAt = DateTimeOffset.Parse(
                reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
            return expiresAt > DateTimeOffset.UtcNow ? reader.GetString(0) : null;
        });

        if (userId is null)
            return null;

        // Read fresh every time, so disabling somebody or changing their role takes effect on their
        // next request rather than at the end of a session they are already holding.
        var user = users.Get(userId);
        return user is { Enabled: true } ? user : null;
    }

    public void Delete(string sessionId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM Sessions WHERE Id = $id;");
            cmd.Bind(database, "id", sessionId);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Ends every session a user holds — what disabling somebody, or removing their last
    /// credential, has to do to mean anything immediately.</summary>
    public void DeleteAllFor(string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM Sessions WHERE UserId = $userId;");
            cmd.Bind(database, "userId", userId);
            cmd.ExecuteNonQuery();
        });
}
