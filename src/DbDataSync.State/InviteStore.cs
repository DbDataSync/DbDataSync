using System.Security.Cryptography;
using System.Text;

namespace DbDataSync.State;

/// <param name="UserId">Set when this invite adds a credential to somebody who already exists, rather
/// than creating a new user. That is how a Windows-authenticated person enrols a passkey for when they
/// are off the domain — the "one user, both methods" case made concrete.</param>
/// <param name="CreatedByUserId">Null for the bootstrap invite, because nobody made it.</param>
public sealed record InviteRecord(
    string Id, UserRole Role, string? UserId, string? CreatedByUserId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RedeemedAtUtc);

/// <summary>A freshly minted invite. The code is returned **once** and never stored — see
/// <see cref="InviteStore"/>.</summary>
public sealed record MintedInvite(InviteRecord Invite, string Code);

/// <summary>
/// One-time invitations.
/// <para>
/// **The code is hashed, never stored.** It arrives over chat or email and is worth exactly what a
/// password is worth; a database somebody can read must not be a database somebody can sign in from.
/// The code is returned once, at creation, and cannot be recovered afterwards — which is why the UI
/// shows it once and says so.
/// </para>
/// <para>
/// Short-lived by default. An invite that works for a month is a password with an expiry date nobody
/// remembers.
/// </para>
/// </summary>
public sealed class InviteStore(StateDatabase database)
{
    public static TimeSpan DefaultLifetime { get; } = TimeSpan.FromHours(24);

    public MintedInvite Create(
        UserRole role, string? forUserId = null, string? createdByUserId = null, TimeSpan? lifetime = null)
    {
        // URL-safe, and long enough that guessing is not a strategy. Base64Url of 32 bytes.
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var invite = new InviteRecord(
            Guid.NewGuid().ToString("N"), role, forUserId, createdByUserId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + (lifetime ?? DefaultLifetime), null);

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO Invites (Id, CodeHash, Role, UserId, CreatedByUserId, CreatedAtUtc, ExpiresAtUtc)
                VALUES ($id, $hash, $role, $userId, $createdBy, $createdAt, $expiresAt);
                """);
            cmd.Bind(database, "id", invite.Id);
            cmd.Bind(database, "hash", Hash(code));
            cmd.Bind(database, "role", role.ToString());
            cmd.Bind(database, "userId", (object?)forUserId ?? DBNull.Value);
            cmd.Bind(database, "createdBy", (object?)createdByUserId ?? DBNull.Value);
            cmd.Bind(database, "createdAt", invite.CreatedAtUtc.ToString("O"));
            cmd.Bind(database, "expiresAt", invite.ExpiresAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        });

        return new MintedInvite(invite, code);
    }

    /// <summary>
    /// The invite a code names, if it is still usable. Null covers every reason it is not — unknown,
    /// expired, already redeemed — because telling a caller *which* would help somebody working
    /// through guesses.
    /// </summary>
    public InviteRecord? Find(string code) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, Role, UserId, CreatedByUserId, CreatedAtUtc, ExpiresAtUtc, RedeemedAtUtc
                FROM Invites WHERE CodeHash = $hash;
                """);
            cmd.Bind(database, "hash", Hash(code));

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var invite = new InviteRecord(
                reader.GetString(0),
                Enum.Parse<UserRole>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Time(reader.GetString(4)),
                Time(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Time(reader.GetString(6)));

            return invite is { RedeemedAtUtc: null } && invite.ExpiresAtUtc > DateTimeOffset.UtcNow
                ? invite
                : null;
        });

    /// <summary>
    /// Marks an invite used, and refuses to do it twice.
    /// <para>
    /// The <c>RedeemedAtUtc IS NULL</c> in the WHERE clause is what makes "single use" true rather than
    /// intended: two redemptions racing each other both find the invite valid, and only one of them
    /// updates a row.
    /// </para>
    /// </summary>
    public bool Redeem(string inviteId, string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                UPDATE Invites SET RedeemedAtUtc = $now, RedeemedByUserId = $userId
                WHERE Id = $id AND RedeemedAtUtc IS NULL;
                """);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "userId", userId);
            cmd.Bind(database, "id", inviteId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Throws away every unredeemed invite that nobody created — the bootstrap one, once
    /// there is a user and it has stopped being the only way in.</summary>
    public void DeleteBootstrapInvites() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM Invites WHERE CreatedByUserId IS NULL AND RedeemedAtUtc IS NULL;");
            cmd.ExecuteNonQuery();
        });

    public IReadOnlyList<InviteRecord> ListOutstanding() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, Role, UserId, CreatedByUserId, CreatedAtUtc, ExpiresAtUtc, RedeemedAtUtc
                FROM Invites WHERE RedeemedAtUtc IS NULL AND ExpiresAtUtc > $now ORDER BY CreatedAtUtc DESC;
                """);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));

            using var reader = cmd.ExecuteReader();
            var invites = new List<InviteRecord>();
            while (reader.Read())
                invites.Add(new InviteRecord(
                    reader.GetString(0), Enum.Parse<UserRole>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    Time(reader.GetString(4)), Time(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Time(reader.GetString(6))));
            return (IReadOnlyList<InviteRecord>)invites;
        });

    /// <summary>SHA-256 and no salt, deliberately: this is a 256-bit random value, not a password, so
    /// there is no dictionary to defend against and a per-row salt would buy nothing.</summary>
    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static DateTimeOffset Time(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
