using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Backups without an engine: what <c>typhon backup …</c> and the Workbench share (D10), the way <see cref="DatabaseRepair"/> serves
/// <c>typhon repair</c>. Online capture of a running database is the engine's (its backup coordinator); this facade backs up a closed one, and
/// restores, verifies and lists a store.
/// </summary>
/// <remarks>
/// A store is a directory bound to one database: <c>store.json</c>, <c>packs/</c> of immutable <c>.tpack</c> files, a rebuildable
/// <c>catalog.json</c>. Every pack byte is under a CRC (BKP-08); a restore opens through the engine's crash path to exactly its point's committed state
/// (BKP-05).
/// </remarks>
[PublicAPI]
public static class BackupOperations
{
    /// <summary>
    /// Backs up a closed database into a store (created when absent): a full point, or an incremental one when the database's last clean close kept a
    /// lineage this store's chain head continues.
    /// </summary>
    /// <param name="bundle">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="store">The store directory.</param>
    /// <param name="options">Full, WAL directory, cancellation, logging.</param>
    /// <returns>The published point.</returns>
    /// <exception cref="DatabaseLockedException">A process holds the database (online capture is the engine's).</exception>
    /// <exception cref="StorageException">The bundle cannot be backed up as configured (e.g. an unclean database whose WAL is elsewhere).</exception>
    /// <exception cref="BackupStoreLockedException">Another writer holds the store.</exception>
    /// <exception cref="BackupStoreMismatchException">The store belongs to another database.</exception>
    public static BackupPointSummary Create(string bundle, string store, BackupCreateOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundle);
        ArgumentException.ThrowIfNullOrEmpty(store);
        options ??= new BackupCreateOptions();
        var point = OfflineCapture.Create(bundle, store, new CreateOptions
        {
            Full = options.Full,
            WalDirectory = options.WalDirectory,
            Cancellation = options.Cancellation,
            Logger = options.Logger,
        });
        var view = StoreView.Open(store, log: options.Logger, trustCatalog: false);
        return Summarize(view.Find(point.BackupId).Original);
    }

    /// <summary>
    /// Restores a point into a bundle: <paramref name="target"/> is a <c>{name}.typhon</c> path carrying the database's own name, or a directory to put
    /// it in. Nothing is written before every refusal is checked; an existing bundle is moved aside, never deleted.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">Which point.</param>
    /// <param name="target">The bundle path or its parent directory.</param>
    /// <param name="options">Replace, format mismatch, forensics, settle, progress.</param>
    /// <returns>What was restored, and where.</returns>
    /// <exception cref="RestoreRefusedException">The target exists, is live, is misnamed, or the image is of another format revision.</exception>
    /// <exception cref="BackupChainBrokenException">A pack of the point's chain is missing or inconsistent; the message names what is restorable.</exception>
    /// <exception cref="BackupPackCorruptException">A byte the restore needs failed its check.</exception>
    public static BackupRestoreResult Restore(string store, PointSelector point, string target, BackupRestoreOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        options ??= new BackupRestoreOptions();
        var outcome = RestoreStager.Run(store, point, target, new RestoreOptions
        {
            Replace = options.Replace,
            AllowFormatMismatch = options.AllowFormatMismatch,
            AllowDamaged = options.AllowDamaged,
            OpenAndClose = options.OpenAndClose,
            ReadBudgetBytes = options.ReadBudgetBytes,
            Cancellation = options.Cancellation,
            Progress = options.Progress,
            Logger = options.Logger,
        });
        var view = StoreView.Open(store, trustCatalog: false);
        return new BackupRestoreResult(Summarize(Standing(view, outcome.PointId)), outcome.Target, outcome.DisplacedBundle, outcome.ChainDepth,
            outcome.PagesWritten, outcome.ZeroPages, outcome.TailSegments, outcome.BytesRead, outcome.BytesWritten, outcome.Duration, outcome.SuspectPages,
            outcome.DamagedPages.Select(d => new PackPageDamage(d.PageId, d.PackFile, d.Reason)).ToList(), outcome.NewTimelineId,
            outcome.SettleFailure?.Message);
    }

    /// <summary>Verifies the whole store. Never throws for damage: it reports it.</summary>
    /// <param name="store">The store directory.</param>
    /// <param name="level">Quick or full.</param>
    /// <exception cref="System.IO.DirectoryNotFoundException">The directory is not a store.</exception>
    /// <exception cref="BackupFormatUnsupportedException">The store was written by a newer format.</exception>
    public static BackupVerifyReport Verify(string store, BackupVerifyLevel level) =>
        Convert(BackupVerifier.VerifyStore(store, (VerifyLevel)level));

    /// <summary>Verifies one point: the packs of its chain, and whether it is restorable. Never throws for damage.</summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">Which point.</param>
    /// <param name="level">Quick or full.</param>
    public static BackupVerifyReport Verify(string store, PointSelector point, BackupVerifyLevel level) =>
        Convert(BackupVerifier.VerifyPoint(store, point, (VerifyLevel)level));

    /// <summary>What the store holds, from its catalog (rebuilt in memory when stale).</summary>
    /// <param name="store">The store directory.</param>
    /// <param name="computeRestorable">Also run a quick verify and list the restorable points.</param>
    public static BackupStoreListing List(string store, bool computeRestorable = false)
    {
        var view = StoreView.Open(store);
        var points = view.Catalog.Packs.OrderBy(p => p.BackupId).ThenBy(p => p.Kind).Select(Summarize).ToList();
        var chains = view.Catalog.Chains.Select(c => new BackupChainSummary(c.ChainId, c.BaseId, c.HeadBackupId, c.Depth, c.HeadDepth)).ToList();
        var unreadable = view.Catalog.Unreadable.Select(u => new KeyValuePair<string, string>(u.File, u.Reason)).ToList();
        var temps = view.Temps.Select(t => t.Name).ToList();
        IReadOnlyList<uint> restorable = null;
        if (computeRestorable)
        {
            restorable = BackupVerifier.VerifyStore(store, VerifyLevel.Quick).Points.Where(p => p.Restorable).Select(p => p.Id).ToList();
        }

        return new BackupStoreListing(view.Identity.StoreId, view.Identity.DatabaseId, points, chains, unreadable, temps, restorable);
    }

    /// <summary>
    /// The store's health from its packs' headers and footers and the chains they form: <c>typhon backup status --store</c>'s answer and exit code.
    /// Cheap by design (each pack's header and footer, no index, no tail); <see cref="Verify(string, BackupVerifyLevel)"/> reads the rest.
    /// </summary>
    /// <param name="store">The store directory.</param>
    public static BackupStoreStatus Status(string store)
    {
        var view = StoreView.Open(store, trustCatalog: false);
        var latestId = ChainResolver.LatestId(view);
        var latestPack = latestId == 0 ? null : Standing(view, latestId);
        var restorableId = ChainResolver.RestorablePoints(view).DefaultIfEmpty(0u).Max();
        var restorable = restorableId == 0 ? null : Summarize(Standing(view, restorableId));
        var count = view.Catalog.Packs.Select(p => p.BackupId).Concat(view.Catalog.Unreadable
            .Select(u => PackFormat.TryParseFileName(u.File, out var id, out _, out _) ? id : 0u).Where(id => id != 0)).Distinct().Count();
        if (restorable == null)
        {
            return new BackupStoreStatus(BackupHealth.Failing, count == 0 ? "the store holds no point" : "no point of the store can be restored", count,
                latestPack == null ? null : Summarize(latestPack), null);
        }

        string warning = null;
        if (restorableId != latestId)
        {
            warning = $"the newest point #{latestId} is not restorable; the newest that is is #{restorableId}";
        }
        else if (latestPack is { Suspect: true })
        {
            warning = $"the newest point #{latestId} holds pages the live database had damaged at capture";
        }
        else if (view.Temps.Count > 0)
        {
            warning = $"{view.Temps.Count} temporary file(s) of an interrupted backup are left; the next backup deletes them";
        }

        return new BackupStoreStatus(warning == null ? BackupHealth.Ok : BackupHealth.Warning, warning ?? "the newest point is restorable", count,
            latestPack == null ? null : Summarize(latestPack), restorable);
    }

    /// <summary>
    /// Turns a point into a base without touching the database (05 §2): <c>{id}-C.tpack</c> holds every page of its image and its tail, so the points
    /// after it rest on it and the packs before it can go. Published only once a full verify of it passed; nothing is deleted.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">Which point; a full or already compacted one is a no-op.</param>
    /// <param name="options">Read budget, cancellation, progress, logging.</param>
    /// <exception cref="BackupStoreLockedException">Another writer holds the store.</exception>
    /// <exception cref="BackupChainBrokenException">The point does not resolve.</exception>
    /// <exception cref="BackupPackCorruptException">A page of its chain, or the compacted pack, failed a check: nothing was published.</exception>
    /// <exception cref="System.IO.IOException">The volume is short of the space a whole image needs, or another I/O failure.</exception>
    public static BackupCompactionResult Compact(string store, PointSelector point, BackupLifecycleOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        options ??= new BackupLifecycleOptions();
        using var session = BackupStore.OpenForMaintenance(store, "compact", log: options.Logger);
        return BackupCompactor.Compact(session, ChainResolver.Select(session.View, point), options);
    }

    /// <summary>
    /// What a retention would do now, without doing it (05 §3, the mandatory preview): the points it keeps, compacts and removes, and the packs it
    /// deletes. Reads the packs' headers only; takes no lock, so a writer may change the store before the plan is applied (see
    /// <see cref="ApplyRetention"/>'s <c>approved</c>).
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="policy">The policy (validated).</param>
    /// <exception cref="ArgumentException">The policy is invalid.</exception>
    public static BackupRetentionPlan PlanRetention(string store, BackupRetentionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        Validate(policy);
        var fs = StoreFileSystem.Instance;
        var view = StoreView.Open(store, fs, trustCatalog: false);
        long? free = ((IStoreFileSystem)fs).TryGetAvailableFreeSpace(view.Paths.Packs, out var bytes) ? bytes : null;
        return RetentionPlanner.Plan(view, policy, [], DateTime.UtcNow, free).Plan;
    }

    /// <summary>
    /// Applies a retention under the store's lock (05 §3, BKP-14): compacts the oldest kept point of each chain that needs it, then deletes, newest first,
    /// every pack no kept point resolves through; a kept point is never lost, and a limit <see cref="BackupRetentionPolicy.MinKeep"/> or
    /// <see cref="BackupRetentionPolicy.KeepLastDrilled"/> prevents is reported, not met.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="policy">The policy (validated).</param>
    /// <param name="approved">
    /// A plan from <see cref="PlanRetention"/> the operator approved: refused with <see cref="BackupPlanChangedException"/> when the store would now
    /// delete a pack it did not list. <c>null</c> applies the policy as it stands.
    /// </param>
    /// <param name="options">Read budget, cancellation, progress, logging.</param>
    /// <exception cref="ArgumentException">The policy is invalid.</exception>
    /// <exception cref="BackupStoreLockedException">Another writer holds the store.</exception>
    /// <exception cref="BackupPlanChangedException">The plan changed since <paramref name="approved"/>.</exception>
    public static BackupRetentionResult ApplyRetention(string store, BackupRetentionPolicy policy, BackupRetentionPlan approved = null,
        BackupLifecycleOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        Validate(policy);
        options ??= new BackupLifecycleOptions();
        using var session = BackupStore.OpenForMaintenance(store, "prune", log: options.Logger);
        return RetentionExecutor.Apply(session, policy, approved, [], DateTime.UtcNow, Guid.Empty, options);
    }

    private static void Validate(BackupRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Validate(out var error))
        {
            throw new ArgumentException(error, nameof(policy));
        }
    }

    /// <summary>The pack that stands for a point: its compacted one when present.</summary>
    private static CatalogPack Standing(StoreView view, uint id)
    {
        var (compacted, original) = view.Find(id);
        return compacted ?? original;
    }

    internal static BackupPointSummary Summarize(CatalogPack p) =>
        p == null
            ? null
            : new BackupPointSummary(p.BackupId, (BackupPointKind)p.Kind, p.ChainId, p.ParentId, p.BaseId, p.CheckpointLsn, p.PointLsn, p.ImagePages,
                p.PageCount, p.TailSegments, p.Suspect, p.Offline, p.FreezeUtc, p.TailEndUtc, p.TimelineId, p.FormatRevision, p.File, p.Size);

    private static BackupVerifyReport Convert(VerifyReport r)
    {
        static BackupVerifyFinding F(VerifyFinding f) => new(f.Code, (BackupFindingSeverity)f.Severity, f.File, f.PageId, f.Message);
        return new BackupVerifyReport((BackupVerifyLevel)r.Level, (BackupVerifyVerdict)r.Verdict,
            r.Packs.Select(p => new BackupPackVerification(p.File, p.BackupId, (BackupPackStatus)p.Status, p.Findings.Select(F).ToList())).ToList(),
            r.Points.Select(p => new BackupPointVerification(p.Id, p.Restorable, p.BrokenBy)).ToList(),
            r.StoreFindings.Select(F).ToList(), r.Duration, r.BytesRead, r.RemovedPoints);
    }
}
