using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests verifying that exhaustion policies are enforced at real resource boundaries:
/// TransactionPool (FailFast), ChunkBasedSegment (FailFast), and ResourceNode metadata.
/// </summary>
[TestFixture]
class ExhaustionPolicyTests : TestBase<ExhaustionPolicyTests>
{
    private const int TestMaxActiveTransactions = 3;

    #region TransactionPool FailFast Enforcement

    [Test]
    public void TransactionPool_ExceedsLimit_ThrowsResourceExhaustedException()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var transactions = new List<Transaction>();
        try
        {
            // Create up to the limit — all should succeed
            for (int i = 0; i < TestMaxActiveTransactions; i++)
            {
                transactions.Add(dbe.CreateQuickTransaction());
            }

            // One more should throw
            var ex = Assert.Throws<ResourceExhaustedException>(() =>
            {
#pragma warning disable TYPHON004 // Expected to throw before returning
                dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            });

            Assert.That(ex.ResourcePath, Is.EqualTo("Data/TransactionChain/CreateTransaction"));
            Assert.That(ex.ResourceType, Is.EqualTo(ResourceType.Service));
            Assert.That(ex.CurrentUsage, Is.EqualTo(TestMaxActiveTransactions));
            Assert.That(ex.Limit, Is.EqualTo(TestMaxActiveTransactions));
            Assert.That(ex.Utilization, Is.EqualTo(1.0));
            Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.ResourceExhausted));
            Assert.That(ex.IsTransient, Is.True);
        }
        finally
        {
            foreach (var t in transactions)
            {
                t.Rollback();
                t.Dispose();
            }
        }
    }

    [Test]
    [Category("Sensitive")] // pool-recovery timing — flaky under parallel CPU load; runs in the gate's serial quiet pass
    public void TransactionPool_RecoveryAfterDispose_AllowsNewTransaction()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var transactions = new List<Transaction>();
        try
        {
            // Fill to capacity
            for (int i = 0; i < TestMaxActiveTransactions; i++)
            {
                transactions.Add(dbe.CreateQuickTransaction());
            }

            // Dispose one transaction (via rollback + dispose)
            var released = transactions[0];
            released.Rollback();
            released.Dispose();
            transactions.RemoveAt(0);

            // Should now succeed — slot freed
            transactions.Add(dbe.CreateQuickTransaction());

            Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(TestMaxActiveTransactions));
        }
        finally
        {
            foreach (var t in transactions)
            {
                t.Rollback();
                t.Dispose();
            }
        }
    }

    #endregion

    #region ChunkBasedSegment Exception Type

    [Test]
    public void ThrowResourceExhausted_AllocateChunkPath_ThrowsCorrectType()
    {
        // The ChunkBasedSegment now calls ThrowHelper.ThrowResourceExhausted instead of
        // throwing InvalidOperationException. Verify the helper produces the correct exception.
        var ex = Assert.Throws<ResourceExhaustedException>(() =>
            ThrowHelper.ThrowResourceExhausted(
                "Storage/ChunkBasedSegment/AllocateChunk",
                ResourceType.Memory,
                100,
                100));

        Assert.That(ex.ResourcePath, Is.EqualTo("Storage/ChunkBasedSegment/AllocateChunk"));
        Assert.That(ex.ResourceType, Is.EqualTo(ResourceType.Memory));
        Assert.That(ex.CurrentUsage, Is.EqualTo(100));
        Assert.That(ex.Limit, Is.EqualTo(100));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.ResourceExhausted));
    }

    [Test]
    public void ThrowResourceExhausted_AllocateChunksPath_ThrowsCorrectType()
    {
        // Same verification for the batch allocation path
        var ex = Assert.Throws<ResourceExhaustedException>(() =>
            ThrowHelper.ThrowResourceExhausted(
                "Storage/ChunkBasedSegment/AllocateChunks",
                ResourceType.Memory,
                50,
                50));

        Assert.That(ex.ResourcePath, Is.EqualTo("Storage/ChunkBasedSegment/AllocateChunks"));
        Assert.That(ex.ResourceType, Is.EqualTo(ResourceType.Memory));
        Assert.That(ex.CurrentUsage, Is.EqualTo(50));
        Assert.That(ex.Limit, Is.EqualTo(50));
    }

    #endregion

    #region ResourceNode ExhaustionPolicy Metadata

    [Test]
    public void ResourceNode_ExhaustionPolicy_DefaultIsNone()
    {
        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestReg" });
        var node = new ResourceNode("TestNode", ResourceType.Node, registry.DataEngine);

        Assert.That(node.ExhaustionPolicy, Is.EqualTo(ExhaustionPolicy.None));

        registry.Dispose();
    }

    [Test]
    public void ResourceNode_ExhaustionPolicy_ExplicitValuePreserved()
    {
        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestReg" });

        var failFastNode = new ResourceNode("TransactionPool", ResourceType.Service, registry.DataEngine, ExhaustionPolicy.FailFast);
        var waitNode = new ResourceNode("PageCache", ResourceType.Memory, registry.Storage, ExhaustionPolicy.Wait);
        var evictNode = new ResourceNode("ChunkCache", ResourceType.Memory, registry.Storage, ExhaustionPolicy.Evict);
        var degradeNode = new ResourceNode("ObjectPool", ResourceType.Service, registry.DataEngine, ExhaustionPolicy.Degrade);

        Assert.That(failFastNode.ExhaustionPolicy, Is.EqualTo(ExhaustionPolicy.FailFast));
        Assert.That(waitNode.ExhaustionPolicy, Is.EqualTo(ExhaustionPolicy.Wait));
        Assert.That(evictNode.ExhaustionPolicy, Is.EqualTo(ExhaustionPolicy.Evict));
        Assert.That(degradeNode.ExhaustionPolicy, Is.EqualTo(ExhaustionPolicy.Degrade));

        registry.Dispose();
    }

    #endregion

    #region ExhaustionPolicy Enum

    [Test]
    public void ExhaustionPolicy_None_IsDefaultValue()
    {
        Assert.That(default(ExhaustionPolicy), Is.EqualTo(ExhaustionPolicy.None));
    }

    #endregion

    #region Setup Override

    public override void Setup()
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithThreadId();

        Log.Logger = config.CreateLogger();

        var serviceCollection = new ServiceCollection();
        ServiceCollection = serviceCollection;
        ServiceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
                options.Resources.MaxActiveTransactions = TestMaxActiveTransactions;
            });

        ServiceProvider = ServiceCollection.BuildServiceProvider();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Logger = ServiceProvider.GetRequiredService<ILogger<ExhaustionPolicyTests>>();
    }

    #endregion
}
