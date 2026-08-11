using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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

        report.Add($"sizeof(ComponentR1) = {Unsafe.SizeOf<ComponentR1>()}");
        report.Add($"sizeof(ArchetypeR1) = {Unsafe.SizeOf<ArchetypeR1>()}");
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
        Assert.That(catalogGeometry.Stride, Is.EqualTo(Unsafe.SizeOf<ComponentR1>()),
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
        Assert.That(selfRow.CompSize, Is.EqualTo(Unsafe.SizeOf<ComponentR1>()),
            "the catalog must contain its own definition, and it must agree with the stride:\n  " + string.Join("\n  ", report));

        // Names decode inline. String64 is a `fixed byte[64]` of UTF-8, not a handle into the string table — reading it
        // needs no indirection at all, which is one fewer thing between an offline reader and the manifest.
        var byName = new Dictionary<string, ComponentR1>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            byName[r.Name.AsString ?? ""] = r;
        }

        Assert.That(byName.Keys, Does.Contain(ComponentR1.SchemaName).And.Contain(ArchetypeR1.SchemaName),
            "the manifest must name itself and the archetype catalog:\n  " + string.Join("\n  ", report));

        // The archetype catalog, reached by NAME rather than by a revision number that happened to be unique.
        var archetypeRow = byName[ArchetypeR1.SchemaName];
        Assert.That(archetypeRow.ComponentSPI, Is.Not.Zero);
        Assert.That(roots, Does.Contain(archetypeRow.ComponentSPI));

        // THE TRAP, and it is not hypothetical: ArchetypeR1's persisted row is 108 bytes while the CLR struct is 112.
        // The engine stores the component's own size; the CLR rounds the type up to its alignment, and `long
        // NextEntityKey` forces that to a multiple of 8. So `MemoryMarshal.Read<ArchetypeR1>(chunk)` over a 108-byte
        // chunk reads four bytes it does not own — the next row's, or past the page's raw-data area on the last chunk of
        // a page, where it throws instead. An offline reader must copy the persisted bytes into a full-size buffer.
        //
        // ComponentR1 hides this: its 160 bytes already land on an 8-boundary, so stride and struct size agree and a
        // reader built and tested only against the component catalog works right up until it meets the archetype one.
        Assert.That(archetypeRow.CompSize, Is.LessThanOrEqualTo(Unsafe.SizeOf<ArchetypeR1>()),
            "the persisted row cannot be LARGER than the struct a reader slices with — that would mean the reader is "
            + "silently dropping fields:\n  " + string.Join("\n  ", report));

        // ── The payoff: ComponentCount and NextEntityKey, read from the file ──────────────────────────────────────
        var archetypes = ReadArchetypeRows(source, walker, archetypeRow.ComponentSPI, report);

        Assert.That(archetypes, Is.Not.Empty,
            "no archetype row decoded, so ComponentCount is still out of reach:\n  " + string.Join("\n  ", report));

        foreach (var a in archetypes)
        {
            Assert.That(a.ComponentCount, Is.GreaterThan(0),
                $"archetype '{a.Name.AsString}' reports no components:\n  " + string.Join("\n  ", report));

            // NextEntityKey is the allocator watermark ALO-02 compares against, and every key the cluster holds must
            // sit below it. Reading it here is what makes that check possible offline.
            Assert.That(a.NextEntityKey, Is.GreaterThan(0),
                $"archetype '{a.Name.AsString}' has no entity-key watermark:\n  " + string.Join("\n  ", report));

            if (a.ClusterSegmentSPI != 0)
            {
                Assert.That(roots, Does.Contain(a.ClusterSegmentSPI),
                    $"archetype '{a.Name.AsString}' names a cluster segment that does not exist");
            }

            if (a.EntityMapSPI != 0)
            {
                Assert.That(roots, Does.Contain(a.EntityMapSPI),
                    $"archetype '{a.Name.AsString}' names an EntityMap segment that does not exist");
            }
        }

        // Guard the conclusion itself. If a later change stops persisting these, this is the test that says so before
        // seven cross-structure checks quietly start reporting nothing.
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(5),
            "the self-describing manifest lost rows; the cross-structure checks read it:\n  " + string.Join("\n  ", report));
    }

    /// <summary>Reads every allocated chunk of the archetype catalog's segment as an <see cref="ArchetypeR1"/> row.</summary>
    private static List<ArchetypeR1> ReadArchetypeRows(OfflineBundlePageSource source, SegmentWalker walker, int root,
        List<string> report)
    {
        var seg = walker.WalkSegment(root);
        var page = new byte[IntegrityConstants.PageSize];
        source.TryReadPage(root, page);
        var geometry = ChunkGeometry.FromPage(page);

        report.Add($"archetype catalog @{root}: stride {geometry.Stride}, {seg.Pages.Count} pages");

        var rows = new List<ArchetypeR1>();
        var capacity = geometry.Capacity(seg.Pages.Count);

        for (var id = 0; id < capacity; id++)
        {
            if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= seg.Pages.Count)
            {
                continue;
            }

            if (!source.TryReadPage(seg.Pages[ordinal], page) || !geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var chunk = new ReadOnlySpan<byte>(page, geometry.OffsetInPage(ordinal, chunkInPage), geometry.Stride);
            var row = ReadRow<ArchetypeR1>(chunk);
            var name = row.Name.AsString;
            if (string.IsNullOrEmpty(name))
            {
                continue;   // the reserved sentinel chunk
            }

            rows.Add(row);
            report.Add($"  archetype '{name}': components={row.ComponentCount} routingId={row.RoutingId} "
                + $"nextEntityKey={row.NextEntityKey} clusterSPI={row.ClusterSegmentSPI} mapSPI={row.EntityMapSPI} "
                + $"idxSPI={row.ClusterIndexSPI}");
        }

        return rows;
    }

    /// <summary>
    /// Decodes one persisted row, tolerating a chunk that is <b>smaller</b> than the CLR struct.
    /// </summary>
    /// <remarks>
    /// The engine persists a component's own size; the CLR rounds its type up to the alignment of its widest field. When
    /// those differ — <c>ArchetypeR1</c> is 108 on disk and 112 in memory — a direct
    /// <see cref="MemoryMarshal.Read{T}(ReadOnlySpan{byte})"/> reads past the row it was handed. Copying into a
    /// zero-filled buffer of the struct's own size is the only form that is correct in both directions.
    /// </remarks>
    private static T ReadRow<T>(ReadOnlySpan<byte> chunk) where T : unmanaged
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
        buffer.Clear();
        chunk[..Math.Min(chunk.Length, buffer.Length)].CopyTo(buffer);
        return MemoryMarshal.Read<T>(buffer);
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
            var row = ReadRow<ComponentR1>(chunk);

            // Chunk 0 is the segment's reserved null sentinel and decodes as all-zero. Skipping it on CompSize rather
            // than on the id keeps this honest about what it is filtering.
            if (row.CompSize == 0)
            {
                continue;
            }

            rows.Add(row);
            report.Add($"  row chunk {id}: '{row.Name.AsString}' CompSize={row.CompSize} overhead={row.CompOverhead} "
                + $"fields={row.FieldCount} compSPI={row.ComponentSPI} verSPI={row.VersionSPI} rev={row.SchemaRevision}");
        }

        return rows;
    }
}
