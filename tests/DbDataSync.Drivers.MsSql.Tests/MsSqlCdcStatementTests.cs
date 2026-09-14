using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The two parts of a CDC read with real defects available in them: the select list, and the boundary
/// arithmetic. Both are assertable without a live SQL Server, which is why the statement builder is
/// separate from the reader.
/// </summary>
public sealed class MsSqlCdcStatementTests
{
    private static string Read(
        MsSqlCdcStatement.CdcFunction function = MsSqlCdcStatement.CdcFunction.NetChanges,
        params string[] columns) =>
        MsSqlCdcStatement.BuildRead("dbo_Orders", function, columns.Length == 0 ? ["Id", "Name"] : columns);

    /// <summary>
    /// The off-by-one this exists to prevent. CDC's window functions are inclusive of `@from`, so
    /// passing the stored LSN unchanged re-reads the last pass's final change every single pass —
    /// harmless to the data, and enough to make a monitoring graph say a quiet source is busy.
    /// </summary>
    [Fact]
    public void TheLowerBound_IsIncremented()
    {
        Assert.Contains("sys.fn_cdc_increment_lsn(@storedLsn)", Read());
        Assert.DoesNotContain("(@storedLsn, @toLsn", Read());
    }

    [Fact]
    public void NetChanges_IsPreferredWhenAvailable() =>
        Assert.Contains("cdc.fn_cdc_get_net_changes_dbo_Orders(", Read());

    [Fact]
    public void AllChanges_IsTheFallbackName() =>
        Assert.Contains(
            "cdc.fn_cdc_get_all_changes_dbo_Orders(",
            Read(MsSqlCdcStatement.CdcFunction.AllChanges));

    /// <summary>The operation is the first column, and the reader reads it by that ordinal.</summary>
    [Fact]
    public void TheOperationIsSelectedFirst()
    {
        var sql = Read();
        Assert.Contains("SELECT __$operation, [Id], [Name]", sql);
        Assert.Equal(0, MsSqlCdcStatement.OperationOrdinal);
    }

    /// <summary>
    /// Ordered by the change's own position, so a window containing two changes to one key is applied
    /// in the order they happened — the difference between all-changes being usable and being a coin
    /// toss.
    /// <para>
    /// <c>__$seqval</c> orders changes *within* a transaction and only all-changes returns it: net
    /// changes has already collapsed them. Ordering by it unconditionally is an "Invalid column name"
    /// on the mode this reader prefers, which is what the integration tests found.
    /// </para>
    /// </summary>
    [Fact]
    public void AllChangesOrderWithinATransaction_AndNetChangesCannot()
    {
        Assert.Contains(
            "ORDER BY __$start_lsn, __$seqval", Read(MsSqlCdcStatement.CdcFunction.AllChanges));

        Assert.Contains("ORDER BY __$start_lsn;", Read());
        Assert.DoesNotContain("__$seqval", Read());
    }

    [Fact]
    public void ATransformIsRenderedAndAliasedBack()
    {
        var sql = MsSqlCdcStatement.BuildRead(
            "dbo_Orders", MsSqlCdcStatement.CdcFunction.NetChanges, ["Name"],
            column => $"UPPER([{column}]) AS [{column}]");

        Assert.Contains("UPPER([Name]) AS [Name]", sql);
    }

    [Theory]
    [InlineData(2, MsSqlCdcStatement.ChangeOperationCode.Insert)]
    [InlineData(4, MsSqlCdcStatement.ChangeOperationCode.Update)]
    [InlineData(1, MsSqlCdcStatement.ChangeOperationCode.Delete)]
    public void OperationCodes_MapToChanges(int code, MsSqlCdcStatement.ChangeOperationCode expected) =>
        Assert.Equal(expected, MsSqlCdcStatement.Operation(code));

    /// <summary>An update-before image can only arrive from a statement this reader did not build, so
    /// guessing at it would be worse than saying so.</summary>
    [Fact]
    public void AnUpdateBeforeImage_SaysItCameFromSomewhereElse() =>
        Assert.Contains(
            "never requests",
            Assert.Throws<InvalidOperationException>(() => MsSqlCdcStatement.Operation(3)).Message);

