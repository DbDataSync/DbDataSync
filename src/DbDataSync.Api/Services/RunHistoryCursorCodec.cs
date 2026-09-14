using System.Text.Json;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns a run-history keyset cursor into the opaque string a client round-trips, and back — see
/// phase 104.
/// <para>
/// **Opaque, not secret.** The token is base64 over a small JSON envelope; nothing here needs to
/// resist inspection, only avoid committing the API to the two raw column values a cursor is built
/// from, so the ordering behind it can change later without every client needing to learn about it.
/// </para>
/// <para>
/// **Carries the filters it was issued under.** A cursor minted while filtering to Failed runs means
/// nothing replayed against BulkLoad runs — the two keyset windows are different pages of different
/// data, and applying one's cursor to the other would produce a page that looks coherent and is not.
/// The alternative was trusting every caller to reset the cursor whenever a filter changes, which is
/// the kind of discipline that holds until somebody adds a filter and forgets — so the filters travel
/// inside the token instead, and are compared against the request's own on the way back in.
/// </para>
/// <para>
/// **A mismatch resets the page rather than failing the request.** A cursor that no longer matches its
/// own filters is answering a different question than the one being asked right now — most likely a
/// client that changed a filter without dropping the old cursor, or a stale bookmark — and "start that
/// question over at its first page" is a more useful response than a 400 for what is, from the
/// caller's side, an ordinary GET. The same reset happens for a token that fails to parse at all.
/// </para>
/// </summary>
public static class RunHistoryCursorCodec
{
    /// <summary>The envelope, field names kept short because they are what the token's bytes hold.</summary>
    private sealed record Token(string Task, string Time, string Run, string? Kind, string? Mapping, string? Status);

    public static string Encode(
        RunHistoryCursor cursor, string taskName, RunKind? kind, string? mappingName, RunStatus? status)
    {
        var token = new Token(
            taskName, cursor.EnqueuedAtUtc.ToString("O"), cursor.RunId.ToString(),
            kind?.ToString(), mappingName, status?.ToString());
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token));
    }

    /// <summary>
    /// Null on a fresh page-one request, on a token that will not parse, and on a token minted under
    /// different filters — every one of those means "there is no valid cursor for this request", and
    /// the caller treats all three the same way: serve page one under the filters actually asked for.
    /// </summary>
    public static RunHistoryCursor? Decode(
        string? raw, string taskName, RunKind? kind, string? mappingName, RunStatus? status)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        try
        {
            var token = JsonSerializer.Deserialize<Token>(Convert.FromBase64String(raw));
            if (token is null)
                return null;

            if (!string.Equals(token.Task, taskName, StringComparison.Ordinal))
                return null;
            if (!string.Equals(token.Kind, kind?.ToString(), StringComparison.Ordinal))
                return null;
            if (!string.Equals(token.Mapping, mappingName, StringComparison.Ordinal))
                return null;
            if (!string.Equals(token.Status, status?.ToString(), StringComparison.Ordinal))
                return null;

            return new RunHistoryCursor(DateTimeOffset.Parse(token.Time), Guid.Parse(token.Run));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}
