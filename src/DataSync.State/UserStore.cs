using Microsoft.Data.Sqlite;

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
        SqliteRetry.Execute(() =>
        {
            var user = new UserRecord(
                Guid.NewGuid().ToString("N"), displayName, email, role, Enabled: true, DateTimeOffset.UtcNow);

            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc)
                VALUES ($id, $name, $email, $role, $enabled, $createdAt);
                """;
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$name", user.DisplayName);
            cmd.Parameters.AddWithValue("$email", (object?)user.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$role", user.Role.ToString());
            cmd.Parameters.AddWithValue("$enabled", 1);
            cmd.Parameters.AddWithValue("$createdAt", user.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            return user;
        });

    public void AddCredential(
        string userId, string method, string subject, string? secret = null, string? label = null) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UserCredentials (Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc)
                VALUES ($id, $userId, $method, $subject, $secret, $label, $createdAt);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$userId", userId);
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$subject", subject);
            cmd.Parameters.AddWithValue("$secret", (object?)secret ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>The user a credential belongs to, or null. Records the use, because "when did this key
    /// last work" is the question a user-management screen exists to answer.</summary>
    public UserRecord? FindByCredential(string method, string subject) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();

            using (var touch = connection.CreateCommand())
            {
                touch.CommandText = """
                    UPDATE UserCredentials SET LastUsedAtUtc = $now
                    WHERE Method = $method AND Subject = $subject;
                    """;
                touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                touch.Parameters.AddWithValue("$method", method);
                touch.Parameters.AddWithValue("$subject", subject);
                touch.ExecuteNonQuery();
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT u.Id, u.DisplayName, u.Email, u.Role, u.Enabled, u.CreatedAtUtc
                FROM Users u
                JOIN UserCredentials c ON c.UserId = u.Id
                WHERE c.Method = $method AND c.Subject = $subject;
                """;
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$subject", subject);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public UserRecord? Get(string id) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, DisplayName, Email, Role, Enabled, CreatedAtUtc FROM Users WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public IReadOnlyList<UserRecord> List() =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, DisplayName, Email, Role, Enabled, CreatedAtUtc FROM Users ORDER BY DisplayName;
                """;
            using var reader = cmd.ExecuteReader();
            var users = new List<UserRecord>();
            while (reader.Read())
                users.Add(Read(reader));
            return (IReadOnlyList<UserRecord>)users;
        });

    /// <summary>Whether anybody exists at all. What decides a fresh install is a fresh install.</summary>
    public bool Any() =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Users LIMIT 1;";
            return cmd.ExecuteScalar() is not null;
        });

    public IReadOnlyList<UserCredentialRecord> CredentialsOf(string userId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc, LastUsedAtUtc
                FROM UserCredentials WHERE UserId = $userId ORDER BY CreatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$userId", userId);

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
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM UserCredentials WHERE Id = $id AND UserId = $userId;";
            cmd.Parameters.AddWithValue("$id", credentialId);
            cmd.Parameters.AddWithValue("$userId", userId);
            return cmd.ExecuteNonQuery() == 1;
        });

    private void Update(string userId, string column, object value) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            // The column name is one of two literals chosen above, never operator input.
            cmd.CommandText = $"UPDATE Users SET {column} = $value WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
        });

    private static UserRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        Enum.Parse<UserRole>(reader.GetString(3)),
        reader.GetInt32(4) == 1,
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
