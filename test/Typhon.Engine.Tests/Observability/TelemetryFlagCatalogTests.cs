using System.Linq;
using NUnit.Framework;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Validates the source-generated runtime catalog (<see cref="TelemetryFlagCatalog"/>, Feature #522 / T3):
/// it enumerates every flag in tree order and faithfully records the exact field names, kinds and the three
/// intentional exceptions (default-true un-gated gauges; composite/subtree roots default off).
/// </summary>
[TestFixture]
public class TelemetryFlagCatalogTests
{
    [Test]
    public void Catalog_enumerates_all_nodes_root_first()
    {
        Assert.That(TelemetryFlagCatalog.Prefix, Is.EqualTo("Typhon:Profiler"));
        // 224 keyed flags (1 master + 4 composite + 4 raw-leaf + 215 subtree) + 8 pure grouping nodes.
        Assert.That(TelemetryFlagCatalog.All.Count, Is.EqualTo(232));

        var root = TelemetryFlagCatalog.All[0];
        Assert.That(root.Kind, Is.EqualTo(TelemetryFlagKind.Master));
        Assert.That(root.Field, Is.EqualTo("ProfilerActive"));
        Assert.That(root.ParentIndex, Is.EqualTo(-1));
    }

    [Test]
    public void Catalog_preserves_the_three_intentional_exceptions()
    {
        // (1) A raw-leaf gauge: read raw, NOT parent-gated, default TRUE (firehose opt-out).
        var gauge = TelemetryFlagCatalog.All.Single(d => d.Field == "SchedulerTrackTransitionLatency");
        Assert.That(gauge.Kind, Is.EqualTo(TelemetryFlagKind.RawLeaf));
        Assert.That(gauge.Default, Is.True);

        // (2) A composite: master's direct opt-in child, default OFF.
        var comp = TelemetryFlagCatalog.All.Single(d => d.Field == "ProfilerGcTracingActive");
        Assert.That(comp.Kind, Is.EqualTo(TelemetryFlagKind.CompositeActive));
        Assert.That(comp.Default, Is.False);

        // (3) A subtree leaf, with the original irregular acronym casing preserved verbatim.
        var leaf = TelemetryFlagCatalog.All.Single(d => d.Field == "DataMvccChainWalkActive");
        Assert.That(leaf.Kind, Is.EqualTo(TelemetryFlagKind.SubtreeResolved));
        Assert.That(leaf.Key, Is.EqualTo("Typhon:Profiler:Data:MVCC:ChainWalk:Enabled"));
    }

    [Test]
    public void Catalog_children_nav_links_master_to_its_children()
    {
        var kids = TelemetryFlagCatalog.Children(0).Select(i => TelemetryFlagCatalog.All[i].Name).ToList();
        Assert.That(kids, Does.Contain("Concurrency"));   // a subtree root
        Assert.That(kids, Does.Contain("GcTracing"));     // a composite
        Assert.That(kids, Does.Contain("Scheduler"));     // the hybrid subtree root
    }
}
