using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>CLU</c> and the archetype half of <c>ALO</c> — cluster slot occupancy, entity identity, and the key watermark.
/// </summary>
/// <remarks>
/// <para>
/// These read a cluster's <b>engine-defined prefix</b> and nothing else: the <c>OccupancyBits</c> word at offset 0, and
/// the packed entity-key array that starts immediately after the per-component <c>EnabledBits</c>. That prefix is
/// addressable from two facts — the chunk stride (on the page since revision 7) and the archetype's component count
/// (in <c>ArchetypeR1</c>) — so none of it needs a schema assembly, and none of it needs the per-component field layout
/// that the <c>ComponentNames</c> VSBS collection would have to be walked for.
/// </para>
/// <para>
/// <b>The bound on slot count comes from the occupancy word itself.</b> A cluster holds at most 64 entities because
/// occupancy is one <c>u64</c>; the walk considers only bits that are set, and refuses any slot whose key would fall
/// outside the chunk. That is what lets these checks run without <c>ClusterSize</c>, which is genuinely not on disk.
/// </para>
/// <para>
/// <c>CLU-02</c> and <c>CLU-05</c> are the pair that would have caught <b>#697</b>. <c>RB-06</c>'s own note says why:
/// <i>"an allocator whose counter is restored below its own restored population hands out an identifier that a live
/// recovered object already holds — and the collision is silent, because both sides are individually well-formed."</i>
/// Only a cross-structure check sees it.
/// </para>
/// </remarks>
internal static class ClusterChecks
{
    /// <summary>Check code: occupancy bits agree with the slots holding a non-zero entity key.</summary>
    public const string OccupancyAgreesWithKeys = "CHK-CLU-01";

    /// <summary>Check code: no duplicate entity key within an archetype.</summary>
    public const string NoDuplicateKeys = "CHK-CLU-02";

    /// <summary>Check code: every entity key is below the archetype's restored watermark.</summary>
    public const string KeysBelowWatermark = "CHK-CLU-05";

    /// <summary>Check code: the entity-key allocator watermark exceeds every key it has issued.</summary>
    public const string KeyWatermark = "CHK-ALO-02";

    /// <summary>A cluster holds at most 64 entities, because its occupancy is a single 64-bit word.</summary>
    private const int MaxSlotsPerCluster = 64;

    /// <summary>Byte offset of the occupancy word within a cluster chunk.</summary>
    private const int OccupancyOffset = 0;

    /// <summary>Low bits of a packed <c>EntityId</c> that hold the archetype routing id; the entity key sits above them.</summary>
    private const int EntityIdRoutingBits = 16;

