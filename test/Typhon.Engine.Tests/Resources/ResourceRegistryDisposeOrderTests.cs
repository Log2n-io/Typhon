using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression for the engine teardown crash where <see cref="ResourceRegistry.Dispose"/> tore the
/// <see cref="ResourceRegistry.Storage"/> subsystem (ManagedPagedMMF) down before the
/// <see cref="ResourceRegistry.DataEngine"/> subsystem — whose graceful shutdown (final checkpoint +
/// <c>PersistArchetypeState</c> / <c>PersistEngineState</c>, durability rule CX-06) reads Storage,
/// Durability and Synchronization. <see cref="ResourceNode"/> keeps children in a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>, so a bare
/// <c>Root.Dispose()</c> cascades in unspecified order; <see cref="ResourceRegistry.Dispose"/> must
/// impose an explicit dependents-first teardown.
/// </summary>
[TestFixture]
public sealed class ResourceRegistryDisposeOrderTests
{
    /// <summary>A leaf resource node that appends its id to a shared list when disposed.</summary>
    private sealed class DisposeRecordingNode : ResourceNode
    {
        private readonly List<string> _log;

        public DisposeRecordingNode(string id, IResource parent, List<string> log)
            : base(id, ResourceType.Node, parent)
            => _log = log;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _log.Add(Id);
            }
            base.Dispose(disposing);
        }
    }

    [Test]
    public void Dispose_TearsDownDataEngineBeforeStorage()
    {
        var order = new List<string>();
        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "DisposeOrderTest" });
        _ = new DisposeRecordingNode("storage-leaf", registry.Storage, order);
        _ = new DisposeRecordingNode("dataengine-leaf", registry.DataEngine, order);

        registry.Dispose();

        Assert.That(order, Does.Contain("dataengine-leaf"));
        Assert.That(order, Does.Contain("storage-leaf"));
        Assert.That(
            order.IndexOf("dataengine-leaf"), Is.LessThan(order.IndexOf("storage-leaf")),
            "the DataEngine subsystem must tear down before Storage — its graceful shutdown reads the MMF (CX-06)");
    }

    [Test]
    public void Dispose_TearsDownEngineBeforeItsDurabilityAndSynchronizationDependencies()
    {
        // The engine's graceful shutdown also reads Durability (WAL) and Synchronization (EpochManager —
        // PersistArchetypeState enters an EpochGuard), so both must outlive the DataEngine teardown.
        var order = new List<string>();
        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "DisposeOrderTest2" });
        _ = new DisposeRecordingNode("engine", registry.DataEngine, order);
        _ = new DisposeRecordingNode("wal", registry.Durability, order);
        _ = new DisposeRecordingNode("epoch", registry.Synchronization, order);

        registry.Dispose();

        Assert.That(order.IndexOf("engine"), Is.LessThan(order.IndexOf("wal")));
        Assert.That(order.IndexOf("engine"), Is.LessThan(order.IndexOf("epoch")));
    }
}
