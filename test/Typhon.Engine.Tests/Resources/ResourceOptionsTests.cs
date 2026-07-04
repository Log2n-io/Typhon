using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for ResourceOptions, ExhaustionPolicy, ResourceExhaustedException, and FindRootCause.
/// </summary>
[TestFixture]
public class ResourceOptionsTests
{
    #region ResourceOptions Tests

    [Test]
    public void ResourceOptions_DefaultValues_AreSensible()
    {
        var options = new ResourceOptions();

        Assert.That(options.MaxActiveTransactions, Is.EqualTo(1000));
        Assert.That(options.WalRingBufferSizeBytes, Is.EqualTo(8 * 1024 * 1024), "Default WAL ring buffer should be 8 MB");
        Assert.That(options.CheckpointIntervalMs, Is.EqualTo(30000), "Default checkpoint interval should be 30 seconds");
        Assert.That(options.CheckpointBarrierTimeoutMs, Is.EqualTo(30000), "Default checkpoint barrier timeout should be 30 seconds");
        Assert.That(options.PageChecksumVerification, Is.EqualTo(PageChecksumVerification.OnLoad));
    }

    #endregion

    #region ExhaustionPolicy Tests

    [Test]
    public void ExhaustionPolicy_AllValuesExist()
    {
        // Verify all expected policies exist
        Assert.That(Enum.IsDefined(typeof(ExhaustionPolicy), ExhaustionPolicy.None));
        Assert.That(Enum.IsDefined(typeof(ExhaustionPolicy), ExhaustionPolicy.FailFast));
        Assert.That(Enum.IsDefined(typeof(ExhaustionPolicy), ExhaustionPolicy.Wait));
        Assert.That(Enum.IsDefined(typeof(ExhaustionPolicy), ExhaustionPolicy.Evict));
        Assert.That(Enum.IsDefined(typeof(ExhaustionPolicy), ExhaustionPolicy.Degrade));

        // Verify we have exactly 5 policies (None + 4 behavioral)
        var values = Enum.GetValues<ExhaustionPolicy>();
        Assert.That(values, Has.Length.EqualTo(5));
    }

    #endregion

    #region ResourceExhaustedException Tests

    [Test]
    public void ResourceExhaustedException_PropertiesAreSet()
    {
        var ex = new ResourceExhaustedException(
            "DataEngine/TransactionPool",
            ResourceType.Service,
            1000,
            1000);

        Assert.That(ex.ResourcePath, Is.EqualTo("DataEngine/TransactionPool"));
        Assert.That(ex.ResourceType, Is.EqualTo(ResourceType.Service));
        Assert.That(ex.CurrentUsage, Is.EqualTo(1000));
        Assert.That(ex.Limit, Is.EqualTo(1000));
    }

    [Test]
    public void ResourceExhaustedException_UtilizationCalculatedCorrectly()
    {
        var ex = new ResourceExhaustedException(
            "Storage/PageCache",
            ResourceType.Memory,
            750,
            1000);

        Assert.That(ex.Utilization, Is.EqualTo(0.75));
    }

    [Test]
    public void ResourceExhaustedException_UtilizationHandlesZeroLimit()
    {
        var ex = new ResourceExhaustedException(
            "Test/Resource",
            ResourceType.Node,
            100,
            0);  // Zero limit edge case

        Assert.That(ex.Utilization, Is.EqualTo(1.0));
    }

    [Test]
    public void ResourceExhaustedException_MessageFormatted()
    {
        var ex = new ResourceExhaustedException(
            "DataEngine/TransactionPool",
            ResourceType.Service,
            950,
            1000);

        Assert.That(ex.Message, Does.Contain("DataEngine/TransactionPool"));
        Assert.That(ex.Message, Does.Contain("950"));
        Assert.That(ex.Message, Does.Contain("1,000"));
        Assert.That(ex.Message, Does.Contain("95.0%"));
    }

