using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The assertion half of the T-5 differential recovery oracle (design 03 §4.2). Drives a <see cref="RecoveryShadowModel"/> against a recovered engine on two axes:
/// the <b>primary</b> broad-scan/per-id axis (<see cref="AssertPrimaryAxis"/> — recovered state ≡ shadow) and the <b>index</b> axis (each secondary index's result set
/// ≡ the broad-scan set; built from <see cref="BroadScanEntityIds"/> + <see cref="IndexEntityIds{T,TKey}"/>). Kept separate from the shadow so the future crash sweep
/// (A1.2) can reuse the same assertions over many crash points.
/// </summary>
internal static class RecoveryOracle
{
    /// <summary>Assert the recovered engine reproduces the shadow exactly (values, enabled-bits, alive-set, no resurrection). Fails with the full structured diff.</summary>
    public static void AssertPrimaryAxis(DatabaseEngine recoveredDbe, RecoveryShadowModel shadow)
    {
        var diffs = shadow.Diff(recoveredDbe);
        Assert.That(
            diffs,
            Is.Empty,
            () => $"Differential oracle — primary (broad-scan) axis found {diffs.Count} mismatch(es):{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", diffs));
    }

    /// <summary>The set of entity ids a broad scan (no secondary index) reports for <paramref name="archetypeId"/> at the transaction's snapshot.</summary>
    public static HashSet<EntityId> BroadScanEntityIds(Transaction tx, ushort archetypeId) => new(tx.EnumerateArchetypeEntities(archetypeId));

    /// <summary>
    /// The set of entity ids a secondary index reports over the full key range. Compared against <see cref="BroadScanEntityIds"/> for the index axis: divergence means
    /// the index disagrees with the primary store (RB-01/RB-02 — e.g. recovery rebuilt the entity but not its index entry).
    /// </summary>
    public static HashSet<EntityId> IndexEntityIds<T, TKey>(DatabaseEngine dbe, Transaction tx, Expression<Func<T, TKey>> keySelector, TKey min, TKey max)
        where T : unmanaged
        where TKey : unmanaged
    {
        var set = new HashSet<EntityId>();
        var indexRef = dbe.GetIndexRef<T, TKey>(keySelector);
        using var e = tx.EnumerateIndex<T, TKey>(indexRef, min, max);
        foreach (var item in e)
        {
            set.Add(EntityId.FromRaw(item.EntityPK));
        }

        return set;
    }
}
