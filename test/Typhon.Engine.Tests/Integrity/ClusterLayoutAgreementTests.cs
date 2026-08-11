using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The re-derived cluster layout, against the one the engine computes.
/// </summary>
/// <remarks>
/// <para>
/// <c>ClusterSize</c> is <b>not</b> persisted — <c>09 §1.1</c> claimed it was, alongside <c>ComponentCount</c>, and
/// only the second half was true. It does not need to be: the engine's selector is a pure function of a fixed header
/// size and a per-entity size, and both come out of the manifest. But "derived rather than read" is exactly the
/// situation where a decoder stays plausible while addressing the wrong bytes, so the derivation is pinned against the
/// engine's own answer rather than trusted.
/// </para>
/// <para>
/// The sweep is over shapes, not over one schema. Component count and component sizes are what the selector scores on,
/// and its result changes discontinuously as the aligned stride crosses a page-division boundary — so the cases are
/// chosen around those discontinuities rather than for tidiness.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ClusterLayoutAgreementTests
{
    /// <summary>
    /// Across many archetype shapes, the derived slot count and offsets equal the engine's.
    /// </summary>
    [Test]
    public void DerivedLayoutMatchesTheEngineAcrossManyShapes()
    {
        var sizes = new[] { 1, 2, 4, 7, 8, 12, 16, 24, 32, 48, 64, 96, 128, 200, 256, 400, 512, 900 };
        var compared = 0;

        foreach (var componentCount in new[] { 1, 2, 3, 5, 8, 16 })
        {
            foreach (var size in sizes)
            {
                foreach (var multiFields in new[] { 0, 1, 3 })
                {
                    var componentSizes = new int[componentCount];
                    for (var i = 0; i < componentCount; i++)
                    {
                        // Vary sizes within an archetype rather than repeating one — a derivation that summed wrongly
                        // but symmetrically would agree on uniform shapes and diverge on real ones.
                        componentSizes[i] = sizes[(Array.IndexOf(sizes, size) + i) % sizes.Length];
                    }

                    ArchetypeClusterInfo engine;
                    try
                    {
                        engine = ArchetypeClusterInfo.Compute(componentCount, componentSizes, multiFields);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;   // too large to cluster; the reader returns null for the same reason
                    }

                    var fixedHeader = 8 + (8 * componentCount);
                    var perEntity = 8 + (multiFields * sizeof(int));
                    foreach (var s in componentSizes)
                    {
                        perEntity += s;
                    }

                    var derivedSize = ArchetypeClusterInfo.SelectClusterSize(fixedHeader, perEntity);

                    Assert.That(derivedSize, Is.EqualTo(engine.ClusterSize),
                        $"componentCount={componentCount} sizes=[{string.Join(",", componentSizes)}] multi={multiFields}");

                    var offset = fixedHeader + (8 * derivedSize);
                    for (var slot = 0; slot < componentCount; slot++)
                    {
                        Assert.That(offset, Is.EqualTo(engine.ComponentOffset(slot)),
                            $"component offset for slot {slot}, componentCount={componentCount}, multi={multiFields}");
                        offset += componentSizes[slot] * derivedSize;
                    }

                    var derivedStride = ArchetypeClusterInfo.AlignStride(
                        offset + (multiFields * derivedSize * sizeof(int)));
                    Assert.That(derivedStride, Is.EqualTo(engine.ClusterStride),
                        $"stride, componentCount={componentCount}, multi={multiFields}");

                    compared++;
                }
            }
        }

        Assert.That(compared, Is.GreaterThan(200), "the sweep must actually cover a range of shapes");
    }
}

/// <summary>
/// The layout the reader derives from a real bundle's manifest agrees with the stride that bundle records.
/// </summary>
/// <remarks>
/// The sweep above proves the arithmetic. This proves the <i>inputs</i> are recovered correctly from disk — component
/// count, per-component sizes, and the multi-value indexed field count — which is the half that a manifest-decode bug
/// would break while the arithmetic stayed perfect.
/// </remarks>
[TestFixture]
internal sealed class ClusterLayoutFromManifestTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void TheDerivedStrideMatchesWhatTheSegmentRecords()
    {
        BuildIndexedDatabase();

        using var source = new OfflineBundlePageSource(BundlePath);
        var roots = new List<int>();
        var page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < source.PageCount; p++)
        {
            if (source.TryReadPage(p, page) && (PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) != 0
                && MemoryMarshal.Read<int>(PageImage.RawData(page)) == p)
            {
                roots.Add(p);
            }
        }

        var manifest = new SchemaCatalogReader(source, roots);
        manifest.Read(BootstrapReader.Read(source));
        Assert.That(manifest.IsUsable, Is.True);

        var checkedAny = 0;
        foreach (var archetype in manifest.Archetypes.Values)
        {
            if (archetype.ClusterSegmentRoot == 0)
            {
                continue;
            }

            var layout = ClusterLayoutReader.TryDerive(manifest, archetype);
            Assert.That(layout, Is.Not.Null, $"the layout for '{archetype.Name}' could not be derived");

            Assert.That(source.TryReadPage(archetype.ClusterSegmentRoot, page), Is.True);
            var geometry = ChunkGeometry.FromPage(page);
            Assert.That(geometry.IsUsable, Is.True);

            Assert.That(layout.Stride, Is.EqualTo(geometry.Stride),
                $"'{archetype.Name}': the derived stride disagrees with the one its segment records, so the slot count "
                + "behind it is wrong too — and every component offset with it");

            Assert.That(layout.ClusterSize, Is.InRange(8, 64));
            Assert.That(layout.EntityKeysOffset + (layout.ClusterSize * 8), Is.LessThanOrEqualTo(geometry.Stride),
                "the entity-key array must fit inside the cluster");

            checkedAny++;
        }

        Assert.That(checkedAny, Is.GreaterThan(0));
    }
}
