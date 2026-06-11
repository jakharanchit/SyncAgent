using FluentAssertions;
using SyncAgent.Data.Models;
using Xunit;

namespace SyncAgent.Tests.Unit;

public class WriteResultTests
{
    // ── WriteResult.Success ───────────────────────────────────────────────────

    [Fact]
    public void Success_PopulatesSucceeded_LeavesFailuresEmpty()
    {
        var records = new List<(string RecordId, string TableName)>
        {
            ("r1", "orders"),
            ("r2", "orders")
        };

        var result = WriteResult.Success(records);

        result.Succeeded.Should().BeEquivalentTo(records);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public void Success_EmptyList_BothCollectionsEmpty()
    {
        var result = WriteResult.Success([]);
        result.Succeeded.Should().BeEmpty();
        result.Failures.Should().BeEmpty();
    }

    // ── WriteResult.InfrastructureFailed ─────────────────────────────────────

    [Fact]
    public void InfrastructureFailed_AllRecordsInFailures_KindIsInfrastructure()
    {
        var records = new List<GenericRecord>
        {
            new() { RecordId = "r1", SourceTable = "orders" },
            new() { RecordId = "r2", SourceTable = "orders" }
        };
        var ex = new Exception("connection refused");

        var result = WriteResult.InfrastructureFailed(records, ex);

        result.Succeeded.Should().BeEmpty();
        result.Failures.Should().HaveCount(2);
        result.Failures.Should().AllSatisfy(f => f.Kind.Should().Be(FailureKind.Infrastructure));
        result.Failures.Select(f => f.RecordId).Should().Contain("r1", "r2");
    }

    [Fact]
    public void InfrastructureFailed_ExceptionPreservedOnEachFailure()
    {
        var ex = new Exception("timeout");
        var records = new List<GenericRecord> { new() { RecordId = "r1", SourceTable = "t" } };

        var result = WriteResult.InfrastructureFailed(records, ex);

        result.Failures[0].Exception.Should().BeSameAs(ex);
    }

    // ── WriteResult.DataFailed ────────────────────────────────────────────────

    [Fact]
    public void DataFailed_AllRecordsInFailures_KindIsData()
    {
        var records = new List<GenericRecord>
        {
            new() { RecordId = "r1", SourceTable = "orders" }
        };

        var result = WriteResult.DataFailed(records, new Exception("constraint violation"));

        result.Succeeded.Should().BeEmpty();
        result.Failures.Should().HaveCount(1);
        result.Failures[0].Kind.Should().Be(FailureKind.Data);
    }

    // ── CycleResult.Empty ─────────────────────────────────────────────────────

    [Fact]
    public void CycleResult_Empty_HasSafeDefaults()
    {
        var empty = CycleResult.Empty();

        empty.PostgresReachable.Should().BeTrue();
        empty.Synced.Should().Be(0);
        empty.Failed.Should().Be(0);
        empty.Deferred.Should().Be(0);
        empty.Deleted.Should().Be(0);
        empty.DeadLetterCount.Should().Be(0);
        empty.StillPending.Should().Be(0);
        empty.LastSyncedAt.Should().BeNull();
        empty.LastInfraErrorAt.Should().BeNull();
    }

    // ── FailedRecord ──────────────────────────────────────────────────────────

    [Fact]
    public void FailedRecord_DefaultKind_IsInfrastructure()
    {
        var record = new FailedRecord("r1", "orders", 0, new Exception());
        record.Kind.Should().Be(FailureKind.Infrastructure);
    }

    [Fact]
    public void FailedRecord_ExplicitKind_IsPreserved()
    {
        var record = new FailedRecord("r1", "orders", 0, new Exception(), FailureKind.Data);
        record.Kind.Should().Be(FailureKind.Data);
    }
}
