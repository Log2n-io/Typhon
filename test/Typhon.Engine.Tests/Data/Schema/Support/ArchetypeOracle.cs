using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Snapshots an archetype's entities per component NAME (design 07 §11): for each entity and each of its components, the raw bytes, the enabled bit of the
/// record, presence (a Versioned slot's chain root) and the enabled bit of the cluster word. Two snapshots compare by name, so they can straddle a
/// migration that moves every slot.
/// </summary>
internal static class ArchetypeOracle
{
    /// <summary>One component of one entity.</summary>
    /// <param name="Mode">Its storage mode.</param>
    /// <param name="Bytes">Its raw storage bytes (<see cref="EntityRef.ReadRaw(int)"/>); empty for a Versioned slot with no content.</param>
    /// <param name="Enabled">The record's enabled bit.</param>
    /// <param name="Present">False only for a Versioned slot with no chain (STAGE-02 absent).</param>
    /// <param name="ClusterBit">The cluster word's enabled bit (ENABLE-01: the second copy).</param>
    internal readonly record struct Entry(StorageMode Mode, byte[] Bytes, bool Enabled, bool Present, bool ClusterBit);

    /// <summary>Per entity (raw id), per component name, its <see cref="Entry"/>.</summary>
    public static unsafe Dictionary<long, Dictionary<string, Entry>> Snapshot(DatabaseEngine dbe, ushort archetypeId, IEnumerable<EntityId> ids)
    {
        var state = dbe._archetypeStates[archetypeId];
        var cs = state.ClusterState;
        var layout = cs.Layout;
        var locations = ClusterPlacementProbe.Locations(dbe, archetypeId);
        var result = new Dictionary<long, Dictionary<string, Entry>>();

        using var tx = dbe.CreateQuickTransaction();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            foreach (var id in ids)
            {
                var raw = (long)id.RawValue;
                Assert.That(locations.TryGetValue(raw, out var location), Is.True, $"entity 0x{raw:X} is placed in the archetype's clusters");
                var clusterBase = accessor.GetChunkAddress(location.Chunk);
                var e = tx.Open(id);
                var components = new Dictionary<string, Entry>(StringComparer.Ordinal);
                for (var slot = 0; slot < e.ComponentCount; slot++)
                {
                    var table = state.SlotToComponentTable[slot];
                    var bit = (*(ulong*)(clusterBase + layout.EnabledBitsOffset(slot)) & (1UL << location.Slot)) != 0;
                    var present = table.StorageMode != StorageMode.Versioned || !e.IsVersionedSlotAbsent((byte)slot);
                    components[e.GetComponentName(slot)] = new Entry(table.StorageMode, e.ReadRaw(slot).ToArray(), e.IsEnabled((byte)slot), present, bit);
                }

                result[raw] = components;
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Every component <paramref name="before"/> holds reads back in <paramref name="after"/> identical, by name: bytes, enabled bit (record and cluster word)
    /// and presence.
    /// </summary>
    public static void AssertSurvivorsEqual(Dictionary<long, Dictionary<string, Entry>> before, Dictionary<long, Dictionary<string, Entry>> after)
    {
        var failures = new List<string>();
        foreach (var (entity, components) in before)
        {
            if (!after.TryGetValue(entity, out var afterComponents))
            {
                failures.Add($"entity 0x{entity:X}: missing after the migration");
                continue;
            }

            foreach (var (name, b) in components)
            {
                if (!afterComponents.TryGetValue(name, out var a))
                {
                    failures.Add($"entity 0x{entity:X} {name}: missing after the migration");
                    continue;
                }

                if (!a.Bytes.AsSpan().SequenceEqual(b.Bytes) || a.Enabled != b.Enabled || a.Present != b.Present || a.ClusterBit != b.ClusterBit)
                {
                    failures.Add($"entity 0x{entity:X} {name}: before {Describe(b)}, after {Describe(a)}");
                }
            }
        }

        Assert.That(failures, Is.Empty, $"{failures.Count} component(s) did not survive by name; first: {First(failures)}");
    }

    /// <summary>
    /// Component <paramref name="name"/> is ABSENT on every entity of <paramref name="after"/>, in its STAGE-02 state: bit clear in the record and the cluster
    /// word; a Versioned slot has no chain, a SingleVersion slot zero bytes.
    /// </summary>
    public static void AssertAbsent(Dictionary<long, Dictionary<string, Entry>> after, string name)
    {
        var failures = new List<string>();
        foreach (var (entity, components) in after)
        {
            if (!components.TryGetValue(name, out var a))
            {
                failures.Add($"entity 0x{entity:X}: has no component {name}");
                continue;
            }

            var bytesZero = a.Bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;
            var absent = !a.Enabled && !a.ClusterBit && a.Mode switch
            {
                StorageMode.Versioned => !a.Present,
                StorageMode.SingleVersion => bytesZero,
                _ => true,
            };
            if (!absent)
            {
                failures.Add($"entity 0x{entity:X} {name}: {Describe(a)}");
            }
        }

        Assert.That(failures, Is.Empty, $"{failures.Count} entit(ies) do not have {name} absent; first: {First(failures)}");
    }

    /// <summary>
    /// The offline integrity scan of the bundle at <paramref name="bundle"/> finds nothing (AC-24): the cluster layout rebuilt from the catalog
    /// (<c>ClusterLayoutReader</c>), the cross-structure checks, the EntityMaps, the chains and the indexes all agree with the migrated file.
    /// </summary>
    /// <remarks>
    /// One finding is excused, and only when the file went through a schema commit: CHK-SEG-02's phantom direction for segments the commit freed. The
    /// scanner discovers segments PHYSICALLY (a page flagged as a root whose directory names itself), and <c>ManagedPagedMMF.DeleteSegment</c> frees a
    /// segment's pages without clearing its root page's flag, so a freed segment still reads as one whose pages the bitmap calls free. Measured on a plain
    /// field migration as well (80 pages: the superseded component segments and the intent), so it is the commit's segment release, not the set migration.
    /// </remarks>
    public static void AssertIntegrityClean(string bundle, string when, bool excuseFreedSegmentRoots = true)
    {
        using var source = new OfflineBundlePageSource(bundle);
        var report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Deep });
        var findings = new List<string>();
        foreach (var f in report.Findings)
        {
            if (excuseFreedSegmentRoots && f.Code == "CHK-SEG-02" && f.Severity == IntegritySeverity.Divergence)
            {
                continue;
            }

            findings.Add($"{f.Code}: {f.Summary}");
        }

        Assert.That(findings, Is.Empty, $"{when}:{Environment.NewLine}" + IntegrityReportText.Render(report));
    }

    private static string First(List<string> failures) => string.Join(" | ", failures.GetRange(0, Math.Min(5, failures.Count)));

    private static string Describe(in Entry e) =>
        $"[{e.Mode} enabled={e.Enabled} cluster={e.ClusterBit} present={e.Present} bytes={Convert.ToHexString(e.Bytes)}]";
}