    [Test]
    public void ResourceExhaustedException_CustomMessageConstructor()
    {
        var ex = new ResourceExhaustedException(
            "Custom message here",
            "Test/Path",
            ResourceType.Memory,
            50,
            100);

        Assert.That(ex.Message, Is.EqualTo("Custom message here"));
        Assert.That(ex.ResourcePath, Is.EqualTo("Test/Path"));
    }

    [Test]
    public void ResourceExhaustedException_InnerExceptionConstructor()
    {
        var inner = new InvalidOperationException("Inner error");
        var ex = new ResourceExhaustedException(
            "Outer message",
            inner,
            "Test/Path",
            ResourceType.Node,
            25,
            50);

        Assert.That(ex.Message, Is.EqualTo("Outer message"));
        Assert.That(ex.InnerException, Is.SameAs(inner));
        Assert.That(ex.ResourcePath, Is.EqualTo("Test/Path"));
    }

    #endregion

    #region FindRootCause Tests

    private ResourceRegistry _registry;
    private ResourceGraph _graph;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestRegistry" });
        _graph = new ResourceGraph(_registry);
    }

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    /// <summary>
    /// Test resource with configurable capacity metrics.
    /// </summary>
    private class TestCapacityResource : ResourceNode, IMetricSource
    {
        public long CapacityCurrent;
        public long CapacityMax;

        public TestCapacityResource(string id, IResource parent)
            : base(id, ResourceType.Node, parent) { }

        public void ReadMetrics(IMetricWriter writer)
        {
            if (CapacityMax > 0)
            {
                writer.WriteCapacity(CapacityCurrent, CapacityMax);
            }
        }

        public void ResetPeaks() { }
    }

    [Test]
    public void FindRootCause_ReturnsSymptomNode_WhenNoHighUtilization()
    {
        // Create a resource with low utilization
        var resource = new TestCapacityResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 10,
            CapacityMax = 100  // 10% utilization
        };
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("DataEngine/TransactionPool");

        Assert.That(rootCause, Is.Not.Null);
        Assert.That(rootCause.Id, Is.EqualTo("TransactionPool"));
    }

    [Test]
    public void FindRootCause_ReturnsSymptomNode_WhenNoDependencies()
    {
        // Create a resource with high utilization but no known dependencies
        var resource = new TestCapacityResource("UnknownComponent", _registry.Storage)
        {
            CapacityCurrent = 95,
            CapacityMax = 100  // 95% utilization
        };
        _registry.Storage.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("Storage/UnknownComponent");

        Assert.That(rootCause, Is.Not.Null);
        Assert.That(rootCause.Id, Is.EqualTo("UnknownComponent"));
    }

    [Test]
    public void FindRootCause_TracesWaitChain()
    {
        // Set up the known dependency chain:
        // TransactionPool -> WALRingBuffer -> WALSegments

        var transactionPool = new TestCapacityResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 95,
            CapacityMax = 100  // 95% - under pressure
        };
        _registry.DataEngine.RegisterChild(transactionPool);

        var walRingBuffer = new TestCapacityResource("WALRingBuffer", _registry.Durability)
        {
            CapacityCurrent = 90,
            CapacityMax = 100  // 90% - under pressure
        };
        _registry.Durability.RegisterChild(walRingBuffer);

        var walSegments = new TestCapacityResource("WALSegments", _registry.Durability)
        {
            CapacityCurrent = 50,
            CapacityMax = 100  // 50% - NOT under pressure
        };
        _registry.Durability.RegisterChild(walSegments);

        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("DataEngine/TransactionPool");

        // Should stop at WALRingBuffer (WALSegments is not under pressure)
        Assert.That(rootCause, Is.Not.Null);
        Assert.That(rootCause.Id, Is.EqualTo("WALRingBuffer"));
    }

    [Test]
    public void FindRootCause_TracesFullChain_WhenAllUnderPressure()
    {
        // Set up the chain with ALL nodes under pressure
        var transactionPool = new TestCapacityResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 95,
            CapacityMax = 100
        };
        _registry.DataEngine.RegisterChild(transactionPool);

        var walRingBuffer = new TestCapacityResource("WALRingBuffer", _registry.Durability)
        {
            CapacityCurrent = 92,
            CapacityMax = 100
        };
        _registry.Durability.RegisterChild(walRingBuffer);

        var walSegments = new TestCapacityResource("WALSegments", _registry.Durability)
        {
            CapacityCurrent = 88,
            CapacityMax = 100  // Also under pressure!
        };
        _registry.Durability.RegisterChild(walSegments);

        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("DataEngine/TransactionPool");

        // Should trace all the way to WALSegments (end of chain)
        Assert.That(rootCause, Is.Not.Null);
        Assert.That(rootCause.Id, Is.EqualTo("WALSegments"));
    }

    [Test]
    public void FindRootCause_HandlesMissingNodes()
    {
        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("NonExistent/Path");

        Assert.That(rootCause, Is.Null);
    }

    [Test]
    public void FindRootCause_HandlesRootPrefix()
    {
        var resource = new TestCapacityResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 50,
            CapacityMax = 100
        };
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();

        // Both formats should work
        var result1 = snapshot.FindRootCause("DataEngine/TransactionPool");
        var result2 = snapshot.FindRootCause("Root/DataEngine/TransactionPool");

        Assert.That(result1, Is.Not.Null);
        Assert.That(result2, Is.Not.Null);
        Assert.That(result1.Id, Is.EqualTo(result2.Id));
    }

    [Test]
    public void FindRootCause_CustomThreshold()
    {
        var resource = new TestCapacityResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 70,
            CapacityMax = 100  // 70% utilization
        };
        _registry.DataEngine.RegisterChild(resource);

        var walRingBuffer = new TestCapacityResource("WALRingBuffer", _registry.Durability)
        {
            CapacityCurrent = 65,
            CapacityMax = 100  // 65% utilization
        };
        _registry.Durability.RegisterChild(walRingBuffer);

        var snapshot = _graph.GetSnapshot();

        // With default 80% threshold, should return symptom (neither under pressure)
        var result80 = snapshot.FindRootCause("DataEngine/TransactionPool", 0.8);
        Assert.That(result80.Id, Is.EqualTo("TransactionPool"));

        // With 60% threshold, should trace to WALRingBuffer
        var result60 = snapshot.FindRootCause("DataEngine/TransactionPool", 0.6);
        Assert.That(result60.Id, Is.EqualTo("WALRingBuffer"));
    }

    [Test]
    public void FindRootCause_SelectsMostUtilizedDependency()
    {
        // If a component has multiple dependencies, select the most utilized one
        // Note: Current WaitDependencies only has single dependencies,
        // but the algorithm supports multiple

        var pageCache = new TestCapacityResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 90,
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(pageCache);

        var mmf = new TestCapacityResource("ManagedPagedMMF", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(mmf);

        var snapshot = _graph.GetSnapshot();
        var rootCause = snapshot.FindRootCause("Storage/PageCache");

        // Should trace to ManagedPagedMMF (the dependency)
        Assert.That(rootCause, Is.Not.Null);
        Assert.That(rootCause.Id, Is.EqualTo("ManagedPagedMMF"));
    }

    [Test]
    public void FindRootCause_DefaultThreshold_Is80Percent() => Assert.That(ResourceSnapshot.DefaultHighUtilizationThreshold, Is.EqualTo(0.8));

    #endregion

    #region DatabaseEngineOptions Integration

    [Test]
    public void DatabaseEngineOptions_HasResourceOptionsProperty()
    {
        var options = new DatabaseEngineOptions();

        Assert.That(options.Resources, Is.Not.Null);
        Assert.That(options.Resources, Is.TypeOf<ResourceOptions>());
    }

    [Test]
    public void DatabaseEngineOptions_ResourceOptions_HasDefaults()
    {
        var options = new DatabaseEngineOptions();

        Assert.That(options.Resources.CheckpointIntervalMs, Is.EqualTo(30000));
        Assert.That(options.Resources.MaxActiveTransactions, Is.EqualTo(1000));
    }

    #endregion
}