    [Fact]
    public void AnUnknownOperation_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => MsSqlCdcStatement.Operation(9));

    private static string Bounded(
        MsSqlCdcStatement.CdcFunction function = MsSqlCdcStatement.CdcFunction.NetChanges) =>
        MsSqlCdcStatement.BuildRead("dbo_Orders", function, ["Id", "Name"], bounded: true);

    [Fact]
    public void Unbounded_IsUnchanged_SoTurningTheCapOffRestoresTheOldStatement()
    {
        Assert.DoesNotContain("TOP", Read());
        Assert.DoesNotContain("__DS_Position", Read());
    }

    [Fact]
    public void Bounded_CapsWithTies_OnTheRowLimitParameter()
    {
        Assert.Contains("TOP (@maxRows) WITH TIES", Bounded());
        Assert.Contains("TOP (@maxRows) WITH TIES", Bounded(MsSqlCdcStatement.CdcFunction.AllChanges));
    }

    /// <summary>
    /// The correctness argument of the whole phase, asserted as a shape. A CDC watermark is an LSN, so
    /// the finest position a pass can *record* is an LSN — which means the tie has to be on
    /// <c>__$start_lsn</c> alone. Tying on <c>(__$start_lsn, __$seqval)</c>, which is what the row
    /// ordering suggests and what a first reading of the phase doc says, would let a pass stop halfway
    /// through a transaction, record that transaction's LSN, and have the next pass resume strictly
    /// beyond it — losing every remaining row of that transaction with nothing to say so.
    /// </summary>
    [Fact]
    public void Bounded_TiesOnTheLsnAlone_SoATransactionIsNeverSplitAcrossPasses()
    {
        // The inner ORDER BY is the one WITH TIES reads its boundary from, so that is the one asserted:
        // everything between the derived table's parentheses, for the mode that has a __$seqval to be
        // tempted by.
        var inner = InnerQuery(Bounded(MsSqlCdcStatement.CdcFunction.AllChanges));

        Assert.EndsWith("ORDER BY __$start_lsn", inner.TrimEnd());
        Assert.DoesNotContain("ORDER BY __$start_lsn, __$seqval", inner);
    }

    /// <summary>Everything the derived table wraps — the part <c>WITH TIES</c> applies to.</summary>
    private static string InnerQuery(string sql)
    {
        var open = sql.IndexOf("FROM (", StringComparison.Ordinal) + "FROM (".Length;
        var close = sql.IndexOf(") AS capped", StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, "the bounded statement should wrap its cap in a derived table");
        return sql[open..close];
    }

    /// <summary>
    /// Two orderings, because they answer two different questions: where may this pass stop, and in
    /// what order did these changes happen. The cap is taken in a derived table ordered by the LSN;
    /// the rows come out of it ordered by the LSN and then by <c>__$seqval</c>, which is what keeps
    /// row-level ordering inside a transaction that the cap landed in the middle of.
    /// </summary>
    [Fact]
    public void Bounded_StillOrdersWithinATransaction()
    {
        Assert.Contains("ORDER BY [__DS_Position], __$seqval;", Bounded(MsSqlCdcStatement.CdcFunction.AllChanges));

        // Net changes has already collapsed the transaction __$seqval would order inside, and selecting
        // it there is an "Invalid column name" — the same trap the unbounded ordering has.
        Assert.Contains("ORDER BY [__DS_Position];", Bounded());
        Assert.DoesNotContain("__$seqval", Bounded());
    }

    /// <summary>
    /// The position column is appended last and <c>__$seqval</c> is not projected outwards, so a
    /// bounded result set is the unbounded one plus one trailing column — which is what lets the reader
    /// keep reading <c>__$operation</c> at 0 and the mapped columns at 1..n with the cap on or off.
    /// </summary>
    [Fact]
    public void Bounded_AppendsThePositionColumnLast_AndProjectsNothingElseExtra()
    {
        var sql = Bounded(MsSqlCdcStatement.CdcFunction.AllChanges);

        Assert.Contains(
            "SELECT __$operation, [Id], [Name], [__DS_ChangeOrdering], [__DS_ChangedAtUtc], [__DS_Position]", sql);
        // __$seqval is carried inside the derived table only, for the outer ORDER BY to use.
        Assert.Contains("__$seqval", InnerQuery(sql));
        Assert.DoesNotContain("__$seqval", sql[..sql.IndexOf("FROM (", StringComparison.Ordinal)]);
        Assert.Equal(0, MsSqlCdcStatement.OperationOrdinal);
        Assert.Equal(3, MsSqlCdcStatement.PositionOrdinal(2));
    }

    [Fact]
    public void Bounded_StillIncrementsTheLowerBound()
    {
        Assert.Contains("sys.fn_cdc_increment_lsn(@storedLsn)", Bounded());
        Assert.DoesNotContain("(@storedLsn, @toLsn", Bounded());
    }

    [Fact]
    public void Bounded_AppliesATransformInsideTheCap_AndSelectsTheAliasOutside()
    {
        var sql = MsSqlCdcStatement.BuildRead(
            "dbo_Orders", MsSqlCdcStatement.CdcFunction.NetChanges, ["Name"],
            column => $"UPPER([{column}]) AS [{column}]", bounded: true);

        Assert.Contains("UPPER([Name]) AS [Name]", sql);
        Assert.Contains(
            "SELECT __$operation, [Name], [__DS_ChangeOrdering], [__DS_ChangedAtUtc], [__DS_Position]", sql);
    }

    // ---- Phase 132: per-row source order and time ------------------------------------------------

    /// <summary>Both new columns appear in the unbounded shape, right after the mapped columns.</summary>
    [Fact]
    public void Unbounded_ProjectsBothOrderingColumns()
    {
        var sql = Read(MsSqlCdcStatement.CdcFunction.AllChanges);

        Assert.Contains("SELECT __$operation, [Id], [Name],", sql);
        Assert.Contains(") AS [__DS_ChangeOrdering]", sql);
        Assert.Contains("sys.fn_cdc_map_lsn_to_time(__$start_lsn) AS [__DS_ChangedAtUtc]", sql);
        Assert.Contains("CONVERT(varchar(20), __$start_lsn, 2)", sql);
        // Appended right after the mapped columns, ahead of the changed-at column, in that order.
        Assert.True(
            sql.IndexOf("AS [__DS_ChangeOrdering]", StringComparison.Ordinal) <
            sql.IndexOf("AS [__DS_ChangedAtUtc]", StringComparison.Ordinal));
    }

    /// <summary>And in the bounded shape too, ahead of the position column — see
    /// <see cref="Bounded_AppendsThePositionColumnLast_AndProjectsNothingElseExtra"/> for the exact
    /// ordering.</summary>
    [Fact]
    public void Bounded_ProjectsBothOrderingColumns()
    {
        var sql = Bounded(MsSqlCdcStatement.CdcFunction.AllChanges);

        Assert.Contains("[__DS_ChangeOrdering]", sql);
        Assert.Contains("[__DS_ChangedAtUtc]", sql);
    }

    /// <summary>
    /// Net changes has no <c>__$seqval</c> to read — the function does not return it at all, not merely
    /// null — so referencing it would be the same "Invalid column name" trap the existing ORDER BY
    /// already avoids. A literal zero stands in for the missing half instead.
    /// </summary>
    [Fact]
    public void NetChanges_UsesALiteralZeroForTheMissingSeqvalHalf()
    {
        var sql = Read(); // NetChanges by default

        Assert.Contains("CONVERT(varchar(20), 0x00000000000000000000, 2)", sql);
        Assert.DoesNotContain("__$seqval", sql);
    }

    /// <summary>All-changes has a real <c>__$seqval</c>, null-guarded the same way the existing
    /// <c>ISNULL(__$seqval, ...)</c> pattern would be used anywhere else in this statement.</summary>
    [Fact]
    public void AllChanges_UsesTheRealSeqvalForTheOrderingColumn()
    {
        var sql = Read(MsSqlCdcStatement.CdcFunction.AllChanges);

        Assert.Contains("ISNULL(__$seqval, 0x00000000000000000000)", sql);
    }

    /// <summary>
    /// Ordinal math: the two new columns are real, schema-visible columns (unlike the position column),
    /// so a schema built from <c>[mapped columns] + [ordering, changedAt]</c> is what <c>PositionOrdinal</c>
    /// has to account for once a bounded read appends its own column after them. Confirmed for both a
    /// 2-mapped-column and a 0-mapped-column schema, cap on and off — <c>PositionOrdinal</c> itself is
    /// pure arithmetic (<c>columnCount + 1</c>), so this is really confirming the reader's own schema
    /// (mapped count + 2) is what gets passed to it, not a property of the statement text.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(5, 6)]
    public void PositionOrdinal_AccountsForTheSchemaItIsGivenIncludingTheTwoOrderingColumns(
        int schemaColumnCount, int expectedPositionOrdinal) =>
        Assert.Equal(expectedPositionOrdinal, MsSqlCdcStatement.PositionOrdinal(schemaColumnCount));

    /// <summary>The reader's own schema — what it actually hands <c>PositionOrdinal</c> — is the mapped
    /// columns plus the two ordering columns, never just the mapped ones.</summary>
    [Fact]
    public void TheReadersSchema_IsMappedColumnsPlusBothOrderingColumns()
    {
        IReadOnlyList<string> mapped = ["Id", "Name"];
        var schema = new ChangeSchema([.. mapped, ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);

        Assert.Equal(4, schema.Count);
        // Past __$operation: the position column (were this read bounded) lands right after these four.
        Assert.Equal(5, MsSqlCdcStatement.PositionOrdinal(schema.Count));
        Assert.True(schema.TryGetOrdinal(ChangeOrdering.OrderingColumn, out var orderingOrdinal));
        Assert.Equal(2, orderingOrdinal);
        Assert.True(schema.TryGetOrdinal(ChangeOrdering.ChangedAtColumn, out var changedAtOrdinal));
        Assert.Equal(3, changedAtOrdinal);
    }
}
