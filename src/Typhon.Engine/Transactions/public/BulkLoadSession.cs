using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// An opt-in, throughput-first write path that skips per-row WAL and brackets a bulk with a manifest pair. Obtained via <c>DatabaseEngine.BeginBulkLoad</c>;
/// trades per-row durability for throughput on a strictly opt-in API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract:</b> the bulk is committed iff both <c>BulkBegin</c> (emitted at session open) and <c>BulkEnd</c> (emitted by <see cref="CompleteBulkLoad"/>)
/// reach the WAL FUA. On crash without <c>BulkEnd</c> durable, the bulk's UoW remains <c>Pending</c> in the registry and is voided on next reopen per
/// <b>UR-03</b> — none of the bulk's revisions become visible. Page allocations on a discarded bulk leak in v1; recovery's Phase 3b (P3) will reclaim them
/// when allocation tracking is added.
/// </para>
/// <para>
/// <b>Concurrency:</b> a bulk session is <i>exclusive</i> (only one per engine in v1) and <i>thread-affine</i> (only the thread that called
/// <c>BeginBulkLoad</c> may call methods on it). Regular <see cref="UnitOfWork"/>s continue to run during the session and see the pre-bulk MVCC snapshot.
/// </para>
/// <para>
/// <b>Implementation note (v1):</b> the bulk session wraps a regular <see cref="UnitOfWork"/> + a single <see cref="Transaction"/>, constructed with the
/// internal <c>SuppressWalSerialization</c> flag (BL-01). All MVCC plumbing (revision chains, EntityMap, indexes, UowRegistry) reuses the standard
/// infrastructure; the only divergence is that <c>PersistAndFinalize</c> skips <c>WalSerializer.SerializeToWal</c>. Pages still get dirty-marked, so
/// <see cref="CompleteBulkLoad"/>'s forced checkpoint flushes them.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class BulkLoadSession : IDisposable
{
    /// <summary>
    /// 64-bit id assigned at session open. Stable across <c>BulkBegin</c> and <c>BulkEnd</c>; used by recovery to identify the bulk.
    /// </summary>
    public long BulkSessionId { get; }

    /// <summary>
    /// LSN of the <c>BulkBegin</c> chunk emitted at session open. Set during construction; immutable for the session's lifetime. The matching <c>BulkEnd</c>
    /// chunk (emitted by <see cref="CompleteBulkLoad"/>) cross-references this value via <see cref="BulkManifestHeader.BulkBeginLsn"/>.
    /// </summary>
    public long BulkBeginLsn { get; }

    /// <summary>
    /// Options the session was opened with. Never <see langword="null"/> — defaults to <c>new BulkLoadOptions()</c> if the caller passed <see langword="null"/>
    /// to <c>BeginBulkLoad</c>.
    /// </summary>
    public BulkLoadOptions Options { get; }

    /// <summary>
    /// <see langword="true"/> after <see cref="CompleteBulkLoad"/> returned successfully, or after <see cref="Dispose"/> ran (discard path). Subsequent Spawn /
    /// Update / Destroy / <see cref="CompleteBulkLoad"/> calls throw <see cref="BulkSessionClosedException"/>.
    /// </summary>
    public bool IsClosed { get; private set; }

    /// <summary>Telemetry: entities spawned so far via <see cref="Spawn{TArch}"/>.</summary>
    public long EntitiesSpawned { get; private set; }

    /// <summary>Telemetry: entities updated so far via <see cref="Update{T}"/>.</summary>
    public long EntitiesUpdated { get; private set; }

    /// <summary>Telemetry: entities destroyed so far via <see cref="Destroy"/>.</summary>
    public long EntitiesDestroyed { get; private set; }

    internal BulkLoadSession(DatabaseEngine engine, BulkLoadOptions options, long bulkSessionId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        BulkSessionId = bulkSessionId;
        _engine = engine;

        if (engine.WalManager == null)
        {
            throw new InvalidOperationException(
                "BulkLoad requires a WAL-configured engine. The bulk path's durability contract (BulkBegin/BulkEnd manifest pair) depends on the WAL stream. " +
                "Configure WalWriterOptions on DatabaseEngineOptions when creating the engine.");
        }

        // Create the underlying UoW with SuppressWalSerialization=true. Deferred durability mode keeps per-tx commit latency low — the bulk's durability comes
        // from the explicit checkpoint barrier in CompleteBulkLoad, not per-row fsync.
        //
        // CRITICAL — transaction recycling for epoch release:
        // The bulk session shares one UoW across the whole bulk (BL-01 ties WAL suppression to the UoW), but it recycles the underlying Transaction every
        // TransactionRecycleThreshold spawns. Each Transaction holds an active epoch — without recycling, the cache cannot evict pages the transaction has
        // touched (PS-01: AccessEpoch < MinActiveEpoch is part of the eviction predicate), and at multi-million-entity scale the working set blows past the
        // cache budget → PageCacheBackpressureTimeoutException with "epoch-protected: <large>". Recycling = Commit + Dispose the current tx, open a new one.
        // The committed transaction's revisions stay invisible to other UoWs because the bulk's UoW remains Pending in UowRegistry until CompleteBulkLoad runs
        // Flush. See claude/design/Durability/BulkLoad/.
        _uow = engine.CreateUnitOfWorkForBulkLoad();
        _currentTransaction = _uow.CreateTransaction();
        _spawnsInCurrentTx = 0;

        // Emit BulkBegin placeholder. Body is just the BulkManifestHeader with PageRangeCount=0 (allocation
        // tracking deferred to P3 where the recovery consumer is). The LSN claimed here anchors the bulk in
        // the WAL stream.
        BulkBeginLsn = EmitBulkManifestChunk(WalChunkType.BulkBegin, isFinal: false);
    }

    /// <summary>
    /// Number of bulk operations between transaction recycles. Each recycle Commit + Dispose-s the current underlying transaction (releasing its epoch and
    /// unpinning every page it had read/written for the cache eviction predicate PS-01), then opens a new one. Matches the regular Fixture path's
    /// <c>BatchSize</c> so the back-pressure profile is identical between the bulk and standard paths.
    /// </summary>
    private const int TransactionRecycleThreshold = 5_000;

    /// <summary>
    /// Operations between independent <c>ChangeSet.ReleaseExcessDirtyMarks</c> calls — caps the per-page <c>DirtyCounter</c> at 1 and clears the tracking
    /// HashSet, preventing DC inflation in long sessions. Critical for the destroy phase: at low destroy rates (random-access cache-bound workload) the
    /// Transaction recycle is too infrequent to keep DC bounded, so this independent cleanup keeps the cache evictable. Matches the cadence used by
    /// <c>EntityAccessor.FlushAndRefreshEpoch</c> (128 ops).
    /// </summary>
    private const int DirtyMarkReleaseInterval = 128;

    /// <summary>
    /// Calls <c>ChangeSet.ReleaseExcessDirtyMarks</c> on the bulk's UoW every <see cref="DirtyMarkReleaseInterval"/> operations. This is independent of
    /// transaction recycling — it runs frequently enough to keep the page cache healthy even when the bulk's operation rate is too low for recycles to fire
    /// (e.g., the destroy phase's random-access cache-bound regime). Cheap: walks the ChangeSet's tracked-page set, capping each page's DC at 1.
    /// </summary>
    private void ReleaseDirtyMarksIfNeeded()
    {
        _opsSinceLastDirtyRelease++;
        if (_opsSinceLastDirtyRelease < DirtyMarkReleaseInterval)
        {
            return;
        }

        _uow.ChangeSet?.ReleaseExcessDirtyMarks();
        _opsSinceLastDirtyRelease = 0;
    }

    /// <summary>
    /// Ensures <see cref="_currentTransaction"/> is fresh: recycles it (Commit + Dispose, open new) when the per-tx op counter hits
    /// <see cref="TransactionRecycleThreshold"/>. The committed transaction's revisions remain MVCC-invisible to other UoWs because the bulk's UoW stays
    /// Pending in UowRegistry until <see cref="CompleteBulkLoad"/> flushes it.
    /// </summary>
    private void RecycleTransactionIfNeeded()
    {
        if (_spawnsInCurrentTx < TransactionRecycleThreshold)
        {
            return;
        }

        // Commit the current transaction — releases its epoch + transitions its revisions to Committed state (no WAL records emitted because
        // OwningUnitOfWork.SuppressWalSerialization=true). The page cache can now evict pages this transaction had touched once their DC drops to 0 (via
        // checkpoint).
        if (!_currentTransaction.Commit())
        {
            throw new InvalidOperationException("bulk transaction recycle commit failed");
        }
        _currentTransaction.Dispose();

        // Open the next transaction in the same UoW.
        _currentTransaction = _uow.CreateTransaction();
        _spawnsInCurrentTx = 0;
    }

    /// <summary>
    /// Spawn a new entity of archetype <typeparamref name="TArch"/>. Same signature shape as <see cref="Transaction.Spawn{TArch}(System.ReadOnlySpan{ComponentValue})"/>.
    /// Returns the new entity's engine-wide id.
    /// </summary>
    /// <typeparam name="TArch">Concrete archetype type.</typeparam>
    /// <param name="values">Initial component values; unspecified components take archetype defaults.</param>
    /// <returns>The newly-spawned entity's id.</returns>
    /// <exception cref="BulkSessionClosedException">Session has been completed or disposed.</exception>
    public EntityId Spawn<TArch>(params ReadOnlySpan<ComponentValue> values) where TArch : Archetype<TArch>
    {
        ThrowIfClosed();
        RecycleTransactionIfNeeded();
        var id = _currentTransaction.Spawn<TArch>(values);
        EntitiesSpawned++;
        _spawnsInCurrentTx++;
        ReleaseDirtyMarksIfNeeded();
        ReportProgressIfDue();
        return id;
    }

    /// <summary>
    /// Open an entity for mutation. Returned <see cref="EntityRef"/> behaves like the one from <see cref="Transaction.OpenMut"/>; use <c>Write&lt;T&gt;</c> on
    /// it to assign component values.
    /// </summary>
    /// <remarks>
    /// Per the bulk contract, the entity should have been spawned earlier in the same session. Bulk sessions are not designed to update entities that exist
    /// on disk pre-bulk (use the standard <see cref="UnitOfWork"/> path for that — the bulk path's durability boundary covers the whole session, so a
    /// pre-bulk entity update would lose atomicity vs. concurrent readers).
    /// </remarks>
    /// <exception cref="BulkSessionClosedException">Session has been completed or disposed.</exception>
    public EntityRef OpenMut(EntityId entity)
    {
        ThrowIfClosed();
        RecycleTransactionIfNeeded();
        return _currentTransaction.OpenMut(entity);
    }

    /// <summary>
    /// Bulk-update a component on an entity. Convenience for <c>OpenMut(entity).Write&lt;T&gt;()</c>.
    /// </summary>
    /// <typeparam name="T">Component type (must be <c>unmanaged</c> — blittable; matches engine convention).</typeparam>
    /// <param name="entity">Bulk-spawned entity id.</param>
    /// <param name="value">New component value.</param>
    /// <exception cref="BulkSessionClosedException">Session has been completed or disposed.</exception>
    public void Update<T>(EntityId entity, in T value) where T : unmanaged
    {
        ThrowIfClosed();
        RecycleTransactionIfNeeded();
        var er = _currentTransaction.OpenMut(entity);
        er.Write<T>() = value;
        EntitiesUpdated++;
        _spawnsInCurrentTx++;
        ReleaseDirtyMarksIfNeeded();
        ReportProgressIfDue();
    }

    /// <summary>
    /// Destroy an entity that was spawned earlier in this session.
    /// </summary>
    /// <param name="entity">Bulk-spawned entity id.</param>
    /// <exception cref="BulkSessionClosedException">Session has been completed or disposed.</exception>
    public void Destroy(EntityId entity)
    {
        ThrowIfClosed();
        RecycleTransactionIfNeeded();
        // Use the bulk fast path (skips IsAlive's random EntityMap lookup — see Transaction.DestroyBulk).
        // The bulk contract guarantees the entity was spawned earlier in this session and is alive.
        _currentTransaction.DestroyBulk(entity);
        EntitiesDestroyed++;
        _spawnsInCurrentTx++;
        ReleaseDirtyMarksIfNeeded();
        ReportProgressIfDue();
    }

    /// <summary>
    /// Synchronous durability barrier: commits the underlying transaction, flushes the UoW, runs a forced checkpoint (waited synchronously), emits the
    /// <c>BulkEnd</c> manifest, waits for <c>BulkEnd</c> durable, then returns. On return the bulk is fully committed and visible to subsequent transactions.
    /// </summary>
    /// <remarks>
    /// Implements the 6-step barrier from <c>claude/design/Durability/BulkLoad/02-write-path.md</c> + invariant <b>BL-04</b> in <c>claude/rules/durability.md</c>.
    /// </remarks>
    /// <exception cref="BulkSessionClosedException">Session has already been completed or disposed.</exception>
    /// <exception cref="BulkLoadCheckpointTimeoutException">Checkpoint did not complete within <see cref="BulkLoadOptions.CheckpointTimeout"/>; the session
    /// remains alive.</exception>
    public void CompleteBulkLoad()
    {
        ThrowIfClosed();

        // Step 1: Commit the FINAL transaction in the recycle chain (whatever's open right now). With SuppressWalSerialization=true, this transitions
        // revisions to Committed and marks pages dirty but emits ZERO Transaction WAL records (BL-01). Earlier transactions in the chain were already
        // committed + disposed by RecycleTransactionIfNeeded — their revisions are stamped with the bulk's UoW ID, still Pending in UowRegistry, hence
        // MVCC-invisible to other UoWs until uow.Flush below.
        if (!_currentTransaction.Commit())
        {
            throw new InvalidOperationException("bulk final transaction commit failed (concurrency conflict?) — recommend Dispose + retry");
        }

        // Step 2: Flush the UoW. Waits for any pending WAL records (BulkBegin) durable and transitions the UoW to WalDurable (records the commit in
        // UowRegistry).
        _uow.Flush();

        // Step 3: Force a checkpoint and block until at least one cycle completes. This drains every dirty page (including all bulk-allocated chunks) to disk
        // + advances CheckpointLSN past the bulk anchor.
        _engine.CheckpointManager.ForceCheckpoint();
        if (!_engine.CheckpointManager.WaitForCheckpoint(Options.CheckpointTimeout))
        {
            throw new BulkLoadCheckpointTimeoutException(BulkSessionId, Options.CheckpointTimeout);
        }

        // Step 4: Verify CheckpointLSN advanced past the bulk anchor. Defensive: if the checkpoint completed but CheckpointLSN somehow didn't move past
        // BulkBeginLsn, we cannot safely emit BulkEnd.
        if (_engine.CheckpointManager.CheckpointLsn < BulkBeginLsn)
        {
            throw new InvalidOperationException(
                $"checkpoint completed but CheckpointLSN ({_engine.CheckpointManager.CheckpointLsn}) did not advance past bulk anchor ({BulkBeginLsn})");
        }

        // Step 5: Emit BulkEnd chunk carrying the final manifest (entity counters; PageRangeCount=0 in v1).
        var bulkEndLsn = EmitBulkManifestChunk(WalChunkType.BulkEnd, isFinal: true);

        // Step 6: Wait for BulkEnd LSN to be durable.
        var wc = WaitContext.FromTimeout(Options.CheckpointTimeout);
        _engine.WalManager.RequestFlush();
        _engine.WalManager.WaitForDurable(bulkEndLsn, ref wc);

        // Tear down the Transaction + UoW (releases the registry slot, etc.) and the bulk gate.
        _currentTransaction.Dispose();
        _uow.Dispose();
        IsClosed = true;
        _engine.ReleaseBulkSessionGate();
    }

    /// <summary>
    /// Discard the bulk session. If <see cref="CompleteBulkLoad"/> was not called, the underlying transaction is rolled back — none of the bulk's revisions
    /// become visible to other UoWs. The bulk's UoW remains <c>Pending</c> in the registry and is voided on next reopen per <b>UR-03</b>; no <c>BulkEnd</c>
    /// chunk is emitted, so recovery treats the bulk as if it never completed.
    /// </summary>
    /// <remarks>
    /// <para>v1 limitation: pages allocated during a discarded bulk leak (occupied in the bitmap, no visible data) until P3's recovery Phase 3b adds
    /// allocation tracking + reclamation.</para>
    /// <para>Calling <see cref="Dispose"/> after a successful <see cref="CompleteBulkLoad"/> is a no-op.</para>
    /// </remarks>
    public void Dispose()
    {
        if (IsClosed)
        {
            // Still ensure the IDisposable fields are released even on the no-op closed path. Both Transaction
            // and UnitOfWork are designed to be Dispose-idempotent.
            _currentTransaction?.Dispose();
            _uow?.Dispose();
            return;
        }

        try
        {
            // Rollback the CURRENT transaction. Earlier transactions in the recycle chain already committed (releasing their epochs), but their revisions are
            // stamped with the bulk's UoW ID — that UoW stays Pending in UowRegistry (we never run uow.Flush on the discard path), so on reopen
            // VoidRemainingPending voids the whole UoW and ALL the bulk's revisions become invisible per UR-03 / UR-05. Page allocations leak (v1 limitation
            // — see 03-recovery.md "v1 deferred").
            _currentTransaction?.Rollback();
        }
        catch
        {
            // Best-effort discard; never let cleanup escape.
        }

        try
        {
            _currentTransaction?.Dispose();
        }
        catch
        {
            // Best-effort discard.
        }

        try
        {
            _uow?.Dispose();
        }
        catch
        {
            // Best-effort discard.
        }

        IsClosed = true;
        _engine.ReleaseBulkSessionGate();
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new BulkSessionClosedException(BulkSessionId);
        }
    }

    private void ReportProgressIfDue()
    {
        var reporter = Options.ProgressReporter;
        if (reporter == null)
        {
            return;
        }

        var total = EntitiesSpawned + EntitiesUpdated + EntitiesDestroyed;
        if (total % Options.ProgressBatchSize == 0)
        {
            reporter(new BulkLoadProgress
            {
                EntitiesSpawned = EntitiesSpawned,
                EntitiesUpdated = EntitiesUpdated,
                EntitiesDestroyed = EntitiesDestroyed,
                PagesAllocated = 0, // v1: not tracked granularly (deferred to P3)
                ElapsedMilliseconds = 0, // v1: not tracked (could be added in P5 perf work)
            });
        }
    }

    /// <summary>
    /// Emits a <c>BulkBegin</c> or <c>BulkEnd</c> chunk into the WAL ring buffer. Returns the chunk's LSN. Body is a single <see cref="BulkManifestHeader"/>;
    /// v1 carries no page-range entries (PageRangeCount=0).
    /// </summary>
    private long EmitBulkManifestChunk(WalChunkType chunkType, bool isFinal)
    {
        const int bodySize = BulkManifestHeader.SizeInBytes;
        const int chunkSize = WalChunkHeader.SizeInBytes + bodySize + WalChunkFooter.SizeInBytes;

        var commitBuffer = _engine.WalManager.CommitBuffer;
        var wc = WaitContext.FromTimeout(Options.CheckpointTimeout);
        var claim = commitBuffer.TryClaim(chunkSize, recordCount: 1, ref wc);
        if (!claim.IsValid)
        {
            throw new InvalidOperationException($"could not claim WAL space for {chunkType} manifest");
        }

        try
        {
            var lsn = claim.FirstLSN;

            var chunkHeader = new WalChunkHeader
            {
                ChunkType = (ushort)chunkType,
                ChunkSize = (ushort)chunkSize,
                PrevCRC = 0, // patched by WAL writer
            };
            MemoryMarshal.Write(claim.DataSpan, in chunkHeader);

            var manifest = new BulkManifestHeader
            {
                Lsn = lsn, // WP-07: at body offset 0
                BulkSessionId = BulkSessionId,
                BulkBeginLsn = chunkType == WalChunkType.BulkBegin ? lsn : BulkBeginLsn,
                SegmentCount = 0, // v1: allocation tracking deferred to P3
                PageRangeCount = 0,
                EntitiesSpawned = isFinal ? EntitiesSpawned : 0,
                EntitiesUpdated = isFinal ? EntitiesUpdated : 0,
                EntitiesDestroyed = isFinal ? EntitiesDestroyed : 0,
            };
            MemoryMarshal.Write(claim.DataSpan.Slice(WalChunkHeader.SizeInBytes), in manifest);

            var footer = new WalChunkFooter { CRC = 0 }; // patched by WAL writer
            MemoryMarshal.Write(claim.DataSpan.Slice(chunkSize - WalChunkFooter.SizeInBytes), in footer);

            commitBuffer.Publish(ref claim);
            return lsn;
        }
        catch
        {
            commitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }

    private readonly DatabaseEngine _engine;
    private readonly UnitOfWork _uow;

    /// <summary>
    /// The current underlying transaction in the recycle chain. Replaced every <see cref="TransactionRecycleThreshold"/> spawns via
    /// <see cref="RecycleTransactionIfNeeded"/>; that recycle is what releases the per-transaction epoch and lets the cache evict pages this transaction had
    /// touched (PS-01).
    /// </summary>
    private Transaction _currentTransaction;
    private int _spawnsInCurrentTx;

    /// <summary>
    /// Operations since the last <c>ChangeSet.ReleaseExcessDirtyMarks</c> call. Independent of <see cref="_spawnsInCurrentTx"/> — the dirty-mark release runs
    /// at a much finer cadence (<see cref="DirtyMarkReleaseInterval"/> ops) than the transaction recycle, because at low operation rates the recycle is too
    /// infrequent to keep the cache healthy on its own.
    /// </summary>
    private int _opsSinceLastDirtyRelease;
}
