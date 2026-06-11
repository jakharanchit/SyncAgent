using FluentAssertions;
using SyncAgent.Config;
using Xunit;

namespace SyncAgent.Tests.Unit;

public class TableMapTests
{
    // ── GetEffectivePrimaryKeys ────────────────────────────────────────────────

    [Fact]
    public void GetEffectivePrimaryKeys_SinglePk_ReturnsSingleElementArray()
    {
        var map = new TableMap { PrimaryKey = "order_id" };
        map.GetEffectivePrimaryKeys().Should().Equal("order_id");
    }

    [Fact]
    public void GetEffectivePrimaryKeys_CompositePks_TakesPrecedenceOverSinglePk()
    {
        var map = new TableMap
        {
            PrimaryKey  = "ignored",
            PrimaryKeys = ["device_id", "seq_no"]
        };
        map.GetEffectivePrimaryKeys().Should().Equal("device_id", "seq_no");
    }

    [Fact]
    public void GetEffectivePrimaryKeys_EmptyPrimaryKeys_FallsBackToSinglePk()
    {
        var map = new TableMap { PrimaryKey = "id", PrimaryKeys = [] };
        map.GetEffectivePrimaryKeys().Should().Equal("id");
    }

    [Fact]
    public void GetEffectivePrimaryKeys_ThreeColumns_AllReturned()
    {
        var map = new TableMap { PrimaryKeys = ["a", "b", "c"] };
        map.GetEffectivePrimaryKeys().Should().HaveCount(3).And.Equal("a", "b", "c");
    }

    // ── GetEffectiveDeleteLogTable ─────────────────────────────────────────────

    [Fact]
    public void GetEffectiveDeleteLogTable_ExplicitValue_ReturnedAsIs()
    {
        new TableMap { SourceTable = "orders", DeleteLogTable = "custom_deletes" }
            .GetEffectiveDeleteLogTable().Should().Be("custom_deletes");
    }

    [Fact]
    public void GetEffectiveDeleteLogTable_Empty_DefaultsToSourceTableSuffix()
    {
        new TableMap { SourceTable = "orders", DeleteLogTable = "" }
            .GetEffectiveDeleteLogTable().Should().Be("orders_deletes");
    }

    [Fact]
    public void GetEffectiveDeleteLogTable_Whitespace_DefaultsToSourceTableSuffix()
    {
        new TableMap { SourceTable = "sessions", DeleteLogTable = "   " }
            .GetEffectiveDeleteLogTable().Should().Be("sessions_deletes");
    }

    [Fact]
    public void GetEffectiveDeleteLogTable_NullDeleteLogTable_DefaultsToSourceTableSuffix()
    {
        // Default value is "" which is empty, behaves same as unset
        new TableMap { SourceTable = "events" }
            .GetEffectiveDeleteLogTable().Should().Be("events_deletes");
    }

    // ── Default property values ───────────────────────────────────────────────

    [Fact]
    public void Defaults_AreCorrect()
    {
        var map = new TableMap();
        map.InjectStationId.Should().BeTrue();
        map.ConflictStrategy.Should().Be("nothing");
        map.PrimaryKeySeparator.Should().Be("|");
        map.SyncDeletes.Should().BeFalse();
        map.TimestampColumns.Should().BeEmpty();
        map.BooleanColumns.Should().BeEmpty();
        map.ExcludeColumns.Should().BeEmpty();
        map.ColumnMap.Should().BeEmpty();
        map.PrimaryKeys.Should().BeEmpty();
    }
}
