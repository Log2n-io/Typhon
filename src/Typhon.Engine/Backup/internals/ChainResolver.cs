using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>What resolving one point found: its chain, or where and why it breaks.</summary>
internal readonly record struct ChainResolution(ResolvedChain Chain, uint BrokenAt, string Why, string Code);

/// <summary>A point's chain, base first and the point last, every link checked.</summary>
internal sealed class ResolvedChain
{
    public ResolvedChain(IReadOnlyList<CatalogPack> packs) => Packs = packs;

    /// <summary>The packs, from the base (Full or Compacted) to the point.</summary>
    public IReadOnlyList<CatalogPack> Packs { get; }

    public CatalogPack Point => Packs[^1];

    public CatalogPack Base => Packs[0];

    public int Depth => Packs.Count;
}

/// <summary>
/// Resolves a point to the packs it is made of (plan 1413 §2.5): follow <c>ParentId</c> from the point down to a base, preferring a point's
/// compacted pack (<c>-C</c>) over its original (05 §2), checking every link.
/// </summary>
internal static class ChainResolver
{
    /// <summary>The source map of a materialised point is 2 B per page: a chain deeper than this cannot be addressed (compaction keeps ~42).</summary>
    public const int MaxDepth = 65_534;

    /// <summary>The newest point in the store, or <c>0</c> when it holds none.</summary>
    public static uint LatestId(StoreView view)
    {
        uint latest = 0;
        foreach (var p in view.Catalog.Packs)
        {
            latest = Math.Max(latest, p.BackupId);
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            if (PackFormat.TryParseFileName(u.File, out var id, out _, out _))
            {
                latest = Math.Max(latest, id);
            }
        }

        return latest;
    }

    /// <summary>The id a selector names in this store.</summary>
    public static uint Select(StoreView view, PointSelector selector)
    {
        if (!selector.IsLatest)
        {
            return selector.BackupId;
        }

        var latest = LatestId(view);
        if (latest == 0)
        {
            throw new BackupChainBrokenException(0, 0, [], $"the store '{view.Paths.Root}' holds no backup point");
        }

        return latest;
    }

    /// <summary>Resolves <paramref name="pointId"/>, or throws <see cref="BackupChainBrokenException"/> naming the restorable alternatives.</summary>
    public static ResolvedChain Resolve(StoreView view, uint pointId)
    {
        if (TryResolve(view, pointId, out var chain, out var brokenAt, out var why))
        {
            return chain;
        }

        throw new BackupChainBrokenException(pointId, brokenAt, RestorablePoints(view), why);
    }

    /// <summary>Resolves <paramref name="pointId"/>; on failure names the point whose pack breaks the chain and why.</summary>
    public static bool TryResolve(StoreView view, uint pointId, out ResolvedChain chain, out uint brokenAt, out string why) =>
        TryResolve(view, pointId, out chain, out brokenAt, out why, out _);

    /// <summary>
    /// As <see cref="TryResolve(StoreView, uint, out ResolvedChain, out uint, out string)"/>, also giving the verify code of the failure:
    /// <c>BKP-VER-07</c> for an LSN out of order, <c>BKP-VER-06</c> for anything else.
    /// </summary>
    public static bool TryResolve(StoreView view, uint pointId, out ResolvedChain chain, out uint brokenAt, out string why, out string code)
    {
        // Memoised per view: verify, status and --restorable resolve every point, often more than once.
        if (!view.Resolved.TryGetValue(pointId, out var resolution))
        {
            Walk(view, pointId, out chain, out brokenAt, out why, out code);
            resolution = new ChainResolution(chain, brokenAt, why, code);
            view.Resolved[pointId] = resolution;
        }

        (chain, brokenAt, why, code) = (resolution.Chain, resolution.BrokenAt, resolution.Why, resolution.Code);
        return chain != null;
    }

