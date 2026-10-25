using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Typhon.Engine.Internals;

/// <summary>A retention decision: the public plan, plus what the executor acts on.</summary>
internal sealed class RetentionDecision
{
    public BackupRetentionPlan Plan { get; init; }

    /// <summary>The kept points <c>S</c>, ascending.</summary>
    public List<uint> Kept { get; init; }

    /// <summary><c>K</c>, the oldest kept point; 0 when nothing is kept.</summary>
    public uint Cutoff { get; init; }

    /// <summary>The points to compact before any delete, ascending.</summary>
    public List<uint> Compactions { get; init; }

    /// <summary>The packs to delete, in delete order: descending id, so a crash part-way leaves every listed point resolvable.</summary>
    public List<StoreFileEntry> Deletions { get; init; }

    /// <summary>
    /// Per timeline, the lowest CheckpointLSN of its kept points and of the packs of a damaged chain that loses nothing (DL-4: it may be repaired, and a
    /// repaired point replays from its own C): what the WAL archive must keep from (DL-13).
    /// </summary>
    public List<(Guid TimelineId, long KeepFromLsn)> ArchiveFloors { get; init; }

    /// <summary>
    /// The archive is not pruned at all: an unreadable pack at or after the cutoff (its timeline unknown) or no restorable point protects every chain, so
    /// every timeline's archive may still be needed.
    /// </summary>
    public bool KeepArchive { get; init; }
}

