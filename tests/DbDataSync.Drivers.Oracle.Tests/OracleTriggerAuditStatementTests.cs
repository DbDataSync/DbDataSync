namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>DDL-shape assertions with no database involved — the string-level defects worth catching
/// without a live server, mirroring the split <c>MySqlTriggerAuditStatementTests</c> uses.</summary>
public sealed class OracleTriggerAuditStatementTests
{
    [Fact]
    public void CreateTrigger_IsOneTrigger_NotThree()
    {
        var sql = OracleTriggerAudit.CreateTrigger("SCHEMA", "orders", ["id"]);

        Assert.Contains("AFTER INSERT OR UPDATE OR DELETE ON", sql);
        // Unlike MySQL, Oracle combines all three events in one definition.
        Assert.DoesNotContain("AFTER INSERT ON", sql);
    }

    [Fact]
    public void CreateTrigger_BranchesOnInsertingUpdatingDeleting()
    {
        var sql = OracleTriggerAudit.CreateTrigger("SCHEMA", "orders", ["id"]);

        Assert.Contains("IF INSERTING THEN", sql);
        Assert.Contains("ELSIF UPDATING THEN", sql);
        Assert.Contains("ELSE", sql);
    }

    [Fact]
    public void CreateTrigger_UsesColonPrefixedRowReferences()
    {
        var sql = OracleTriggerAudit.CreateTrigger("SCHEMA", "orders", ["id"]);

        Assert.Contains(":NEW.\"id\"", sql);
        Assert.Contains(":OLD.\"id\"", sql);
    }

    [Fact]
    public void CreateTrigger_QualifiesTheShadowTableWithSchemaDotTable()
    {
        var sql = OracleTriggerAudit.CreateTrigger("MYSCHEMA", "orders", ["id"]);

        Assert.Contains("\"MYSCHEMA\".\"DS_Changes_orders\"", sql);
    }

    [Fact]
    public void CreateTrigger_HandlesACompositeKey_InEveryBranch()
    {
        var sql = OracleTriggerAudit.CreateTrigger("SCHEMA", "orders", ["region", "id"]);

        Assert.Contains(":NEW.\"region\", :NEW.\"id\"", sql);
        Assert.Contains(":OLD.\"region\", :OLD.\"id\"", sql);
    }

    [Fact]
    public void CreateTrigger_UsesCreateOrReplace_SoItIsIdempotent()
    {
        var sql = OracleTriggerAudit.CreateTrigger("SCHEMA", "orders", ["id"]);

        Assert.Contains("CREATE OR REPLACE TRIGGER", sql);
    }

    [Fact]
    public void CreateShadowTable_UsesGeneratedAlwaysAsIdentity_NotAutoIncrement()
    {
        var sql = OracleTriggerAudit.CreateShadowTable("SCHEMA", "orders", ["\"id\" NUMBER NOT NULL"]);

        Assert.Contains("NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY", sql);
        Assert.DoesNotContain("AUTO_INCREMENT", sql);
    }

    [Fact]
    public void TriggerName_IsStable() =>
        Assert.Equal("ds_trg_orders", OracleTriggerAudit.TriggerName("orders"));
}