    private static void Walk(StoreView view, uint pointId, out ResolvedChain chain, out uint brokenAt, out string why, out string code)
    {
        code = null;
        chain = null;
        var links = new List<CatalogPack>();
        CatalogPack child = null;
        var id = pointId;
        while (true)
        {
            var pack = Pick(view, id, out why);
            if (pack == null)
            {
                brokenAt = id;
                code = "BKP-VER-06";
                return;
            }

            (code, why) = CheckLink(view, pack, child, links.Count == 0 ? null : links[0]);
            if (why != null)
            {
                brokenAt = id;
                return;
            }

            links.Add(pack);
            if (links.Count > MaxDepth)
            {
                brokenAt = id;
                why = $"the chain is deeper than {MaxDepth} points";
                code = "BKP-VER-06";
                return;
            }

            if (pack.IsBase)
            {
                break;
            }

            child = pack;
            id = pack.ParentId;
        }

        links.Reverse();
        chain = new ResolvedChain(links);
        brokenAt = 0;
        why = null;
    }

    /// <summary>Every point whose chain resolves, ascending.</summary>
    public static List<uint> RestorablePoints(StoreView view)
    {
        var ids = new SortedSet<uint>();
        foreach (var p in view.Catalog.Packs)
        {
            ids.Add(p.BackupId);
        }

        var restorable = new List<uint>();
        foreach (var id in ids)
        {
            if (TryResolve(view, id, out _, out _, out _))
            {
                restorable.Add(id);
            }
        }

        return restorable;
    }

    /// <summary>The pack that stands for point <paramref name="id"/>: its compacted pack when present, else its original.</summary>
    private static CatalogPack Pick(StoreView view, uint id, out string why)
    {
        var (compacted, original) = view.Find(id);
        var pack = compacted ?? original;
        if (pack != null)
        {
            why = null;
            return pack;
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            if (PackFormat.TryParseFileName(u.File, out var uid, out _, out _) && uid == id)
            {
                why = $"its pack {u.File} is unreadable: {u.Reason}";
                return null;
            }
        }

        why = "its pack is missing";
        return null;
    }

    /// <summary>
    /// Checks <paramref name="pack"/> as the parent of <paramref name="child"/> (null for the point itself): same store, database and chain,
    /// ids descending, the data file never shrinking, <c>C</c> and <c>P</c> never decreasing, <c>BaseId</c> consistent.
    /// </summary>
    private static (string Code, string Why) CheckLink(StoreView view, CatalogPack pack, CatalogPack child, CatalogPack point)
    {
        const string link = "BKP-VER-06";
        const string lsn = "BKP-VER-07";
        if (pack.DatabaseId != view.Identity.DatabaseId)
        {
            return (link, $"its pack belongs to database {pack.DatabaseId}, the store to {view.Identity.DatabaseId}");
        }

        if (pack.StoreId != view.Identity.StoreId)
        {
            return (link, $"its pack was written by store {pack.StoreId}, not this one");
        }

        if (pack.PointLsn < pack.CheckpointLsn)
        {
            return (lsn, $"its PointLSN {pack.PointLsn} is below its CheckpointLSN {pack.CheckpointLsn}");
        }

        if (child == null)
        {
            return (null, null);
        }

        if (pack.ChainId != child.ChainId)
        {
            return (link, $"it belongs to chain {pack.ChainId}, its child #{child.BackupId} to chain {child.ChainId}");
        }

        if (pack.BackupId >= child.BackupId || child.ParentId != pack.BackupId)
        {
            return (link, $"#{child.BackupId} names parent #{child.ParentId}");
        }

        if (pack.ImagePages > child.ImagePages)
        {
            return (link, $"its image ({pack.ImagePages} pages) is larger than its child's ({child.ImagePages}); the data file never shrinks");
        }

        if (pack.CheckpointLsn > child.CheckpointLsn || pack.PointLsn > child.PointLsn)
        {
            return (lsn, $"its LSNs (C {pack.CheckpointLsn}, P {pack.PointLsn}) are past its child's (C {child.CheckpointLsn}, P {child.PointLsn})");
        }

        // BaseId runs unchanged along incrementals; a Full base IS that id. A compacted base stands for a prefix of the chain, so the points
        // written before the compaction keep the older base id.
        var expectedBase = point.BaseId;
        var ok = pack.Kind switch
        {
            BackupKind.Incremental => pack.BaseId == expectedBase,
            BackupKind.Full => pack.BackupId == expectedBase,
            _ => expectedBase <= pack.BackupId,
        };
        return ok ? (null, null) : (link, $"its base id {pack.BaseId} disagrees with the point's {expectedBase}");
    }
}
