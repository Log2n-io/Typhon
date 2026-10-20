using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// Applies a retention under <c>store.lock</c> (05 §3 as corrected, BKP-14): plan, refuse a plan that changed since the one approved, compact, re-plan on
/// what the compactions actually published, delete newest first with each delete made durable, prune the WAL archive, then prove every kept point
/// still resolves.
/// </summary>
/// <remarks>
/// Runs on whichever thread holds the session: the engine's backup thread after a point (#1416), or <c>typhon backup prune</c>. A compaction that fails
/// is logged and its chain keeps every pack this run; anything else that fails stops the retention where it is, which every prefix of a descending
/// delete sequence allows (each listed point keeps its whole chain).
/// </remarks>
internal static class RetentionExecutor
{
    /// <param name="session">The store, its lock held.</param>
    /// <param name="policy">A validated policy.</param>
    /// <param name="approved">The plan an operator approved, or <c>null</c>: then whatever the policy says now is applied.</param>
    /// <param name="drillPassed">The points whose newest drill passed (#1416's drill records); empty without them.</param>
    /// <param name="utcNow">Now, for <c>MaxAge</c>.</param>
    /// <param name="liveTimeline">The timeline a live engine is writing, whose archive is never pruned whole; empty from the CLI.</param>
    /// <param name="options">Read budget, cancellation, progress, logging.</param>
    public static BackupRetentionResult Apply(BackupStore session, BackupRetentionPolicy policy, BackupRetentionPlan approved, IReadOnlyCollection<uint> drillPassed,
        DateTime utcNow, Guid liveTimeline, BackupLifecycleOptions options)
    {
        options ??= new BackupLifecycleOptions();
        var clock = Stopwatch.StartNew();
        var fs = session.FileSystem;
        var log = session.Logger;
        var root = session.Root;
        long? free = fs.TryGetAvailableFreeSpace(session.View.Paths.Packs, out var bytes) ? bytes : null;
        var first = RetentionPlanner.Plan(session.View, policy, drillPassed, utcNow, free);
        if (approved != null && !IsSubset(first.Plan.PackDeletions, approved.PackDeletions))
        {
            throw new BackupPlanChangedException(root, approved.Fingerprint, first.Plan.Fingerprint);
        }

        // Compactions before any delete: a point stops needing the packs below it only once its -C is published and verified.
        var compactions = new List<BackupCompactionResult>();
        foreach (var id in first.Compactions)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            try
            {
                var result = BackupCompactor.Compact(session, id, options);
                if (!result.AlreadyBase)
                {
                    compactions.Add(result);
                }
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                BackupLog.RetentionCompactionFailed(log, root, id, ex.Message);
            }
        }

        // Re-planned on what is there now: a failed compaction is not substituted, so its chain keeps its packs. Its own compaction it does not
        // re-attempt: the second plan's list is ignored.
        var decision = first.Compactions.Count == 0
            ? first
            : RetentionPlanner.Plan(session.View, policy, drillPassed, utcNow, null, compact: false);
        if (!IsSubset(decision.Plan.PackDeletions, first.Plan.PackDeletions))
        {
            throw new InvalidOperationException(
                $"Retention of '{root}': the plan after its compactions deletes a pack the first did not list. This is a defect: nothing was deleted.");
        }

        var deleted = new List<string>();
        long bytesFreed = 0;
        try
        {
            foreach (var d in decision.Deletions)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                session.DeletePack(d.Name);
                deleted.Add(d.Name);
                bytesFreed += d.Length;
            }
        }
        finally
        {
            session.RefreshView();
        }

        // DL-13 (05 §3 step 5): the archived WAL no kept point can replay from.
        PruneArchive(session, decision.ArchiveFloors, liveTimeline);

        // R1 (DL-7): every kept point resolves on the store as it now is. Deletion cannot change a kept pack (BKP-08), only remove a link.
        foreach (var id in decision.Kept)
        {
            if (!ChainResolver.TryResolve(session.View, id, out _, out _, out var why))
            {
                BackupLog.RetentionBrokeAKeptPoint(log, root, id, why);
                throw new InvalidOperationException($"Retention of '{root}' left kept point #{id} unrestorable ({why}). This is a defect: report it.");
            }
        }

        foreach (var c in compactions)
        {
            bytesFreed -= c.BytesWritten;
        }

        // The plan applied: the first one's compactions (the result lists those that published), the deletions re-planned after them.
        var plan = decision.Plan with { Compactions = first.Plan.Compactions };
        if (plan.Blocked != BackupRetentionBlock.None)
        {
            BackupLog.RetentionBlocked(log, root, plan.Blocked, plan.BlockedReason);
        }

        BackupLog.RetentionApplied(log, root, plan.Kept.Count, plan.CutoffId, compactions.Count, deleted.Count, bytesFreed, clock.ElapsedMilliseconds);
        return new BackupRetentionResult(plan, compactions, deleted, bytesFreed, clock.Elapsed);
    }

    /// <summary>
    /// Deletes the archived WAL segments no kept point can replay from: per timeline, every segment wholly below the CheckpointLSN of its oldest kept
    /// point, and every timeline no kept point is on, except <paramref name="liveTimeline"/>.
    /// </summary>
    private static void PruneArchive(BackupStore session, List<(Guid TimelineId, long KeepFromLsn)> floors, Guid liveTimeline)
    {
        // TODO(#1418): WalArchive.PruneArchive(session, floors, liveTimeline) — the archive and its per-timeline archive.lock are #1418's (impl/1418
        // §4.6). Until it lands there is no wal/ to prune. The floors are computed here, under store.lock, as the seam fixes them.
        _ = session;
        _ = floors;
        _ = liveTimeline;
    }

    private static bool IsSubset(IReadOnlyList<string> subset, IReadOnlyList<string> of)
    {
        var set = new HashSet<string>(of, StringComparer.Ordinal);
        foreach (var s in subset)
        {
            if (!set.Contains(s))
            {
                return false;
            }
        }

        return true;
    }
}
