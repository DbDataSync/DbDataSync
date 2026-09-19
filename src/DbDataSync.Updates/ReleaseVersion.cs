using System.Globalization;

namespace DbDataSync.Updates;

/// <summary>
/// A DbDataSync version — <c>YYYY.M.D.HHmm</c>, optionally followed by a SemVer 2 prerelease label
/// (<c>-beta</c>, <c>-snapshot.g65615e7</c>). Ordered by NuGet's own rules, because the operator's
/// question is "is this newer than what I have" and `dotnet tool update` will answer it the same way:
/// numeric parts compare numerically, a prerelease sorts *below* the same numeric core, and prerelease
/// labels compare identifier by identifier (numeric identifiers below alphanumeric ones).
/// <para>
/// A hand-written parser rather than <c>NuGet.Versioning</c>: this codebase deliberately carries no NuGet
/// client library (see <c>LibrarySearchService</c>), and the versions it produces have one fixed shape.
/// </para>
/// <para>
/// Restricted to <c>[0-9A-Za-z.-]</c>, which is what makes a version safe to use as a directory name and a
/// URL path segment without any further escaping — <see cref="TryParse"/> is the single gate a version
/// string from the network passes through before it names anything on disk.
/// </para>
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int[] _numeric;
    private readonly string[] _label;

    private ReleaseVersion(string text, int[] numeric, string? label)
    {
        Text = text;
        _numeric = numeric;
        Label = label;
        _label = label is null ? [] : label.Split('.');
    }

    /// <summary>The version exactly as given (build metadata after <c>+</c> removed).</summary>
    public string Text { get; }

    /// <summary>The prerelease label without its leading hyphen, or null for a stable version.</summary>
    public string? Label { get; }

    /// <summary>Which of this product's channels the version belongs to, or null for a label that isn't
    /// one of them (a local development build's <c>-alpha.&lt;seconds&gt;</c>, for instance).</summary>
    public ReleaseChannel? Channel =>
        Label is null ? ReleaseChannel.Stable
        : Label == "beta" ? ReleaseChannel.Beta
        : Label == "snapshot" || Label.StartsWith("snapshot.", StringComparison.Ordinal) ? ReleaseChannel.Snapshot
        : null;

    /// <summary>
    /// When the build was made, read back out of the version itself — every version this product has ever
    /// published is <c>YYYY.M.D.HHmm</c> in UTC, so listing releases needs no extra request for a date.
    /// Null when the numeric parts are not a valid date and time.
    /// </summary>
    public DateTimeOffset? BuiltUtc
    {
        get
        {
            if (_numeric.Length != 4)
                return null;

            var hhmm = _numeric[3];
            try
            {
                return new DateTimeOffset(_numeric[0], _numeric[1], _numeric[2], hhmm / 100, hhmm % 100, 0, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var plus = text.IndexOf('+');
        var withoutBuild = plus >= 0 ? text[..plus] : text;
        if (withoutBuild.Length == 0 || !withoutBuild.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return false;

        var hyphen = withoutBuild.IndexOf('-');
        var core = hyphen >= 0 ? withoutBuild[..hyphen] : withoutBuild;
        var label = hyphen >= 0 ? withoutBuild[(hyphen + 1)..] : null;
        if (label is not null && (label.Length == 0 || label.Split('.').Any(p => p.Length == 0)))
            return false;

        var parts = core.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        var numeric = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0
                || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numeric[i]))
                return false;
        }

        version = new ReleaseVersion(withoutBuild, numeric, label);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a DbDataSync version.");

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;

        var length = Math.Max(_numeric.Length, other._numeric.Length);
        for (var i = 0; i < length; i++)
        {
            var mine = i < _numeric.Length ? _numeric[i] : 0;
            var theirs = i < other._numeric.Length ? other._numeric[i] : 0;
            if (mine != theirs)
                return mine.CompareTo(theirs);
        }

        // Same numeric core: no label is the *greater* one — a release outranks its own prereleases.
        if (Label is null || other.Label is null)
            return (Label is null ? 1 : 0) - (other.Label is null ? 1 : 0);

        var labelLength = Math.Min(_label.Length, other._label.Length);
        for (var i = 0; i < labelLength; i++)
        {
            var compared = CompareIdentifier(_label[i], other._label[i]);
            if (compared != 0)
                return compared;
        }

        return _label.Length.CompareTo(other._label.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);

        if (aNumeric && bNumeric)
        {
            // Compared as numbers without overflowing on a long run of digits: length first, then text.
            var trimmedA = a.TrimStart('0');
            var trimmedB = b.TrimStart('0');
            return trimmedA.Length != trimmedB.Length
                ? trimmedA.Length.CompareTo(trimmedB.Length)
                : string.CompareOrdinal(trimmedA, trimmedB);
        }

        if (aNumeric != bNumeric)
            return aNumeric ? -1 : 1;

        return string.CompareOrdinal(a, b);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    // Consistent with CompareTo: 2026.9.1.5 and 2026.09.01.0005 are the same version, so they hash alike.
    public override int GetHashCode()
    {
        var hash = new HashCode();
        var length = _numeric.Length;
        while (length > 0 && _numeric[length - 1] == 0)
            length--;
        for (var i = 0; i < length; i++)
            hash.Add(_numeric[i]);
        foreach (var identifier in _label)
            hash.Add(identifier.All(char.IsAsciiDigit) ? identifier.TrimStart('0') : identifier);
        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
}
