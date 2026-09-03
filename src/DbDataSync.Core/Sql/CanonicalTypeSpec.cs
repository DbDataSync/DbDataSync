namespace DbDataSync.Core.Sql;

/// <summary>
/// Splits a native type spec string (e.g. <c>"nvarchar(50)"</c>, <c>"numeric(18,2)"</c>,
/// <c>"double precision"</c>) into a lowercased base name and its parenthesised arguments — shared
/// parsing for every dialect's <see cref="SqlDialect.ToCanonicalType"/>, so each engine's switch only
/// has to name its own type spellings rather than re-write this splitting.
/// </summary>
public static class CanonicalTypeSpec
{
    public static (string BaseName, IReadOnlyList<string> Args) Parse(string nativeType)
    {
        var trimmed = nativeType.Trim();
        var paren = trimmed.IndexOf('(');
        if (paren < 0)
            return (trimmed.ToLowerInvariant(), []);

        var baseName = trimmed[..paren].Trim().ToLowerInvariant();
        var argsText = trimmed[(paren + 1)..trimmed.LastIndexOf(')')];
        var args = argsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return (baseName, args);
    }

    /// <summary>The argument at <paramref name="index"/> as an int, or <paramref name="fallback"/> when
    /// there is no such argument or it is the literal <c>"max"</c> marker.</summary>
    public static int? IntAt(IReadOnlyList<string> args, int index, int? fallback) =>
        index < args.Count && int.TryParse(args[index], out var value) ? value : fallback;
}