/// <summary>
/// Retention's plan (05 §3 as corrected by plan 1415 DL-4/5/12, BKP-14): which points are kept, which are compacted first, and which packs then go.
/// Pure: it reads the view (headers only) and the inputs it is given, and writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept set.</b> The restorable points, newest first: a point is kept while <c>rank &lt; MinKeep</c>, or while it is within every limit set
/// (<c>MaxAge</c> from its freeze, <c>rank &lt; MaxCount</c>, the packs the kept points resolve through ≤ <c>MaxTotalBytes</c>); the walk stops at the
/// first point that is not. <c>K</c> is the last kept; <c>KeepLastDrilled</c> moves it back to the newest drill-passed point. <c>S</c> = every
/// restorable point ≥ <c>K</c>, whatever its chain.
/// </para>
/// <para>
/// <b>Deletions.</b> One store can interleave chains (copied bundles, offline and online captures), and a chain with points ≥ K can rest on a base
/// &lt; K, so "every pack below K" would delete a kept point's base. Instead: the oldest kept point of each chain is compacted when its chain reaches
/// below K, and a pack goes only if no kept point resolves through it (compactions substituted) and its id is &lt; K — or it is an <c>-I</c> whose
/// <c>-C</c> a kept point uses. A chain holding a point ≥ K that does not resolve loses nothing: it may be repaired, and its base must still be there.
/// </para>
/// </remarks>
internal static class RetentionPlanner
{
    /// <param name="view">The store, as the lock holder sees it.</param>
    /// <param name="policy">A validated policy.</param>
    /// <param name="drillPassed">The points whose newest drill passed (KeepLastDrilled's input; #1416 records drills).</param>
    /// <param name="utcNow">Now, for <c>MaxAge</c>.</param>
    /// <param name="freeBytes">The store volume's free bytes, or <c>null</c> when it does not say (DL-12).</param>
    /// <param name="compact">
    /// Plan compactions (the first plan). <c>false</c> after they ran: each kept point then needs exactly the packs it resolves through now, so a
    /// compaction that failed is never counted as if it had published.
    /// </param>
    public static RetentionDecision Plan(StoreView view, BackupRetentionPolicy policy, IReadOnlyCollection<uint> drillPassed, DateTime utcNow, long? freeBytes,
        bool compact = true)
    {
        var restorable = ChainResolver.RestorablePoints(view);
        if (restorable.Count == 0)
        {
            var any = view.Catalog.Packs.Count > 0 || view.Catalog.Unreadable.Count > 0;
            return Decision(view, [], 0, [], [], [], any ? BackupRetentionBlock.DamagedChain : BackupRetentionBlock.None,
                any ? "no point of the store is restorable: nothing is deleted" : null, keepArchive: any);
        }

        // The DL-5 walk.
        var block = BackupRetentionBlock.None;
        string reason = null;
        var keptFiles = new HashSet<string>(StringComparer.Ordinal);
        long keptBytes = 0;
        uint cutoff = 0;
        for (var rank = 0; rank < restorable.Count; rank++)
        {
            var id = restorable[restorable.Count - 1 - rank];
            var chain = ChainResolver.Resolve(view, id);
            var bytesWith = keptBytes;
            foreach (var p in chain.Packs)
            {
                if (!keptFiles.Contains(p.File))
                {
                    bytesWith += p.Size;
                }
            }

            var withinAge = policy.MaxAge == TimeSpan.Zero || utcNow - chain.Point.FreezeUtc <= policy.MaxAge;
            var withinCount = policy.MaxCount == 0 || rank < policy.MaxCount;
            var withinBytes = policy.MaxTotalBytes == 0 || bytesWith <= policy.MaxTotalBytes;
            var withinLimits = withinAge && withinCount && withinBytes;
            if (!withinLimits && rank >= policy.MinKeep)
            {
                break;
            }

            if (!withinLimits && block == BackupRetentionBlock.None)
            {
                block = BackupRetentionBlock.MinKeep;
                reason = $"MinKeep {policy.MinKeep} keeps point #{id} past its {Limit(withinAge, withinCount)} limit";
            }

            foreach (var p in chain.Packs)
            {
                keptFiles.Add(p.File);
            }

            keptBytes = bytesWith;
            cutoff = id;
        }

        if (policy.KeepLastDrilled)
        {
            uint drilled = 0;
            foreach (var id in drillPassed)
            {
                if (id > drilled && restorable.BinarySearch(id) >= 0)
                {
                    drilled = id;
                }
            }

            if (drilled != 0 && drilled < cutoff)
            {
                block = BackupRetentionBlock.KeepLastDrilled;
                reason = $"KeepLastDrilled keeps point #{drilled}, the newest whose drill passed, and every point after it, past the policy's limits";
                cutoff = drilled;
            }
        }

        var kept = new List<uint>();
        foreach (var id in restorable)
        {
            if (id >= cutoff)
            {
                kept.Add(id);
            }
        }

        // A chain with a point at or after K that does not resolve loses nothing; an unreadable pack there could be of any chain.
        var protectedChains = new HashSet<Guid>();
        var protectAll = false;
        foreach (var p in view.Catalog.Packs)
        {
            if (p.BackupId >= cutoff && restorable.BinarySearch(p.BackupId) < 0)
            {
                protectedChains.Add(p.ChainId);
            }
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            if (PackFormat.TryParseFileName(u.File, out var id, out _, out _) && id >= cutoff)
            {
                protectAll = true;
            }
        }

        if (protectAll || protectedChains.Count > 0)
        {
            block = BackupRetentionBlock.DamagedChain;
            reason = protectAll
                ? "an unreadable pack at or after the cutoff may belong to any chain: nothing below the cutoff is deleted until it is repaired or removed"
                : $"{protectedChains.Count} chain(s) hold a point at or after the cutoff that does not resolve: they keep every pack";
        }

        // Compactions: the oldest kept point of each chain whose resolution reaches below K, then each head deeper than CompactThreshold.
        var oldestOfChain = new Dictionary<Guid, uint>();
        var newestOfChain = new Dictionary<Guid, uint>();
        foreach (var id in kept)
        {
            var chainId = ChainResolver.Resolve(view, id).Point.ChainId;
            oldestOfChain.TryAdd(chainId, id);
            newestOfChain[chainId] = id;
        }

        var compactions = new SortedSet<uint>();
        foreach (var (chainId, id) in oldestOfChain)
        {
            var chain = ChainResolver.Resolve(view, id);
            if (compact && !chain.Point.IsBase && chain.Base.BackupId < cutoff && !protectedChains.Contains(chainId))
            {
                compactions.Add(id);
            }
        }

        if (compact && policy.CompactThreshold > 0)
        {
            foreach (var (_, id) in newestOfChain)
            {
                var chain = ChainResolver.Resolve(view, id);
                if (!chain.Point.IsBase && DepthAfter(chain, compactions) > policy.CompactThreshold)
                {
                    compactions.Add(id);
                }
            }
        }

        // DL-12: a compaction writes a whole image beside the packs it reads.
        long needBytes = 0;
        foreach (var id in compactions)
        {
            needBytes += BackupCompactor.ChainBytes(ChainResolver.Resolve(view, id));
        }

        if (compactions.Count > 0 && freeBytes is { } free && free < needBytes)
        {
            // The worst block is the one reported: a damaged chain (exit 3) is not hidden behind the free space (exit 1).
            if (block != BackupRetentionBlock.DamagedChain)
            {
                block = BackupRetentionBlock.InsufficientSpace;
                reason = $"the compactions need up to {needBytes} bytes on the store's volume and {free} are free: none runs, and their chains keep their packs";
            }

            compactions.Clear();
        }

        var needed = Needed(view, kept, compactions);
        var deletions = new List<StoreFileEntry>();
        foreach (var p in view.Catalog.Packs)
        {
            var superseded = p.Kind == BackupKind.Incremental && needed.Contains(PackFormat.FileName(p.BackupId, BackupKind.Compacted));
            if (needed.Contains(p.File) || protectedChains.Contains(p.ChainId) || (protectAll && !superseded) || !(p.BackupId < cutoff || superseded))
            {
                continue;
            }

            deletions.Add(new StoreFileEntry(p.File, p.Size));
        }

        if (!protectAll && protectedChains.Count == 0)
        {
            // Unreadable packs below K: no kept point resolves through them, and no damaged chain past K might need them.
            foreach (var u in view.Catalog.Unreadable)
            {
                if (PackFormat.TryParseFileName(u.File, out var id, out _, out _) && id < cutoff)
                {
                    deletions.Add(new StoreFileEntry(u.File, u.Size));
                }
            }
        }

        // Newest first; within an id the -I before the -C (either way the id resolves while one remains).
        deletions.Sort(static (a, b) =>
        {
            PackFormat.TryParseFileName(a.Name, out var ia, out var ka, out _);
            PackFormat.TryParseFileName(b.Name, out var ib, out var kb, out _);
            return ia != ib ? ib.CompareTo(ia) : ((byte)kb).CompareTo((byte)ka);
        });

        return Decision(view, kept, cutoff, [.. compactions], deletions, Floors(view, kept, protectedChains), block, reason, keepArchive: protectAll);
    }

