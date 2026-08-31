using System.Data.Common;

namespace DataSync.State;

/// <summary>The two things a person can be allowed to do. Two, and no more — every additional role is
/// a door that is hard to close once opened.</summary>
public enum UserRole
{
    /// <summary>Reads everything. Changes nothing, and causes nothing to happen.</summary>
    Viewer,

    /// <summary>Everything a viewer can do, plus every change and every action.</summary>
    Admin,
}

/// <summary>How somebody proved who they are.</summary>
public static class CredentialMethods
{
    public const string Windows = "Windows";
    public const string Passkey = "Passkey";
}

/// <param name="Email">For git attribution. Null where the method does not supply one.</param>
public sealed record UserRecord(
    string Id, string DisplayName, string? Email, UserRole Role, bool Enabled, DateTimeOffset CreatedAtUtc);

/// <param name="Subject">A Windows SID, or a passkey's credential id. What a sign-in is looked up by.</param>
/// <param name="Secret">A passkey's **public** key. Null for Windows, which stores no material at all.</param>
public sealed record UserCredentialRecord(
    string Id, string UserId, string Method, string Subject, string? Secret, string? Label,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc);

/// <summary>
/// Users, their credentials, and their sessions.
/// <para>
/// One user, any number of credentials — which is what makes "signs in with Windows at the office and
/// a passkey from home" a row rather than a schema change.
/// </para>
/// </summary>
public sealed class UserStore(StateDatabase database)
{
    public UserRecord CreateUser(string displayName, string? email, UserRole role) =>
        database.Retry(() =>
        {
            var user = new UserRecord(
                Guid.NewGuid().ToString("N"), displayName, email, role, Enabled: true, DateTimeOffset.UtcNow);

            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc)
                VALUES ($id, $name, $email, $role, $enabled, $createdAt);
                """);
            cmd.Bind(database, "id", user.Id);
            cmd.Bind(database, "name", user.DisplayName);
            cmd.Bind(database, "email", (object?)user.Email ?? DBNull.Value);
            cmd.Bind(database, "role", user.Role.ToString());
            cmd.Bind(database, "enabled", 1);
            cmd.Bind(database, "createdAt", user.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            return user;
        });

    public void AddCredential(
        string userId, string method, string subject, string? secret = null, string? label = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO UserCredentials (Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc)
                VALUES ($id, $userId, $method, $subject, $secret, $label, $createdAt);
                """);
            cmd.Bind(database, "id", Guid.NewGuid().ToString("N"));
            cmd.Bind(database, "userId", userId);
            cmd.Bind(database, "method", method);
            cmd.Bind(database, "subject", subject);
            cmd.Bind(database, "secret", (object?)secret ?? DBNull.Value);
            cmd.Bind(database, "label", (object?)label ?? DBNull.Value);
            cmd.Bind(database, "createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>The user a credential belongs to, or null. Records the use, because "when did this key
    /// last work" is the question a user-management screen exists to answer.</summary>
    public UserRecord? FindByCredential(string method, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();

            using (var touch = database.Command(connection, """
                UPDATE UserCredentials SET LastUsedAtUtc = $now
                WHERE Method = $method AND Subject = $subject;
                """))
            {
                touch.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                touch.Bind(database, "method", method);
                touch.Bind(database, "subject", subject);
                touch.ExecuteNonQuery();
            }

            using var cmd = database.Command(connection, """
                SELECT u.Id, u.DisplayName, u.Email, u.Role, u.Enabled, u.CreatedAtUtc
                FROM Users u
                JOIN UserCredentials c ON c.UserId = u.Id
                WHERE c.Method = $method AND c.Subject = $subject;
                """);
            cmd.Bind(database, "method", method);
            cmd.Bind(database, "subject", subject);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public UserRecord? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, DisplayName, Email, Role, Enabled, CreatedAtUtc FROM Users WHERE Id = $id;
                """);
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public IReadOnlyList<UserRecord> List() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, DisplayName, Email, Role, Enabled, CreatedAtUtc FROM Users ORDER BY DisplayName;
                """);
            using var reader = cmd.ExecuteReader();
            var users = new List<UserRecord>();
            while (reader.Read())
                users.Add(Read(reader));
            return (IReadOnlyList<UserRecord>)users;
        });

    /// <summary>Whether anybody exists at all. What decides a fresh install is a fresh install.</summary>
    public bool Any() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            // Not "SELECT 1 ... LIMIT 1": SQL Server's row limiting needs an ORDER BY, and there is
            // no meaningful order in which to ask whether anybody exists. EXISTS is the question.
            using var cmd = database.Command(
                connection, "SELECT CASE WHEN EXISTS (SELECT 1 FROM Users) THEN 1 ELSE 0 END;");
            return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        });

    public IReadOnlyList<UserCredentialRecord> CredentialsOf(string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc, LastUsedAtUtc
                FROM UserCredentials WHERE UserId = $userId ORDER BY CreatedAtUtc;
                """);
            cmd.Bind(database, "userId", userId);

            using var reader = cmd.ExecuteReader();
            var credentials = new List<UserCredentialRecord>();
            while (reader.Read())
                credentials.Add(new UserCredentialRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(7)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            return (IReadOnlyList<UserCredentialRecord>)credentials;
        });

    public void SetRole(string userId, UserRole role) => Update(userId, "Role", role.ToString());

    public void SetEnabled(string userId, bool enabled) => Update(userId, "Enabled", enabled ? 1 : 0);

    public bool RemoveCredential(string userId, string credentialId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM UserCredentials WHERE Id = $id AND UserId = $userId;");
            cmd.Bind(database, "id", credentialId);
            cmd.Bind(database, "userId", userId);
            return cmd.ExecuteNonQuery() == 1;
        });

    private void Update(string userId, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            // The column name is one of two literals chosen above, never operator input.
            cmd.CommandText = $"UPDATE Users SET {column} = $value WHERE Id = $id;";
            cmd.Bind(database, "value", value);
            cmd.Bind(database, "id", userId);
            cmd.ExecuteNonQuery();
        });

    private static UserRecord Read(DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        Enum.Parse<UserRole>(reader.GetString(3)),
        reader.Int32(4) == 1,
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