    /// <summary>Runs the cluster family. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, with the manifest read and segments walked.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped("CHK-CLU-*", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped("CHK-CLU-*", "the schema manifest could not be read, so archetypes cannot be identified");
            return;
        }

        // The entity-key watermark is persisted on clean shutdown and restored on reopen. On a crash-path file it lags
        // whatever the WAL window still holds — the same shape RB-05 documents for the TSN watermark — so the two
        // watermark checks are skipped there rather than reporting a fatal allocator fault on a database that is
        // behaving as designed. Occupancy and duplicate-identity are unaffected: they compare the file against itself.
        var (_, cleanShutdown) = ctx.Bootstrap.ReadWatermarks();

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            if (archetype.ClusterSegmentRoot == 0)
            {
                continue;   // a pure-Transient archetype has no cluster storage, which is not a defect
            }

            Walk(ctx, archetype, cleanShutdown);
        }

        if (!cleanShutdown)
        {
            ctx.Findings.NoteSkipped($"{KeysBelowWatermark}, {KeyWatermark}",
                "the database was not closed cleanly, so its persisted entity-key watermark is stale by design and "
                + "recovery has not yet restored it");
        }
    }

    private static void Walk(ScanContext ctx, ArchetypeView archetype, bool cleanShutdown)
    {
        if (!ctx.Segments.TryGetValue(archetype.ClusterSegmentRoot, out var segment) || segment.Pages.Count == 0)
        {
            return;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(segment.Pages[0], page))
        {
            return;
        }

        var geometry = ChunkGeometry.FromPage(page);
        if (!geometry.IsUsable)
        {
            ctx.Findings.NoteCaveat(
                $"The cluster segment for archetype '{archetype.Name}' (root page {segment.RootPageIndex}) records no chunk "
                + "stride, so its slots were not read.");
            return;
        }

        // EntityIdsOffset == HeaderSize == 8 (occupancy) + 8 per component (EnabledBits). ComponentCount is the value
        // 09 §1 said was nowhere in the file; it is in ArchetypeR1, which is why this check exists at all.
        var entityKeysOffset = 8 + (8 * archetype.ComponentCount);
        if (entityKeysOffset + sizeof(long) > geometry.Stride)
        {
            ctx.Findings.NoteCaveat(
                $"Archetype '{archetype.Name}' reports {archetype.ComponentCount} components, which would place its entity "
                + $"keys at offset {entityKeysOffset} of a {geometry.Stride}-byte cluster. The layout does not fit, so its "
                + "slots were not read.");
            return;
        }

        var seen = new Dictionary<long, string>();

        // Published for MAP-01/02. Both the identities and WHERE each one lives: the map's value record names a cluster
        // chunk and slot, so an entry can hold a real entity id and still point somewhere else, and only the location
        // comparison sees it.
        var liveIds = new HashSet<long>();
        ctx.ClusterEntityIds[archetype.Name] = liveIds;

        var liveLocations = new Dictionary<long, int>();
        ctx.ClusterEntityLocations[archetype.Name] = liveLocations;

        var maxKey = 0L;
        var occupiedTotal = 0;
        var loadedPage = -1;

        for (var chunkId = 0; chunkId < geometry.Capacity(segment.Pages.Count); chunkId++)
        {
            if (!geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
            {
                continue;
            }

            var filePage = segment.Pages[ordinal];
            if (loadedPage != filePage)
            {
                if (!ctx.Source.TryReadPage(filePage, page))
                {
                    loadedPage = -1;
                    continue;
                }

                loadedPage = filePage;
            }

            if (!geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + geometry.Stride > IntegrityConstants.PageSize)
            {
                continue;
            }

            var cluster = new ReadOnlySpan<byte>(page, at, geometry.Stride);
            var occupancy = MemoryMarshal.Read<ulong>(cluster[OccupancyOffset..]);
            var locus = new Locus(filePage, segment.RootPageIndex, segment.Kind);

            for (var slot = 0; slot < MaxSlotsPerCluster; slot++)
            {
                var keyAt = entityKeysOffset + (slot * sizeof(long));
                if (keyAt + sizeof(long) > cluster.Length)
                {
                    break;   // past the cluster's own bytes; the remaining bits address slots that do not exist here
                }

                var occupied = (occupancy & (1UL << slot)) != 0;

                // The slot array holds a packed EntityId — the archetype's routing id in the low 16 bits, the entity key
                // above them — while NextEntityKey counts only the key. Comparing the raw value against the watermark is
                // how a healthy database reports every one of its entities as over the limit, which is exactly what the
                // first version of this check did.
                var raw = MemoryMarshal.Read<long>(cluster[keyAt..]);
                var key = raw >> EntityIdRoutingBits;

                if (occupied)
                {
                    occupiedTotal++;
                    if (key != 0)
                    {
                        // The KEY, not the packed id. The EntityMap is per-archetype, so its routing bits would be
                        // constant and are not stored: its keys are the bare entity key. Publishing the packed value
                        // here makes the two sets disjoint and MAP-01/02 fire on every healthy database.
                        liveIds.Add(key);
                        liveLocations[key] = ClusterLocation.Pack(chunkId, slot);
                    }

                    CheckOccupiedSlot(ctx, archetype, locus, chunkId, slot, key, seen, ref maxKey, cleanShutdown);
                }
                else if (key != 0)
                {
                    // The reverse direction. A key left behind in a free slot is not itself dangerous, but it means the
                    // occupancy word and the slot array disagree — and occupancy is what every walk trusts, so whichever
                    // is stale, something is being skipped or double-counted.
                    ctx.Report(OccupancyAgreesWithKeys, IntegritySeverity.Divergence, "", locus,
                        $"A free cluster slot of '{archetype.Name}' still holds an entity key.",
                        $"Cluster {chunkId} slot {slot} is marked free by its occupancy word but carries key {key}. The "
                        + "occupancy word is what every reader trusts, so this entity is invisible to scans while its "
                        + "identity is still recorded — and the slot can be handed to a new entity at any time.",
                        Repairability.Lossless);
                }
            }
        }

        // Strictly greater. NextEntityKey is the LAST key issued, not the next one free, so a live entity holding
        // exactly the watermark is the ordinary state of every database that has ever spawned anything.
        if (cleanShutdown && occupiedTotal > 0 && maxKey > archetype.NextEntityKey)
        {
            // ALO-02 and CLU-05 are the same disagreement seen from two ends, so they are reported together rather than
            // as two findings an operator would have to correlate. RB-06's shape: the allocator will hand out an id a
            // live entity already holds, and both sides are individually well-formed, so nothing else notices.
            ctx.Report(KeyWatermark, IntegritySeverity.Fatal, "RB-06",
                new Locus(segment.RootPageIndex, segment.RootPageIndex, segment.Kind),
                $"The entity-key allocator for '{archetype.Name}' is behind the keys it has already issued.",
                $"The archetype's restored NextEntityKey is {archetype.NextEntityKey}, but a live cluster slot holds key "
                + $"{maxKey}. The next entity spawned into this archetype receives an identifier a live entity already "
                + "holds; both sides are individually well-formed, so nothing detects the collision at the point it "
                + "happens.");
        }
    }

    private static void CheckOccupiedSlot(ScanContext ctx, ArchetypeView archetype, Locus locus, int chunkId, int slot,
        long key, Dictionary<long, string> seen, ref long maxKey, bool cleanShutdown)
    {
        if (key == 0)
        {
            ctx.Report(OccupancyAgreesWithKeys, IntegritySeverity.DataLoss, "", locus,
                $"An occupied cluster slot of '{archetype.Name}' holds no entity key.",
                $"Cluster {chunkId} slot {slot} is marked occupied but its entity key is zero. A live slot always carries "
                + "a non-zero identity, so either the slot's contents are gone or the geometry this was read through is "
                + "wrong — and the engine's own open path throws on exactly this condition rather than serving it.",
                Repairability.NotRepairable,
                new LossEstimate
                {
                    Kind = LossKind.Entities,
                    EntityCount = 1,
                    BoundedMin = 1,
                    BoundedMax = 1,
                    Archetype = archetype.Name,
                    Explanation = $"One '{archetype.Name}' whose identity is no longer recorded in its own slot."
                });
            return;
        }

        if (key > maxKey)
        {
            maxKey = key;
        }

        var where = $"cluster {chunkId} slot {slot}";
        if (seen.TryGetValue(key, out var first))
        {
            ctx.Report(NoDuplicateKeys, IntegritySeverity.Fatal, "RB-06", locus,
                $"Two live slots of '{archetype.Name}' claim the same entity.",
                $"Entity key {key} is held by both {first} and {where}. Two live objects share one identity, so every "
                + "lookup of that entity resolves to whichever the index happens to name, and a write to one is invisible "
                + "through the other.");
            return;
        }

        seen[key] = where;

        if (cleanShutdown && key > archetype.NextEntityKey)
        {
            ctx.Report(KeysBelowWatermark, IntegritySeverity.Divergence, "RB-06", locus,
                $"A live entity of '{archetype.Name}' holds a key at or above the allocator's watermark.",
                $"{where} holds key {key} while the archetype's restored NextEntityKey is "
                + $"{archetype.NextEntityKey}. The "
                + "watermark is meant to sit above every key ever issued; a key that reaches it will be issued a second "
                + "time.",
                Repairability.Lossless);
        }
    }
}
