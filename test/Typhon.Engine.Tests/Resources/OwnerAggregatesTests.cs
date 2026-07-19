using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the Owner Aggregates pattern where ComponentTable aggregates metrics
/// from its internal segments rather than exposing them as separate graph nodes.
/// </summary>
[TestFixture]
class OwnerAggregatesTests : TestBase<OwnerAggregatesTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    #region Test Infrastructure

    /// <summary>
    /// Test spy that captures all metric values written to it.
    /// </summary>
    private class TestMetricWriter : IMetricWriter
    {
        public long? CapacityCurrent { get; private set; }
        public long? CapacityMaximum { get; private set; }
        public bool CapacityWritten => CapacityCurrent.HasValue;

        public long? MemoryAllocatedBytes { get; private set; }
        public long? MemoryPeakBytes { get; private set; }

        public long? DiskReadOps { get; private set; }
        public long? DiskWriteOps { get; private set; }
        public long? DiskReadBytes { get; private set; }
        public long? DiskWriteBytes { get; private set; }

        public List<(string Name, long Count)> ThroughputCounters { get; } = new();
        public List<(string Name, long LastUs, long AvgUs, long MaxUs)> DurationMetrics { get; } = new();

        public void WriteMemory(long allocatedBytes, long peakBytes)
        {
            MemoryAllocatedBytes = allocatedBytes;
            MemoryPeakBytes = peakBytes;
        }

        public void WriteCapacity(long current, long maximum)
        {
            CapacityCurrent = current;
            CapacityMaximum = maximum;
        }

        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes)
        {
            DiskReadOps = readOps;
            DiskWriteOps = writeOps;
            DiskReadBytes = readBytes;
            DiskWriteBytes = writeBytes;
        }

        public void WriteThroughput(string name, long count) => ThroughputCounters.Add((name, count));

        public void WriteDuration(string name, long lastUs, long avgUs, long maxUs) => DurationMetrics.Add((name, lastUs, avgUs, maxUs));

        public void Reset()
        {
            CapacityCurrent = null;
            CapacityMaximum = null;
            MemoryAllocatedBytes = null;
            MemoryPeakBytes = null;
            DiskReadOps = null;
            DiskWriteOps = null;
            DiskReadBytes = null;
            DiskWriteBytes = null;
            ThroughputCounters.Clear();
            DurationMetrics.Clear();
        }
    }

    #endregion

    #region Interface Implementation Tests

    [Test]
    public void ComponentTable_ImplementsAllInterfaces()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var componentTable = dbe.GetComponentTable<CompA>();

        Assert.That(componentTable, Is.InstanceOf<IResource>(), "ComponentTable should implement IResource");
        Assert.That(componentTable, Is.InstanceOf<IMetricSource>(), "ComponentTable should implement IMetricSource");
        Assert.That(componentTable, Is.InstanceOf<IDebugPropertiesProvider>(), "ComponentTable should implement IDebugPropertiesProvider");
    }

    #endregion

    #region Owner Aggregates Pattern Tests

    [Test]
    public void ComponentTable_SegmentsAreNotChildren()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var componentTable = dbe.GetComponentTable<CompA>();

        // Segments follow the Owner Aggregates pattern - they are NOT graph nodes
        Assert.That(componentTable.Children, Is.Empty,
            "ComponentTable should have no children - segments are aggregated, not children");
    }

    [Test]
    public void ComponentTable_AggregatesSegmentCapacity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var componentTable = dbe.GetComponentTable<CompA>();

        // Calculate expected totals from individual segments
        long expectedAllocated =
            componentTable.ComponentSegment.AllocatedChunkCount +
            componentTable.CompRevTableSegment.AllocatedChunkCount +
            componentTable.DefaultIndexSegment.AllocatedChunkCount +
            (componentTable.String64IndexSegment?.AllocatedChunkCount ?? 0);

        long expectedCapacity =
            componentTable.ComponentSegment.ChunkCapacity +
            componentTable.CompRevTableSegment.ChunkCapacity +
            componentTable.DefaultIndexSegment.ChunkCapacity +
            (componentTable.String64IndexSegment?.ChunkCapacity ?? 0);

        var writer = new TestMetricWriter();
        ((IMetricSource)componentTable).ReadMetrics(writer);

        Assert.That(writer.CapacityWritten, Is.True, "Should write capacity metrics");
        Assert.That(writer.CapacityCurrent, Is.EqualTo(expectedAllocated),
            "Capacity current should be sum of all segment allocations");
        Assert.That(writer.CapacityMaximum, Is.EqualTo(expectedCapacity),
            "Capacity maximum should be sum of all segment capacities");
    }

    [Test]
    public void ComponentTable_AggregatesSegmentCapacity_AfterEntityCreation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var componentTable = dbe.GetComponentTable<CompA>();

        var writer = new TestMetricWriter();
        ((IMetricSource)componentTable).ReadMetrics(writer);
        var initialAllocated = writer.CapacityCurrent;

        // Create some entities to allocate chunks
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var a = new CompA(i);
                t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }
            t.Commit();
        }

        writer.Reset();
        ((IMetricSource)componentTable).ReadMetrics(writer);

        Assert.That(writer.CapacityCurrent, Is.GreaterThan(initialAllocated),
            "Allocated chunks should increase after creating entities");
    }

    #endregion

    #region IDebugPropertiesProvider Tests

    [Test]
    public void ComponentTable_GetDebugProperties_ShowsPerSegmentBreakdown()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var componentTable = dbe.GetComponentTable<CompA>();
        var debugProvider = (IDebugPropertiesProvider)componentTable;

        var props = debugProvider.GetDebugProperties();

        // Verify all expected keys are present
        Assert.That(props.ContainsKey("ComponentSegment.AllocatedChunks"), Is.True);
        Assert.That(props.ContainsKey("ComponentSegment.Capacity"), Is.True);
        Assert.That(props.ContainsKey("ComponentSegment.ChunkSize"), Is.True);

        Assert.That(props.ContainsKey("CompRevTableSegment.AllocatedChunks"), Is.True);
        Assert.That(props.ContainsKey("CompRevTableSegment.Capacity"), Is.True);

        Assert.That(props.ContainsKey("DefaultIndexSegment.AllocatedChunks"), Is.True);
        Assert.That(props.ContainsKey("DefaultIndexSegment.Capacity"), Is.True);
    }

    [Test]
    public void ComponentTable_GetDebugProperties_ValuesMatchSegments()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var componentTable = dbe.GetComponentTable<CompA>();
        var debugProvider = (IDebugPropertiesProvider)componentTable;

        var props = debugProvider.GetDebugProperties();

        Assert.That(props["ComponentSegment.AllocatedChunks"],
            Is.EqualTo(componentTable.ComponentSegment.AllocatedChunkCount));
        Assert.That(props["ComponentSegment.Capacity"],
            Is.EqualTo(componentTable.ComponentSegment.ChunkCapacity));

        Assert.That(props["CompRevTableSegment.AllocatedChunks"],
            Is.EqualTo(componentTable.CompRevTableSegment.AllocatedChunkCount));
        Assert.That(props["CompRevTableSegment.Capacity"],
            Is.EqualTo(componentTable.CompRevTableSegment.ChunkCapacity));

        Assert.That(props["DefaultIndexSegment.AllocatedChunks"],
            Is.EqualTo(componentTable.DefaultIndexSegment.AllocatedChunkCount));
        Assert.That(props["DefaultIndexSegment.Capacity"],
            Is.EqualTo(componentTable.DefaultIndexSegment.ChunkCapacity));
    }

    [Test]
    public void ComponentTable_GetDebugProperties_IncludesString64Segment_WhenPresent()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompC has a String64 field, so it should have a String64IndexSegment
        var componentTable = dbe.GetComponentTable<CompC>();
        var debugProvider = (IDebugPropertiesProvider)componentTable;

        var props = debugProvider.GetDebugProperties();

        // CompC uses String64 but doesn't have an [Index] on it,
        // but String64IndexSegment is always allocated for tables that might need it
        if (componentTable.String64IndexSegment != null)
        {
            Assert.That(props.ContainsKey("String64IndexSegment.AllocatedChunks"), Is.True,
                "Should include String64IndexSegment when present");
            Assert.That(props.ContainsKey("String64IndexSegment.Capacity"), Is.True);
        }
    }

    #endregion

    #region Integration Tests

    [Test]
    public void ComponentTable_MetricsReflectRealUsage()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var componentTable = dbe.GetComponentTable<CompA>();

        // Get baseline metrics
        var writer = new TestMetricWriter();
        ((IMetricSource)componentTable).ReadMetrics(writer);
        var baselineAllocated = writer.CapacityCurrent ?? 0;

        // Create entities to allocate chunks
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new CompA(i);
                t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }
            t.Commit();
        }

        // Get metrics after work
        writer.Reset();
        ((IMetricSource)componentTable).ReadMetrics(writer);

        Assert.That(writer.CapacityCurrent, Is.GreaterThan(baselineAllocated),
            "Allocated chunks should increase after creating 100 entities");

        // Debug properties should match aggregated metrics
        var debugProps = ((IDebugPropertiesProvider)componentTable).GetDebugProperties();

        long debugTotal =
            (int)debugProps["ComponentSegment.AllocatedChunks"] +
            (int)debugProps["CompRevTableSegment.AllocatedChunks"] +
            (int)debugProps["DefaultIndexSegment.AllocatedChunks"];

        // Include String64IndexSegment if present
        if (debugProps.ContainsKey("String64IndexSegment.AllocatedChunks"))
        {
            debugTotal += (int)debugProps["String64IndexSegment.AllocatedChunks"];
        }

        Assert.That(writer.CapacityCurrent, Is.EqualTo(debugTotal),
            "Aggregated capacity should equal sum of debug property values");
    }

    #endregion
}
