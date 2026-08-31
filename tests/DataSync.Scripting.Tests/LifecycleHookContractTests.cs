using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.Drivers.Postgres;
using DataSync.Scripting.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Scripting.Tests;

/// <summary>
/// Phase 27's motivating case — target schema evolution — exercised against the two real dialects
/// rather than a fake one, because a fake would happily accept a type spelling neither engine actually
/// uses. <see cref="AddMissingColumnsHook"/> is the reference implementation the phase doc sketches:
/// "for each mapped source column with no target column, emit ALTER TABLE ... ADD".
/// </summary>
public sealed class LifecycleHookContractTests
{
    private static readonly SourceTableRef Source = new()
    {
        ConnectionName = "src", Database = "Sales", Schema = "dbo", Table = "Orders",
    };

    private static readonly TableRef Target = new()
    {
        ConnectionName = "tgt", Database = "sales", Schema = "public", Table = "orders",
    };

    private static readonly IReadOnlyList<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Region", TargetColumn = "Region" },
    ];

    private static LifecycleHookContext Context(
        IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMetadata> targetColumns,
        string point = HookPoints.BeforeStage) => new(
        point, Source, Target, Mappings, sourceColumns, targetColumns,
        new FakeScriptDialect("MsSql", MsSqlDialect.Instance), new FakeScriptDialect("Postgres", PostgresDialect.Instance),
        new HookRunFacts(Guid.NewGuid(), "sync", "orders", "Primary", null, 0, 1, true, null, null, null, null),
        ScriptParameters.Empty, _ => { });

    [Fact]
    public void DeclarePoints_OnlyRunsAtTheDeclaredPoint()
    {
        var hook = new AddMissingColumnsHook();
        var declared = hook.DeclarePoints(Context([], []));

        Assert.Equal([HookPoints.BeforeStage], declared);
    }

    [Fact]
    public void NoMissingColumns_IsANoOp_EmptyListNotAnError()
    {
        var sourceColumns = new[]
        {
            new ColumnMetadata("Id", "int", false, true, false),
            new ColumnMetadata("Region", "nvarchar(50)", true, false, false),
        };
        var targetColumns = new[]
        {
            new ColumnMetadata("Id", "integer", false, true, false),
            new ColumnMetadata("Region", "varchar(50)", true, false, false),
        };

        var statements = new AddMissingColumnsHook().BuildStatements(HookPoints.BeforeStage, Context(sourceColumns, targetColumns));

        Assert.Empty(statements);
    }

    [Fact]
    public void AMissingColumn_EmitsAlterTableAdd_WithTheTypeTranslatedForTheTargetEngine()
    {
        var sourceColumns = new[]
        {
            new ColumnMetadata("Id", "int", false, true, false),
            new ColumnMetadata("Region", "nvarchar(50)", true, false, false),
        };
        var targetColumns = new[] { new ColumnMetadata("Id", "integer", false, true, false) };

        var statements = new AddMissingColumnsHook().BuildStatements(HookPoints.BeforeStage, Context(sourceColumns, targetColumns));

        var statement = Assert.Single(statements);
        // MsSql source -> Postgres target: nvarchar(50) becomes varchar(50), per phase 25's table.
        Assert.Equal("ALTER TABLE \"public\".\"orders\" ADD \"Region\" varchar(50) NULL;", statement.CommandText);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void AnUnmappableSourceColumn_IsSkippedRatherThanGuessed()
    {
        var sourceColumns = new[]
        {
            new ColumnMetadata("Id", "int", false, true, false),
            new ColumnMetadata("Region", "hierarchyid", true, false, false),
        };
        var targetColumns = new[] { new ColumnMetadata("Id", "integer", false, true, false) };

        var statements = new AddMissingColumnsHook().BuildStatements(HookPoints.BeforeStage, Context(sourceColumns, targetColumns));

        Assert.Empty(statements);
    }

    [Fact]
    public void RowsWritten_IsNullBeforeTheLoadAndPopulatedAfter()
    {
        var beforeLoad = new HookRunFacts(Guid.NewGuid(), "sync", "orders", "Primary", null, 0, 1, true, 10, null, "[stg]", null);
        var afterLoad = beforeLoad with { RowsWritten = 10 };

        Assert.Null(beforeLoad.RowsWritten);
        Assert.Equal(10, afterLoad.RowsWritten);
    }

    [Fact]
    public void AParameterValueThatLooksLikeInjection_ReachesTheStatementAsAValue_NeverInterpolated()
    {
        const string maliciousValue = "x'; DROP TABLE y --";
        var statement = new HookStatement("UPDATE t SET Name = @name;", [new HookParameter("name", maliciousValue)]);

        Assert.DoesNotContain(maliciousValue, statement.CommandText);
        Assert.Equal(maliciousValue, Assert.Single(statement.Parameters).Value);
    }

    /// <summary>Wraps a real <see cref="SqlDialect"/> the way <c>RunnerScriptDialect</c> does in
    /// production — proving the contract against the actual translation tables phase 25 built, not a
    /// stand-in that would accept a spelling neither engine uses.</summary>
    private sealed class FakeScriptDialect(string engineName, DataSync.Core.Sql.SqlDialect dialect) : IScriptDialect
    {
        public string EngineName => engineName;
        public string QuoteIdentifier(string identifier) => dialect.QuoteIdentifier(identifier);
        public string ParameterReference(string name) => dialect.ParameterReference(name);
        public CanonicalType ToCanonicalType(string nativeType) => dialect.ToCanonicalType(nativeType);
        public RenderedColumnType RenderColumnType(CanonicalType type) => dialect.RenderColumnType(type);
    }

    /// <summary>The reference implementation phase 27's doc sketches by name. Declares only
    /// <c>beforeStage</c> — schema evolution has to happen before staging, which fails today with
    /// "Target column not found" the moment a mapped column is missing.</summary>
    private sealed class AddMissingColumnsHook : ILifecycleHook
    {
        public IReadOnlyList<string> DeclarePoints(LifecycleHookContext context) => [HookPoints.BeforeStage];

        public IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext context)
        {
            var targetNames = context.TargetColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statements = new List<HookStatement>();

            foreach (var mapping in context.ColumnMappings)
            {
                if (targetNames.Contains(mapping.TargetColumn))
                    continue;

                var sourceColumn = context.SourceColumns.First(
                    c => string.Equals(c.Name, mapping.SourceColumn, StringComparison.OrdinalIgnoreCase));
                var canonical = context.SourceDialect.ToCanonicalType(sourceColumn.NativeType);
                if (canonical.Kind == CanonicalTypeKind.Unmappable)
                    continue; // Never guess — the same rule phase 25's provisioner follows.

                var rendered = context.TargetDialect.RenderColumnType(canonical);
                var qualifiedTarget = $"{context.TargetDialect.QuoteIdentifier(context.Target.Schema)}.{context.TargetDialect.QuoteIdentifier(context.Target.Table)}";
                var nullability = sourceColumn.IsNullable ? "NULL" : "NOT NULL";
                statements.Add(new HookStatement(
                    $"ALTER TABLE {qualifiedTarget} ADD {context.TargetDialect.QuoteIdentifier(mapping.TargetColumn)} {rendered.Sql} {nullability};",
                    []));
            }

            return statements;
        }
    }
}
