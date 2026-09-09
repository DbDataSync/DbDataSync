namespace DbDataSync.Cli;

/// <summary>The minimal starter shape for <c>driver install</c> with no <c>--from</c> at all — an
/// empty <c>typeMap</c> maps every native type to <c>Unmappable</c>, which provisioning reports rather
/// than guesses at, so an incomplete descriptor fails loud, not silently. A curated starting point for
/// a common engine instead of this blank shell is <c>DbDataSync.Drivers.Descriptor.KnownDrivers</c>
/// (phase 117) — reached through <c>--from &lt;id&gt;</c>, which this class has no part in.</summary>
public static class DriverTemplates
{
    public static string Minimal(string id, string displayName, string libraryId) =>
        Fill("""
            id: __ID__
            displayName: __DISPLAY_NAME__
            library: __LIBRARY_ID__
            dialect:
              quoteIdentifier: doubleQuote      # backtick | doubleQuote | bracket
              parameterPrefix: "@"              # "@" -> @p , ":" -> :p , "?" -> positional
              rowLimit: offsetFetch             # limitOffset | offsetFetch
              catalog: informationSchema        # the only strategy supported today
              supportsChangeDatabase: true
              defaultDatabase: ""

            # Empty until filled in — every native type is Unmappable, which provisioning reports
            # rather than guesses at. See a --from <id> install for a filled-in example.
            typeMap: {}

            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """, id, displayName, libraryId);

    private static string Fill(string template, string id, string displayName, string libraryId) =>
        template
            .Replace("__ID__", id)
            .Replace("__DISPLAY_NAME__", displayName)
            .Replace("__LIBRARY_ID__", libraryId);
}
