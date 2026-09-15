using System.Text.Json;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns a bulk-load-history keyset cursor into the opaque string a client round-trips, and back —
/// structurally identical to <see cref="RunHistoryCursorCodec"/> (phase 104), applied to phase 139's
/// own Bulk Load History screen: an opaque base64-JSON envelope, carrying the one filter
/// (<c>mappingName</c>) it was issued under, resetting to page one — never a 400 — on any mismatch or
/// a token that fails to parse at all. See that codec's own doc comment for the fuller reasoning; it
/// applies here unchanged.
/// <para>
/// **Kept as an independent class rather than sharing a generic base with <see cref="RunHistoryCursorCodec"/>.**
/// The two differ in more than which filters they carry — this keyset's tiebreak is a <c>string</c>
/// <c>BatchId</c> where Run History's is a <c>Guid</c> <c>RunId</c>, and one filter here against three
/// there. What is actually shared between them — an opaque envelope, and "decode failure means page
/// one" — is a handful of lines each; factoring it out would trade that little duplication for a
/// generic base neither call site would read as simpler.
/// </para>
/// </summary>
public static class BulkLoadHistoryCursorCodec
{
    /// <summary>The envelope, field names kept short because they are what the token's bytes hold.</summary>
    private sealed record Token(string Task, string Time, string Batch, string? Mapping);

    public static string Encode(BulkLoadHistoryCursor cursor, string taskName, string? mappingName)
    {
        var token = new Token(taskName, cursor.CreatedAtUtc.ToString("O"), cursor.BatchId, mappingName);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token));
    }

    /// <summary>
    /// Null on a fresh page-one request, on a token that will not parse, and on a token minted under a
    /// different <paramref name="mappingName"/> filter — every one of those means "there is no valid
    /// cursor for this request", and the caller treats all three the same way: serve page one under
    /// the filter actually asked for.
    /// </summary>
    public static BulkLoadHistoryCursor? Decode(string? raw, string taskName, string? mappingName)
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
            if (!string.Equals(token.Mapping, mappingName, StringComparison.Ordinal))
                return null;

            return new BulkLoadHistoryCursor(DateTimeOffset.Parse(token.Time), token.Batch);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}
