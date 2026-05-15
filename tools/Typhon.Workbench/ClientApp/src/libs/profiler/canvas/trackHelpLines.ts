/**
 * Per-track "?" help lines — what each TimeArea section shows and how to read it. Originally
 * ported verbatim from the retired `Typhon.Profiler.Server/ClientApp/src/GraphArea.tsx::getTrackHelpLines`
 * so the explanations match the original profiler exactly.
 *
 * Data-only module, no React / canvas imports. Consumers:
 *   - `panels/profiler/sections/TimeArea.tsx` — renders the "?" glyph on each track's gutter and
 *     the DOM overlay on hover.
 *   - any future Detail-panel help surface.
 */

export function getTrackHelpLines(trackId: string, slotLabel: string): string[] {
  if (trackId.startsWith('slot-')) {
    return [
      `Thread lane — ${slotLabel}`,
      '',
      'What\'s drawn:',
      '  Top strip (if present): scheduler chunks — one colored bar per',
      '    ECS system execution bound to this worker thread within the',
      '    current tick. Color is a stable hash of the system index.',
      '  Below: nested spans drawn as a flame graph, one row per depth,',
      '    colored by a stable hash of the span name.',
      '',
      'How to read it:',
      '  Bar width is proportional to duration (X = µs).',
      '  Depth increases downward — depth 0 is the outermost instrumented',
      '    operation (e.g. Transaction.Commit), deeper rows are its nested',
      '    calls (BTree.Insert, PagedMMF.GetPage, ...).',
      '  Adjacent <=1 px spans at the same depth are coalesced into a grey',
      '    block labeled "N spans — zoom in" so the row stays readable at',
      '    wide time ranges. Zoom in to resolve individual spans.',
      '',
      'Interaction:',
      '  Click a chunk or span → details in the right pane.',
      '  Double-click → animated zoom to that span\'s time range.',
    ];
  }

  if (trackId.startsWith('system-')) {
    return [
      `System lane — ${slotLabel}`,
      '',
      'What\'s drawn:',
      '  One bar per scheduler chunk that ran this ECS system in the',
      '  current viewport, regardless of which thread it landed on.',
      '  Colored by system index (same palette as the per-thread chunk',
      '  bars above, so the same system keeps its identity across views).',
      '',
      'How to read it:',
      '  Width ∝ duration. Many small bars spread across time = system',
      '    ran in parallel across multiple threads.',
      '  One wide bar per tick = system is serial-only.',
      '',
      'Interaction:',
      '  Click a chunk → details in the right pane.',
      '  Double-click → animated zoom to that chunk\'s time range.',
    ];
  }

  switch (trackId) {
    case 'gauge-memory':
      return [
        'Memory gauge',
        '',
        'Total process memory footprint — managed (GC heap), unmanaged',
        '(NativeMemory via PinnedMemoryBlock, buffer pools, pinned page',
        'cache), and GC activity overlays. Snapshot sampled once per',
        'scheduler tick.',
        '',
        'What\'s drawn:',
        '  Row 1 (top) — stacked-area of heap gens, bottom-to-top:',
        '    Gen0, Gen1, Gen2, LOH, POH — managed heap generations.',
        '  Row 2 (middle) — unmanaged area chart + dashed peak reference',
        '    line. The dashed line sits at the session\'s observed peak;',
        '    it only moves up, never down. When the area touches the',
        '    dashed line, you\'re at a new peak.',
        '  Row 3 (bottom) — live-block count line (thin grey) + one marker',
        '    glyph per allocation / free event. Triangle-UP = alloc,',
        '    triangle-DOWN = free. Color encodes the source subsystem',
        '    (WAL / PageCache / StagingPool / ComponentTable / etc. —',
        '    see MemoryAllocSource enum).',
        '  GC overlay (covers all three rows):',
        '    Red-orange rectangle — one per GC suspension, X-extent =',
        '      actual EE stop-the-world window (start..start+duration).',
        '      The rectangle\'s width on screen is the real pause length.',
        '    Triangle marker — GcStart event, colored by generation:',
        '      Gen0 teal / Gen1 green / Gen2+ yellow.',
        '    Dot marker — GcEnd event, same generation palette.',
        '',
        'How to read it:',
        '  Total height (rows 1+2) = current resident working set.',
        '  Unmanaged slice climbing = ComponentTable growth, page cache',
        '    expansion, or buffer-pool churn.',
        '  A red rectangle visibly wider than ~1 ms is a stop-the-world',
        '    pause worth investigating — read it directly off the X-axis.',
        '  Gen2+ markers are rare but usually accompany the wider rects',
        '    (big pauses) and a drop in the Gen2 stacked area.',
        '',
        'Interaction:',
        '  Click any marker → detail pane shows the event\'s direction',
        '    (Alloc / Free), source subsystem, size, running total after',
        '    the event, and the thread slot the allocation happened on.',
        '  Hover near a GC suspension → tooltip surfaces the pause',
        '    duration and the nearest GC start/end event.',
      ];

    case 'gauge-persistence':
      return [
        'Page Cache gauge',
        '',
        'Per-tick snapshot of the PagedMMF cache bucket population.',
        'Total height = the cache\'s fixed page capacity.',
        '',
        'What\'s drawn (stacked, bottom-to-top, mutually exclusive):',
        '  Free        — pages currently unallocated; pool residue.',
        '  CleanUsed   — resident + fully flushed to disk; safe to evict.',
        '  DirtyUsed   — resident, not yet flushed; DC > 0 blocks eviction',
        '                until checkpoint drains them.',
        '  Exclusive   — pinned by an active UoW (ACW > 0). Neither',
        '                evictable nor visible to the checkpoint snapshot.',
        '',
        'How to read it:',
        '  DirtyUsed climbing = checkpoint is not draining fast enough;',
        '    you\'re approaching the dirty-page backpressure point.',
        '  Exclusive steady & small = normal transactional workload.',
        '  Exclusive climbing = a long-running UoW is holding pages; if',
        '    it keeps climbing, suspect a stuck transaction.',
        '  Free shrinking toward 0 = cache is near capacity — next page',
        '    fault triggers eviction of a CleanUsed page.',
      ];

    case 'gauge-transient':
      return [
        'Transient Store gauge',
        '',
        'Per-tick snapshot of the pinned-heap bytes currently held by',
        'every live TransientStore. Transient storage is the heap-backed',
        '(non-persisted) page pool behind StorageMode.Transient — no WAL,',
        'no checkpoint, no MVCC revision chain, no page-cache eviction.',
        '',
        'What\'s drawn:',
        '  One stacked area: aggregated "Used" bytes across all live',
        '    transient stores, sampled once per scheduler tick.',
        '  Y axis auto-scales to the max observed value. There is NO',
        '    capacity reference line — each TransientStore has its own',
        '    TransientOptions.MaxMemoryBytes cap; a per-store ceiling',
        '    on aggregated "used" would mislead.',
        '',
        'How to read it:',
        '  Monotonic climbing = transient pages are being allocated but',
        '    nothing is releasing them (stores only free on Dispose).',
        '  Sudden drop = a ComponentTable or engine was disposed.',
      ];

    case 'gauge-wal':
      return [
        'WAL gauge',
        '',
        'Per-tick snapshot of the Write-Ahead Log state. Drawn as two',
        'stacked sub-rows sharing the viewport\'s X axis.',
        '',
        'Row 1 (top — area chart):',
        '  Commit buffer bytes — how much of WalManager.CommitBuffer is',
        '    currently occupied (rising between fsyncs, dropping when a',
        '    flush completes and frees frames).',
        '  Dashed line — commit-buffer capacity (fixed at init).',
        '',
        'Row 2 (bottom — line chart):',
        '  Mustard — Inflight frames: submitted but not yet durable.',
        '    Nonzero = fsync is in flight or queued.',
        '  Green   — Staging rented: buffers currently borrowed from',
        '    StagingBufferPool.',
        '  Dashed yellow — Staging peak: session high-water mark.',
        '',
        'How to read it:',
        '  Row 1 area climbing toward the dashed capacity line = commits',
        '    are outpacing fsync cadence; commits may stall on back-pressure.',
        '  Row 2 mustard climbing above green = lots of frames in flight',
        '    per staging rental (big batches).',
      ];

    case 'gauge-tx-uow':
      return [
        'Transactions + UoW gauge',
        '',
        'Per-tick snapshot of the transaction / unit-of-work subsystem.',
        'Drawn as two stacked sub-rows.',
        '',
        'Row 1 (top — Tx):',
        '  Navy area — Tx active count: transactions currently open.',
        '  Blue   line — Tx created/tick.',
        '  Green  line — Tx commits/tick.',
        '  Yellow line — Tx rollbacks/tick.',
        '',
        'Row 2 (bottom — UoW):',
        '  Deep-indigo area — UoW active count.',
        '  Teal    line — UoW created/tick.',
        '  Mustard line — UoW committed/tick.',
        '',
        'How to read it:',
        '  Tx active area tracks the scheduler\'s system count in steady',
        '    state — big divergence = systems skipping their transactions.',
        '  Commits close to created = healthy throughput. Gap between them',
        '    = long-lived Tx holding work.',
        '  Rollbacks should be near zero in healthy workloads.',
      ];

    case 'phases':
      return [
        'Phases',
        '',
        'Scheduler phase timeline. Each tick is divided into phases by the',
        'DagScheduler: Parallel, Serial, Writer, Barrier, and the checkpoint',
        'phases. Phase bars show when each phase started and how long it ran.',
        '',
        'What\'s drawn:',
        '  One horizontal bar per phase per tick, colored with the scheduler',
        '  accent. Width ∝ duration.',
        '  When the bar is wider than ~50 px, the phase name + duration is',
        '  rendered inline.',
        '',
        'How to read it:',
        '  Parallel phase is typically the widest — that\'s where worker',
        '    threads execute non-conflicting systems in parallel.',
        '  Serial / Writer phase narrow = low exclusive write load.',
        '  Barrier phase narrow = good; wide = many systems waiting on',
        '    the previous phase to drain.',
      ];

    case 'page-cache':
      return [
        'Page Cache operations',
        '',
        'Four mini-rows of discrete PagedMMF cache events. Complements the',
        'Page Cache gauge above (which shows bucket populations) by showing',
        'the events that MOVE pages between buckets.',
        '',
        'Rows (top-to-bottom):',
        '  Fetch    — a thread requested a resident page. Fast path, no disk.',
        '  Allocate — a new page was allocated (page fault or segment Grow()).',
        '  Evicted  — a CleanUsed page was reclaimed to make room.',
        '  Flush    — a DirtyUsed page was written to disk by checkpoint.',
        '',
        'How to read it:',
        '  Evicted bars appearing = cache is pressure-eviction driven.',
        '  Flush bars clustered = checkpoint cycle is running.',
      ];

    case 'disk-io':
      return [
        'Disk IO',
        '',
        'Raw disk read/write operations. Two mini-rows:',
        '  Reads   — page-fault reads pulling data into the cache.',
        '  Writes  — page flushes + WAL fsyncs writing out to disk.',
        '',
        'How to read it:',
        '  Clusters of writes = checkpoint in progress or WAL fsync batching.',
        '  Reads in steady state = cache miss pressure; consider a larger cache.',
      ];

    case 'transactions':
      return [
        'Transactions',
        '',
        'Discrete transaction lifecycle events. Three mini-rows:',
        '  Commits    — Transaction.Commit spans (hot path for writes).',
        '  Rollbacks  — Transaction.Rollback spans (error / conflict path).',
        '  Persists   — WAL persistence call — point at which the commit',
        '               becomes durable from the writer\'s perspective.',
        '',
        'How to read it:',
        '  Commit width = full commit duration (includes WAL wait).',
        '    Wide bars = WAL back-pressure or lock contention.',
        '  Rollback bars should be rare in healthy workloads.',
        '  The gap between a Commit and its Persist = WAL fsync wait.',
      ];

    case 'wal':
      return [
        'WAL operations',
        '',
        'Two mini-rows of Write-Ahead Log events:',
        '  Flushes  — WAL flush spans. Duration = fsync cost on this tick.',
        '  Waits    — threads blocked waiting for a flush to complete.',
        '',
        'How to read it:',
        '  Flush bars wide = slow fsync (disk latency, OS page-cache pressure).',
        '  Waits stacking up = commits queueing behind a slow flush; commit',
        '    latency tail will show in the Transactions row.',
      ];

    case 'checkpoint':
      return [
        'Checkpoint cycles',
        '',
        'One mini-row showing each complete checkpoint pass — snapshot',
        '→ flush-dirty → WAL-segment-recycle. Typhon runs checkpoints',
        'concurrently with application ticks; a cycle\'s duration spans',
        'many ticks.',
        '',
        'How to read it:',
        '  Bar start = snapshot begin.',
        '  Bar end   = last dirty page flushed + WAL segments recycled.',
        '  Long bars = lots of DirtyUsed pages to flush; see Page Cache',
        '    gauge for confirmation.',
      ];

    default:
      return [
        'Track',
        '',
        'Hover a bar or event to see details. No help available for this',
        'track yet.',
      ];
  }
}
