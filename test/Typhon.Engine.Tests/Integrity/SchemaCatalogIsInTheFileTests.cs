using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Whether the persisted schema catalogs can be reached from raw bytes — the question <c>09 §1</c> answered wrongly.
/// </summary>
/// <remarks>
/// <para>
/// <c>09-closing-the-gap.md</c> §1 concluded <i>"the schema is not in the file"</i> from
/// <c>DBD = new DatabaseDefinitions()</c> — the runtime definitions really are rebuilt from CLR structs at every open.
/// But that is a statement about how the engine <b>boots</b>, not about what the file <b>records</b>. Alongside those
/// runtime definitions the engine writes a self-describing manifest: <c>ComponentR1</c> (name, <c>CompSize</c>,
/// <c>CompOverhead</c>, field descriptors, segment roots) and <c>ArchetypeR1</c> (<c>ComponentCount</c>,
/// <c>ComponentNames</c> in slot order, <c>NextEntityKey</c>, cluster and index segment roots).
/// </para>
/// <para>
/// If those are reachable offline then three of the plan's conclusions are wrong: G1's unfinished half (cluster size and
/// component count) is unnecessary, MAP/CLU/ALO-02 are not blocked on it, and Tier 2 — the four checks said to need a
/// schema assembly, and G5 with them — may not exist as a tier at all.
/// </para>
/// <para>
/// This measures it rather than arguing from a struct definition, which is exactly the mistake that produced the wrong
/// answer the first time. It reads only bytes: bootstrap, then the component catalog's own segment, then the rows.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SchemaCatalogIsInTheFileTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void TheComponentAndArchetypeCatalogsAreReadableWithNoEngineAndNoSchemaAssembly()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var report = new List<string>();
        using var source = new OfflineBundlePageSource(BundlePath);
        var bootstrap = BootstrapReader.Read(source);

        Assert.That(bootstrap.IsUsable, Is.True, "precondition: the bootstrap must be readable");

        // Everything the bootstrap names, so the report says what IS there rather than only what was looked for.
        for (var i = 0; i < bootstrap.Entries.Count; i++)
        {
            var e = bootstrap.Entries[i];
            var ints = new List<string>();
            for (var c = 0; c < e.Value.IntCount; c++)
            {
                ints.Add(e.Value.GetInt(c).ToString());
            }

            report.Add($"bootstrap[{e.Key}] = [{string.Join(",", ints)}]");
        }

        var walker = new SegmentWalker(source);
        var page = new byte[IntegrityConstants.PageSize];

        // Every chunk-based segment the physical sweep can find, with the stride each one now records. This is the list
        // a cross-structure check would work from.
        var roots = new List<int>();
        for (var p = 0; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page))
            {
                continue;
            }

            if ((PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            if (MemoryMarshal.Read<int>(PageImage.RawData(page)) == p)
            {
                roots.Add(p);
            }
        }

        report.Add($"segments found by physical sweep = {roots.Count}");

        var componentSized = 0;
        foreach (var root in roots)
        {
            var seg = walker.WalkSegment(root);
            source.TryReadPage(root, page);
            var geometry = ChunkGeometry.FromPage(page);
            report.Add($"  segment @{root} kind={seg.Kind} pages={seg.Pages.Count} stride={geometry.Stride}");

            if (geometry.IsUsable)
            {
                componentSized++;
            }
        }

        report.Add($"sizeof(ComponentR1) = {Marshal.SizeOf<ComponentR1>()}");
        report.Add($"sizeof(ArchetypeR1) = {Marshal.SizeOf<ArchetypeR1>()}");
        Assert.That(componentSized, Is.GreaterThan(0), "no chunk-based segment carried a stride");

        // The catalog's own segment is named by the bootstrap — no guessing, no sweep heuristic.
        Assert.That(bootstrap.TryGet("sys.ComponentR1", out var catalogSpi), Is.True,
            "the bootstrap must name the component catalog:\n  " + string.Join("\n  ", report));

        var catalogRoot = catalogSpi.GetInt(0);
        var catalog = walker.WalkSegment(catalogRoot);
        source.TryReadPage(catalogRoot, page);
        var catalogGeometry = ChunkGeometry.FromPage(page);

        report.Add($"catalog segment @{catalogRoot}: stride {catalogGeometry.Stride}, {catalog.Pages.Count} pages");

        // One row per chunk, and the stride IS the row size. That equality is what makes the catalog decodable with no
        // schema knowledge at all: the stride is on the page (revision 7), so the row size is on the page too.
        Assert.That(catalogGeometry.Stride, Is.EqualTo(Marshal.SizeOf<ComponentR1>()),
            "the catalog segment's stride must be the row size, or rows cannot be sliced from it:\n  " + string.Join("\n  ", report));

        var rows = ReadCatalogRows(source, catalog, catalogGeometry, report);

        Assert.That(rows, Is.Not.Empty, "no catalog row decoded:\n  " + string.Join("\n  ", report));

        // Every row's own segment pointers must resolve to segments the physical sweep independently found. This is the
        // cross-check that makes the decode trustworthy rather than merely plausible: a wrong offset would still yield
        // integers, but not integers that agree with a list built by another route entirely.
        var unresolved = new List<string>();
        foreach (var r in rows)
        {
            if (r.ComponentSPI != 0 && !roots.Contains(r.ComponentSPI))
            {
                unresolved.Add($"CompSize={r.CompSize}: ComponentSPI {r.ComponentSPI} is not a segment root");
            }

            if (r.VersionSPI != 0 && !roots.Contains(r.VersionSPI))
            {
                unresolved.Add($"CompSize={r.CompSize}: VersionSPI {r.VersionSPI} is not a segment root");
            }
        }

        Assert.That(unresolved, Is.Empty,
            "a decoded catalog row points at something that is not a segment:\n  " + string.Join("\n  ", unresolved)
            + "\n  " + string.Join("\n  ", report));

        // The catalog describes ITSELF — its own row's ComponentSPI is the segment the row was read from. A decoder
        // reading the wrong offsets could not produce that fixed point by accident.
        var selfRow = rows.Find(r => r.ComponentSPI == catalogRoot);
        Assert.That(selfRow.CompSize, Is.EqualTo(Marshal.SizeOf<ComponentR1>()),
            "the catalog must contain its own definition, and it must agree with the stride:\n  " + string.Join("\n  ", report));

        // And the archetype catalog is reachable from here — the row whose schema revision is 2, matching
        // [Component(SchemaName, 2)] on ArchetypeR1. ArchetypeR1 is what carries ComponentCount and NextEntityKey, the
        // two values 09 §1 recorded as being nowhere in the file.
        var archetypeRow = rows.Find(r => r.SchemaRevision == 2);
        Assert.That(archetypeRow.ComponentSPI, Is.Not.Zero,
            "the archetype catalog's own segment must be named by a component-catalog row:\n  " + string.Join("\n  ", report));
        Assert.That(roots, Does.Contain(archetypeRow.ComponentSPI));

        // Guard the conclusion itself. If a later change stops persisting these, this is the test that says so before
        // seven cross-structure checks quietly start reporting nothing.
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(5),
            "the self-describing manifest lost rows; the cross-structure checks read it:\n  " + string.Join("\n  ", report));
    }

    /// <summary>Reads every allocated chunk of the catalog segment as a <see cref="ComponentR1"/> row.</summary>
    private static List<ComponentR1> ReadCatalogRows(OfflineBundlePageSource source, SegmentView seg, ChunkGeometry geometry,
        List<string> report)
    {
        var rows = new List<ComponentR1>();
        var page = new byte[IntegrityConstants.PageSize];
        var capacity = geometry.Capacity(seg.Pages.Count);

        for (var id = 0; id < capacity; id++)
        {
            if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= seg.Pages.Count)
            {
                continue;
            }

            if (!source.TryReadPage(seg.Pages[ordinal], page))
            {
                continue;
            }

            if (!geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var chunk = new ReadOnlySpan<byte>(page, geometry.OffsetInPage(ordinal, chunkInPage), geometry.Stride);
            var row = MemoryMarshal.Read<ComponentR1>(chunk);

            // Chunk 0 is the segment's reserved null sentinel and decodes as all-zero. Skipping it on CompSize rather
            // than on the id keeps this honest about what it is filtering.
            if (row.CompSize == 0)
            {
                continue;
            }

            rows.Add(row);
            report.Add($"  row chunk {id}: CompSize={row.CompSize} overhead={row.CompOverhead} "
                + $"fields={row.FieldCount} compSPI={row.ComponentSPI} verSPI={row.VersionSPI} rev={row.SchemaRevision}");
        }

        return rows;
    }
}