    /// <summary>The packs a restore of a kept point reads, a planned compaction standing for its whole chain.</summary>
    private static HashSet<string> Needed(StoreView view, List<uint> kept, SortedSet<uint> compactions)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in kept)
        {
            var packs = ChainResolver.Resolve(view, id).Packs;
            for (var i = packs.Count - 1; i >= 0; i--)
            {
                var p = packs[i];
                if (!p.IsBase && compactions.Contains(p.BackupId))
                {
                    needed.Add(PackFormat.FileName(p.BackupId, BackupKind.Compacted));
                    break;
                }

                needed.Add(p.File);
            }
        }

        return needed;
    }

    /// <summary>The packs a restore of <paramref name="chain"/>'s point reads once the planned compactions exist.</summary>
    private static int DepthAfter(ResolvedChain chain, SortedSet<uint> compactions)
    {
        var depth = 0;
        for (var i = chain.Packs.Count - 1; i >= 0; i--)
        {
            depth++;
            if (compactions.Contains(chain.Packs[i].BackupId))
            {
                break;
            }
        }

        return depth;
    }

    private static List<(Guid, long)> Floors(StoreView view, List<uint> kept, HashSet<Guid> protectedChains)
    {
        var floors = new Dictionary<Guid, long>();
        foreach (var id in kept)
        {
            var point = ChainResolver.Resolve(view, id).Point;
            Lower(floors, point.TimelineId, point.CheckpointLsn);
        }

        // A damaged chain keeps every pack (DL-4): once repaired, each of its points replays the archive from its own C.
        foreach (var p in view.Catalog.Packs)
        {
            if (protectedChains.Contains(p.ChainId))
            {
                Lower(floors, p.TimelineId, p.CheckpointLsn);
            }
        }

        var list = new List<(Guid, long)>(floors.Count);
        foreach (var (timeline, lsn) in floors)
        {
            list.Add((timeline, lsn));
        }

        list.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    private static void Lower(Dictionary<Guid, long> floors, Guid timeline, long lsn)
    {
        if (!floors.TryGetValue(timeline, out var floor) || lsn < floor)
        {
            floors[timeline] = lsn;
        }
    }

    /// <summary>The CRC of a plan's compactions and deletions, in order: what an approval names.</summary>
    internal static string Fingerprint(IReadOnlyList<uint> compactions, IReadOnlyList<string> deletions)
    {
        var crc = Crc32CUtil.Start;
        foreach (var c in compactions)
        {
            crc = Crc32CUtil.Append(crc, Encoding.UTF8.GetBytes("C" + c.ToString(CultureInfo.InvariantCulture) + ";"));
        }

        foreach (var d in deletions)
        {
            crc = Crc32CUtil.Append(crc, Encoding.UTF8.GetBytes("D" + d + ";"));
        }

        return Crc32CUtil.Finish(crc).ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string Limit(bool withinAge, bool withinCount) => !withinAge ? "MaxAge" : !withinCount ? "MaxCount" : "MaxTotalBytes";

    private static RetentionDecision Decision(StoreView view, List<uint> kept, uint cutoff, List<uint> compactions, List<StoreFileEntry> deletions,
        List<(Guid, long)> floors, BackupRetentionBlock block, string reason, bool keepArchive = false)
    {
        long before = 0;
        foreach (var p in view.Catalog.Packs)
        {
            before += p.Size;
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            before += u.Size;
        }

        var keptFiles = new HashSet<string>(StringComparer.Ordinal);
        long after = 0;
        foreach (var id in kept)
        {
            foreach (var p in ChainResolver.Resolve(view, id).Packs)
            {
                if (keptFiles.Add(p.File))
                {
                    after += p.Size;
                }
            }
        }

        // A point is removed when every pack of its id goes (a kept point being compacted loses its -I, not itself).
        var keptSet = new HashSet<uint>(kept);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in deletions)
        {
            deleted.Add(d.Name);
        }

        var ids = new SortedSet<uint>();
        foreach (var p in view.Catalog.Packs)
        {
            ids.Add(p.BackupId);
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            if (PackFormat.TryParseFileName(u.File, out var id, out _, out _))
            {
                ids.Add(id);
            }
        }

        var removed = new List<uint>();
        foreach (var id in ids)
        {
            var (compacted, original) = view.Find(id);
            var gone = (compacted == null || deleted.Contains(compacted.File)) && (original == null || deleted.Contains(original.File));
            foreach (var u in view.Catalog.Unreadable)
            {
                if (PackFormat.TryParseFileName(u.File, out var uid, out _, out _) && uid == id && !deleted.Contains(u.File))
                {
                    gone = false;
                }
            }

            if (gone && !keptSet.Contains(id))
            {
                removed.Add(id);
            }
        }

        var files = new List<string>(deletions.Count);
        foreach (var d in deletions)
        {
            files.Add(d.Name);
        }

        var plan = new BackupRetentionPlan(kept, removed, compactions, files, cutoff, before, after, block, reason, Fingerprint(compactions, files));
        return new RetentionDecision
        {
            Plan = plan,
            Kept = kept,
            Cutoff = cutoff,
            Compactions = compactions,
            Deletions = deletions,
            ArchiveFloors = floors,
            KeepArchive = keepArchive,
        };
    }
}
