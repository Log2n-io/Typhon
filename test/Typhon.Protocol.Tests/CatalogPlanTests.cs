using System;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Compilation is the decoder's trust boundary (W33): a count sizes its stack buffers, so the plan bounds it whether or not the catalog went through the
/// validator. Each test compiles a catalog the validator would refuse, skipping it.
/// </summary>
[TestFixture]
public class CatalogPlanTests
{
    /// <summary>A metric's values decode into a buffer of one value per component: a count there is refused, not an overflow.</summary>
    [Test]
    public void AMetricCarryingACountIsRefused()
    {
        var catalog = Canonical();
        var i = Array.FindIndex(catalog.Metrics, m => m.Name == "app.load");
        var m = catalog.Metrics[i];
        catalog.Metrics[i] = new CatalogMetric
        {
            Idx = m.Idx, Name = m.Name, Unit = m.Unit, Scope = m.Scope, Kind = m.Kind, Labels = m.Labels,
            Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8, Count = 5 },
        };

        var ex = Assert.Throws<CatalogException>(() => CatalogPlan.Compile(catalog));
        Assert.That(ex.Message, Does.Contain("app.load").And.Contain("count 5"));
    }

    /// <summary>A list element is one value: a count on it would multiply the list's buffer, so it is refused.</summary>
    [Test]
    public void AListElementCarryingACountIsRefused()
    {
        var catalog = Canonical();
        SetPingPath(catalog, new CatalogCodec { Kind = CodecKind.U8, Count = 16 });

        var ex = Assert.Throws<CatalogException>(() => CatalogPlan.Compile(catalog));
        Assert.That(ex.Message, Does.Contain("path").And.Contain("count 16"));
    }

    /// <summary>A count of 0 is absent, as the validator reads it: one value, the same plan every SDK compiles.</summary>
    [Test]
    public void ACountOfZeroIsOneValue()
    {
        var catalog = Canonical();
        SetPingPath(catalog, new CatalogCodec { Kind = CodecKind.U8, Count = 0 });

        var path = Array.Find(CatalogPlan.Compile(catalog).EventByName("Ping").Body.Fields, f => f.Name == "path");
        Assert.Multiple(() =>
        {
            Assert.That(path.Element.Count, Is.EqualTo(1));
            Assert.That(path.Element.Components, Is.EqualTo(1));
        });
    }

    private static Catalog Canonical() => CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink());

    private static void SetPingPath(Catalog catalog, CatalogCodec element)
    {
        var ping = Array.Find(catalog.Events, e => e.Name == "Ping");
        var i = Array.FindIndex(ping.Fields, f => f.Name == "path");
        ping.Fields[i] = new CatalogField
        {
            Name = "path", Codec = new CatalogCodec { Kind = CodecKind.List, Of = element, MinCount = 0, MaxCount = 4 },
        };
    }
}
