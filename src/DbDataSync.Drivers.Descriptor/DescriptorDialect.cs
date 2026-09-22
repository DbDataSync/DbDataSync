using System.Data.Common;
using System.Globalization;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// A <see cref="SqlDialect"/> built entirely from a <c>driver.yaml</c>'s <c>dialect</c> and
/// <c>typeMap</c> blocks — data instead of code, per
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The descriptor*. Provisioning
/// (<see cref="RenderColumnType"/>) is out of this phase's scope and throws; everything else a
/// descriptor driver's generic Kinds need (quoting, parameter placeholders, row-limit rendering,
/// native → canonical type lookup) is here.
/// </summary>
public sealed class DescriptorDialect : SqlDialect
{
    private readonly char _quoteOpen;
    private readonly char _quoteClose;
    private readonly string _parameterPrefix;
    private readonly bool _parameterNameIsBare;
    private readonly bool _limitOffsetStyle;
    private readonly bool _supportsChangeDatabase;
    private readonly IReadOnlyDictionary<string, (IReadOnlyList<string> Placeholders, TypeMapEntryYaml Entry)> _typeMap;

    public DescriptorDialect(DescriptorDialectYaml spec, IReadOnlyDictionary<string, TypeMapEntryYaml> typeMap)
    {
        (_quoteOpen, _quoteClose) = spec.QuoteIdentifier switch
        {
            "backtick" => ('`', '`'),
            "doubleQuote" => ('"', '"'),
            "bracket" => ('[', ']'),
            _ => throw new NotSupportedException(
                $"Unknown quoteIdentifier style '{spec.QuoteIdentifier}' (expected backtick, doubleQuote or bracket)."),
        };
        _parameterPrefix = spec.ParameterPrefix;
        _parameterNameIsBare = spec.ParameterNameIsBare;
        _limitOffsetStyle = spec.RowLimit switch
        {
            "limitOffset" => true,
            "offsetFetch" => false,
            _ => throw new NotSupportedException(
                $"Unknown rowLimit style '{spec.RowLimit}' (expected limitOffset or offsetFetch)."),
        };
        _supportsChangeDatabase = spec.SupportsChangeDatabase;
        _typeMap = BuildTypeMap(typeMap);
    }

    protected override char IdentifierQuoteOpen => _quoteOpen;
    protected override char IdentifierQuoteClose => _quoteClose;

    public override string QuoteIdentifier(string identifier) =>
        $"{_quoteOpen}{identifier.Replace(_quoteClose.ToString(), $"{_quoteClose}{_quoteClose}")}{_quoteClose}";

    public override string ParameterReference(string name) => $"{_parameterPrefix}{name}";

    /// <summary>See <see cref="DescriptorDialectYaml.ParameterNameIsBare"/> — false keeps the base
    /// class's own default (identical to <see cref="ParameterReference"/>, the sigil included), which
    /// every ADO.NET provider so far tolerates.</summary>
    public override string ParameterName(string name) => _parameterNameIsBare ? name : base.ParameterName(name);

    /// <summary>
    /// <c>SupportsChangeDatabase: false</c> makes this a config error rather than a runtime one that
    /// only surfaces the first time a mapping actually reads — <see cref="ConfigValidation"/>-adjacent
    /// but stated here, since only the dialect knows whether its engine has the notion at all.
    /// </summary>
    public override Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        if (!_supportsChangeDatabase)
        {
            throw new InvalidOperationException(
                $"This driver's engine does not support switching database at connect time ('{database}' " +
                "was requested); every mapping must already point at the connection's own database.");
        }

        return base.UseDatabaseAsync(connection, database, cancellationToken);
    }

    /// <summary>
    /// <c>limitOffset</c> engines lose tie-safety (see the base class's own doc comment): a plain
    /// <c>LIMIT n</c> can split a group of rows sharing the boundary value across two passes, silently
    /// skipping the tied-but-unread ones. Accepted for this phase's scope — an engine that needs both
    /// bulk batch reads and guaranteed-no-skip behaviour is exactly the signal to write a compiled
    /// dialect instead of a descriptor one.
    /// </summary>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        _limitOffsetStyle
            ? ("", $"\nLIMIT {ParameterReference(parameterName)}")
            : base.RenderTieSafeRowLimit(parameterName);

    /// <summary>
    /// A name-keyed lookup over <c>typeMap</c>, with <c>(p,s)</c>-style argument substitution — strictly
    /// a table, no branching. An engine whose type mapping genuinely branches on a value
    /// (<c>tinyint(1)</c> as boolean) is the signal to write a compiled driver instead, per the plan
    /// doc; this deliberately does not grow that capability.
    /// </summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        if (!_typeMap.TryGetValue(baseName, out var mapping))
            return new CanonicalType(CanonicalTypeKind.Unmappable, null, null, null, false, false);

        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < mapping.Placeholders.Count && i < args.Count; i++)
            bound[mapping.Placeholders[i]] = args[i];

        var entry = mapping.Entry;
        return new CanonicalType(
            Enum.Parse<CanonicalTypeKind>(entry.Kind),
            Length: entry.Max ? null : ResolvePlaceholder(entry.Length, bound),
            Precision: ResolvePlaceholder(entry.Precision, bound),
            Scale: ResolvePlaceholder(entry.Scale, bound),
            IsUnicode: entry.Unicode,
            IsMax: entry.Max);
    }

    /// <summary>Not supported in this phase — a descriptor driver's initial scope is watermark and
    /// batch reload only, neither of which renders a target column. See the plan doc's §*The
    /// descriptor* — provisioning needs the reverse (canonical → native) direction of exactly the table
    /// that stays deliberately minimal here.</summary>
    public override RenderedColumnType RenderColumnType(CanonicalType type) =>
        throw new NotSupportedException(
            "Provisioning is not supported for a driver defined by a YAML descriptor. " +
            "Write a compiled driver if this engine needs target-table DDL.");

    private static IReadOnlyDictionary<string, (IReadOnlyList<string>, TypeMapEntryYaml)> BuildTypeMap(
        IReadOnlyDictionary<string, TypeMapEntryYaml> typeMap)
    {
        var result = new Dictionary<string, (IReadOnlyList<string>, TypeMapEntryYaml)>(StringComparer.Ordinal);
        foreach (var (key, entry) in typeMap)
        {
            var (baseName, placeholders) = CanonicalTypeSpec.Parse(key);
            result[baseName] = (placeholders, entry);
        }
        return result;
    }

    /// <summary>A field's YAML value is either one of this entry's own placeholder names (bound from
    /// the matched native type's own arguments) or a literal integer (a fixed mapping, e.g. a
    /// hand-written <c>decimal(10,2): { kind: Decimal, precision: "10", scale: "2" }</c>).</summary>
    private static int? ResolvePlaceholder(string? value, IReadOnlyDictionary<string, string> bound)
    {
        if (value is null)
            return null;
        if (bound.TryGetValue(value, out var resolved))
            return int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boundInt) ? boundInt : null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal) ? literal : null;
    }
}
