using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Api.Services;

/// <summary>
/// A neutral dialect for testing a script that is not bound to anything yet.
/// <para>
/// Distinct from <c>DialectlessScriptDialect</c>, which throws — and rightly, because there it means
/// "this driver names no dialect" and generating SQL for it would be a fabrication. Here it means
/// "you have not said which engine", which is the normal state of a script one minute after it is
/// written, and refusing to run it then would make the test button useless exactly when it is most
/// wanted.
/// </para>
/// <para>
/// ANSI double quotes, and the result says so, because the identifier a script sees here is not
/// necessarily the one its engine will hand it.
/// </para>
/// </summary>
internal sealed class AnsiScriptDialect : IScriptDialect
{
    public static AnsiScriptDialect Instance { get; } = new();

    public string EngineName => "ANSI";

    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string ParameterReference(string name) => $"@{name}";

    public CanonicalType ToCanonicalType(string nativeType) => throw Unsupported();

    public RenderedColumnType RenderColumnType(CanonicalType type) => throw Unsupported();

    /// <summary>Type translation is genuinely engine-specific and has no neutral answer, so it still
    /// refuses rather than guessing — a script that needs it needs a connection chosen.</summary>
    private static InvalidOperationException Unsupported() =>
        new("Translating a column type needs a specific engine — choose a connection to test against.");
}
