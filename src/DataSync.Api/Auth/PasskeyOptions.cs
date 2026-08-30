namespace DataSync.Api.Auth;

/// <summary>
/// WebAuthn's relying-party settings — the origin passkeys are bound to.
/// <para>
/// **This is the thing that will go wrong.** A passkey registered against <c>localhost</c> does not
/// work against <c>datasync.corp.example</c>, and one registered against an IP address does not work
/// at all. It has to be configuration, it has to be checked at startup, and a mismatch has to say what
/// it is — because otherwise it fails inside a browser API with a message that names nothing.
/// </para>
/// </summary>
public sealed class PasskeyOptions
{
    /// <summary>The relying party id: a **domain**, with no scheme and no port. <c>localhost</c> for a
    /// local install; the hostname the console is reached at otherwise.</summary>
    public required string RelyingPartyId { get; init; }

    /// <summary>What a browser shows the user when it asks them to approve.</summary>
    public required string RelyingPartyName { get; init; }

    /// <summary>Full origins a passkey ceremony may come from, scheme and port included. Several,
    /// because a deployment reached at both a hostname and localhost is ordinary.</summary>
    public required IReadOnlySet<string> Origins { get; init; }

    public static PasskeyOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataSync:Auth:Passkeys");
        var id = section["RelyingPartyId"];
        var origins = section.GetSection("Origins").Get<string[]>();

        return new PasskeyOptions
        {
            // localhost is the only default that can be right without being told, and it is the one a
            // freshly installed tool actually runs at.
            RelyingPartyId = string.IsNullOrWhiteSpace(id) ? "localhost" : id,
            RelyingPartyName = section["RelyingPartyName"] ?? "DataSync",
            Origins = (origins is { Length: > 0 }
                ? origins
                : ["http://localhost:5080", "https://localhost:5080", "http://localhost:5173"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// What is wrong with this configuration, or null. Checked at startup so a deployment that cannot
    /// work says so then, rather than the first time somebody tries to enrol a key.
    /// </summary>
    public string? Problem()
    {
        if (RelyingPartyId.Contains("://", StringComparison.Ordinal) || RelyingPartyId.Contains(':'))
        {
            return
                $"The passkey relying-party id '{RelyingPartyId}' looks like a URL. It has to be a bare " +
                "domain — no scheme, no port. Put the full URL in Origins instead.";
        }

        if (System.Net.IPAddress.TryParse(RelyingPartyId, out _))
        {
            return
                $"The passkey relying-party id '{RelyingPartyId}' is an IP address. WebAuthn requires a " +
                "domain name, so passkeys cannot work on a deployment reached only by IP.";
        }

        var mismatched = Origins
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var uri)
                        && !uri.Host.Equals(RelyingPartyId, StringComparison.OrdinalIgnoreCase)
                        && !uri.Host.EndsWith("." + RelyingPartyId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return mismatched.Count == 0
            ? null
            : $"Passkey origin(s) {string.Join(", ", mismatched)} are not under the relying-party id " +
              $"'{RelyingPartyId}'. A browser refuses a ceremony whose origin does not match, and the " +
              "message it gives names neither.";
    }
}
