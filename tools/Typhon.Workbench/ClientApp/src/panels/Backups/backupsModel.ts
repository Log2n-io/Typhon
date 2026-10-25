import type {
  BackupArchiveDto,
  BackupArchiveGapDto,
  BackupDrillReportDto,
  BackupJobDto,
  BackupListingDto,
  BackupPointDto,
  BackupRestorePlanDto,
  BackupRetentionPlanDto,
  BackupStatusDto,
  BackupVerifyReportDto,
} from '@/api/generated/model';
import type { StatusTone } from '@/components/ui/status-badge';

/**
 * Normalized mirrors of the Backups DTOs (#1417), plus the layout rules the view draws with: chain ribbons, the archive coverage bar, health tones,
 * job progress, and "what is lost after" text. Pure functions — the panel renders, this decides.
 *
 * Orval emits `string | null` for every C# string and `number | string` for every integer, so every value is narrowed here, once. Engine enums
 * arrive as their names; a name this build does not know is kept and shown as itself (never mapped to a wrong word).
 */

const n = (v: number | string | null | undefined): number => (v == null ? 0 : typeof v === 'number' ? v : Number(v));
const s = (v: string | null | undefined): string => v ?? '';

// ── Health ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/** The engine's BackupHealth names; any other string is a newer engine's and renders as itself. */
export type Health = 'Off' | 'Ok' | 'Warning' | 'Degraded' | 'Failing';

const HEALTH_TONE: Readonly<Record<Health, StatusTone>> = {
  Off: 'neutral',
  Ok: 'success',
  Warning: 'warn',
  Degraded: 'warn',
  Failing: 'error',
};

export function healthTone(health: string): StatusTone {
  return HEALTH_TONE[health as Health] ?? 'neutral';
}

/** The label of a health name: known names as they are, an unknown one as itself, an empty one as "Unknown". */
export function healthLabel(health: string): string {
  return health || 'Unknown';
}

// ── Points and chains ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export interface Point {
  backupId: number;
  kind: string;
  chainId: string;
  parentId: number;
  baseId: number;
  checkpointLsn: number;
  pointLsn: number;
  imagePages: number;
  pageCount: number;
  tailSegments: number;
  suspect: boolean;
  offline: boolean;
  freezeUtc: string | null;
  tailEndUtc: string | null;
  timelineId: string;
  formatRevision: number;
  packFile: string;
  packBytes: number;
}

export function normalizePoint(p: BackupPointDto | null | undefined): Point | null {
  if (!p) {
    return null;
  }
  return {
    backupId: n(p.backupId),
    kind: s(p.kind),
    chainId: s(p.chainId),
    parentId: n(p.parentId),
    baseId: n(p.baseId),
    checkpointLsn: n(p.checkpointLsn),
    pointLsn: n(p.pointLsn),
    imagePages: n(p.imagePages),
    pageCount: n(p.pageCount),
    tailSegments: n(p.tailSegments),
    suspect: !!p.suspect,
    offline: !!p.offline,
    freezeUtc: p.freezeUtc ?? null,
    tailEndUtc: p.tailEndUtc ?? null,
    timelineId: s(p.timelineId),
    formatRevision: n(p.formatRevision),
    packFile: s(p.packFile),
    packBytes: n(p.packBytes),
  };
}

export interface Drill {
  backupId: number;
  utc: string | null;
  outcome: string;
  error: string | null;
}

/** A drill's ✓ / ✗: a pass with warnings is still a pass; anything else that is recorded is a failure (an Error drill is never recorded). */
export function drillPassed(d: Drill | undefined): boolean | null {
  if (!d) {
    return null;
  }
  return d.outcome === 'Passed' || d.outcome === 'PassedWithWarnings';
}

export interface Listing {
  storeId: string;
  databaseId: string;
  points: Point[];
  chains: { chainId: string; baseId: number; headBackupId: number; depth: number; headDepth: number }[];
  unreadable: { file: string; reason: string }[];
  temporaryFiles: string[];
  /** Null when not asked for (the restorable set costs a quick verify). */
  restorable: number[] | null;
  drills: Map<number, Drill>;
}

export function normalizeListing(l: BackupListingDto): Listing {
  const drills = new Map<number, Drill>();
  for (const d of l.drills ?? []) {
    drills.set(n(d.backupId), {
      backupId: n(d.backupId),
      utc: d.utc ?? null,
      outcome: s(d.outcome),
      error: d.error ?? null,
    });
  }
  return {
    storeId: s(l.storeId),
    databaseId: s(l.databaseId),
    points: (l.points ?? []).map(normalizePoint).filter((p): p is Point => p !== null),
    chains: (l.chains ?? []).map((c) => ({
      chainId: s(c.chainId),
      baseId: n(c.baseId),
      headBackupId: n(c.headBackupId),
      depth: n(c.depth),
      headDepth: n(c.headDepth),
    })),
    unreadable: (l.unreadablePacks ?? []).map((u) => ({ file: s(u.file), reason: s(u.reason) })),
    temporaryFiles: (l.temporaryFiles ?? []).map(s),
    restorable: l.restorablePoints == null ? null : l.restorablePoints.map(n),
    drills,
  };
}

/** One point on a ribbon: the pack that stands for its id (a compacted one preferred, 05 §2), with its marks. */
export interface RibbonPoint {
  point: Point;
  /** The id also has a compacted pack (`-C`) standing for it. */
  compacted: boolean;
  /** ✓ / ✗ of its newest drill; null when never drilled. */
  drill: boolean | null;
  /** False when the restorable set is known and does not hold it; null when unknown. */
  restorable: boolean | null;
}

/** One chain, base → head. */
export interface Ribbon {
  chainId: string;
  baseId: number;
  points: RibbonPoint[];
}

/**
 * The timeline's ribbons: one per chain (in base order), points base → head. An id listed twice (its original `-I` and its compacted `-C`) shows once,
 * as the compacted pack — the one a restore reads — marked compacted.
 */
export function buildRibbons(listing: Listing): Ribbon[] {
  const byChain = new Map<string, Map<number, RibbonPoint>>();
  for (const p of listing.points) {
    const chain = byChain.get(p.chainId) ?? new Map<number, RibbonPoint>();
    byChain.set(p.chainId, chain);
    const existing = chain.get(p.backupId);
    const restorable = listing.restorable == null ? null : listing.restorable.includes(p.backupId);
    const entry: RibbonPoint = {
      point: p,
      compacted: p.kind === 'Compacted',
      drill: drillPassed(listing.drills.get(p.backupId)),
      restorable,
    };
    if (!existing) {
      chain.set(p.backupId, entry);
    } else if (p.kind === 'Compacted') {
      chain.set(p.backupId, { ...entry, compacted: true });
    } else {
      existing.compacted ||= existing.point.kind === 'Compacted';
    }
  }

  const ribbons: Ribbon[] = [];
  for (const [chainId, points] of byChain) {
    const ordered = [...points.values()].sort((a, b) => a.point.backupId - b.point.backupId);
    ribbons.push({ chainId, baseId: ordered[0]?.point.baseId ?? 0, points: ordered });
  }
  return ribbons.sort((a, b) => (a.points[0]?.point.backupId ?? 0) - (b.points[0]?.point.backupId ?? 0));
}

// ── Archive coverage ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export interface Gap {
  fromLsn: number;
  toLsn: number;
  recordedUtc: string | null;
  reason: string;
  covered: boolean;
}

const gap = (g: BackupArchiveGapDto): Gap => ({
  fromLsn: n(g.fromLsn),
  toLsn: n(g.toLsn),
  recordedUtc: g.recordedUtc ?? null,
  reason: s(g.reason),
  covered: !!g.covered,
});

export interface ArchiveTimeline {
  timelineId: string;
  parentTimelineId: string | null;
  originLsn: number;
  reason: string;
  archivedThroughLsn: number;
  segments: {
    segmentId: number;
    firstLsn: number;
    lastLsn: number;
    sealedUtc: string | null;
    bytes: number;
    flags: string;
  }[];
  gaps: Gap[];
  samples: { lsn: number; utc: string }[];
}

export function normalizeArchive(a: BackupArchiveDto): ArchiveTimeline[] {
  return (a.timelines ?? []).map((t) => ({
    timelineId: s(t.timelineId),
    parentTimelineId: t.parentTimelineId ?? null,
    originLsn: n(t.originLsn),
    reason: s(t.reason),
    archivedThroughLsn: n(t.archivedThroughLsn),
    segments: (t.segments ?? []).map((g) => ({
      segmentId: n(g.segmentId),
      firstLsn: n(g.firstLsn),
      lastLsn: n(g.lastLsn),
      sealedUtc: g.sealedUtc ?? null,
      bytes: n(g.bytes),
      flags: s(g.flags),
    })),
    gaps: (t.gaps ?? []).map(gap),
    samples: (t.samples ?? []).map((x) => ({ lsn: n(x.lsn), utc: x.utc })),
  }));
}

/** A run of the coverage bar, as fractions of the bar's LSN domain. */
export interface CoverageRun {
  kind: 'archived' | 'gap';
  from: number;
  to: number;
  fromLsn: number;
  toLsn: number;
  covered?: boolean;
}

export interface Coverage {
  minLsn: number;
  maxLsn: number;
  runs: CoverageRun[];
  /** Points of this timeline, placed at their P. */
  ticks: { backupId: number; at: number; lsn: number }[];
}

/**
 * The Layer 2 bar of one timeline: archived LSN ranges (contiguous segments merged), gaps in red, and a tick per point at its P. The domain is from the
 * first LSN known (archive or point) to the last; a domain of zero width (one LSN, or nothing) yields no runs.
 */
export function coverageOf(timeline: ArchiveTimeline, points: Point[]): Coverage {
  const own = points.filter((p) => p.timelineId === timeline.timelineId);
  const lsns = [
    ...timeline.segments.flatMap((g) => [g.firstLsn, g.lastLsn]),
    ...timeline.gaps.flatMap((g) => [g.fromLsn, g.toLsn]),
    ...own.map((p) => p.pointLsn),
    timeline.archivedThroughLsn,
  ].filter((x) => x > 0);
  const minLsn = lsns.length ? Math.min(...lsns) : 0;
  const maxLsn = lsns.length ? Math.max(...lsns) : 0;
  const width = maxLsn - minLsn;
  const at = (lsn: number) => (width <= 0 ? 0 : Math.min(1, Math.max(0, (lsn - minLsn) / width)));

  const runs: CoverageRun[] = [];
  const segments = [...timeline.segments].sort((a, b) => a.firstLsn - b.firstLsn);
  for (const g of segments) {
    const last = runs[runs.length - 1];
    if (last && last.kind === 'archived' && g.firstLsn <= last.toLsn + 1) {
      last.toLsn = Math.max(last.toLsn, g.lastLsn);
      last.to = at(last.toLsn);
    } else {
      runs.push({ kind: 'archived', fromLsn: g.firstLsn, toLsn: g.lastLsn, from: at(g.firstLsn), to: at(g.lastLsn) });
    }
  }
  for (const g of timeline.gaps) {
    runs.push({
      kind: 'gap',
      fromLsn: g.fromLsn,
      toLsn: g.toLsn,
      from: at(g.fromLsn),
      to: at(g.toLsn),
      covered: g.covered,
    });
  }
  if (width <= 0) {
    runs.length = 0;
  }

  return {
    minLsn,
    maxLsn,
    runs,
    ticks: own.map((p) => ({ backupId: p.backupId, at: at(p.pointLsn), lsn: p.pointLsn })).sort((a, b) => a.at - b.at),
  };
}

// ── Status ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export type RouteKind = 'inProcess' | 'request' | 'offline' | 'unavailable';

export interface Status {
  store: string;
  bundle: string | null;
  storeStatus: {
    health: string;
    reason: string;
    points: number;
    latest: Point | null;
    latestRestorable: Point | null;
  } | null;
  storeError: string | null;
  engine: {
    live: boolean;
    pid: number;
    machine: string;
    storePath: string;
    health: string;
    reason: string;
    state: string;
    phase: string;
    progressPages: number;
    progressOfPages: number;
    lastPoint: Point | null;
    lastSuccessUtc: string | null;
    nextDueUtc: string | null;
    consecutiveFailures: number;
    lastFailure: string | null;
    lastDrill: Drill | null;
    archive: {
      state: string;
      lagBytes: number;
      lagSeconds: number;
      lastGapFromLsn: number;
      lastGapToLsn: number;
      lastError: string | null;
    };
  } | null;
  route: { kind: RouteKind | string; reason: string; sessionId: string | null };
  runningJobId: string | null;
  defaultScratch: string | null;
}

export function normalizeStatus(d: BackupStatusDto): Status {
  const e = d.engine;
  const ld = e?.lastDrill;
  return {
    store: s(d.store),
    bundle: d.bundle ?? null,
    storeStatus: d.storeStatus
      ? {
          health: s(d.storeStatus.health),
          reason: s(d.storeStatus.reason),
          points: n(d.storeStatus.points),
          latest: normalizePoint(d.storeStatus.latest),
          latestRestorable: normalizePoint(d.storeStatus.latestRestorable),
        }
      : null,
    storeError: d.storeError ?? null,
    engine: e
      ? {
          live: !!e.engineLive,
          pid: n(e.pid),
          machine: s(e.machine),
          storePath: s(e.storePath),
          health: s(e.health),
          reason: s(e.reasonText),
          state: s(e.state),
          phase: s(e.phase),
          progressPages: n(e.progressPages),
          progressOfPages: n(e.progressOfPages),
          lastPoint: normalizePoint(e.lastPoint),
          lastSuccessUtc: e.lastSuccessUtc ?? null,
          nextDueUtc: e.nextDueUtc ?? null,
          consecutiveFailures: n(e.consecutiveFailures),
          lastFailure: e.lastFailure?.message ?? null,
          lastDrill: ld
            ? { backupId: n(ld.backupId), utc: ld.utc ?? null, outcome: s(ld.outcome), error: ld.error ?? null }
            : null,
          archive: {
            state: s(e.archive?.state),
            lagBytes: n(e.archive?.lagBytes),
            lagSeconds: n(e.archive?.lagSeconds),
            lastGapFromLsn: n(e.archive?.lastGapFromLsn),
            lastGapToLsn: n(e.archive?.lastGapToLsn),
            lastError: e.archive?.lastError ?? null,
          },
        }
      : null,
    route: {
      kind: s(d.route?.kind) || 'unavailable',
      reason: s(d.route?.reason),
      sessionId: d.route?.sessionId ?? null,
    },
    runningJobId: d.runningJobId ?? null,
    defaultScratch: d.defaultScratch ?? null,
  };
}

/**
 * The health card's verdict: the engine's, when its status file reports one (it judges schedule, archive and drills too), else the store's own from
 * its packs (07 §6). Null when neither is known.
 */
export function cardHealth(status: Status): { health: string; reason: string; source: 'engine' | 'store' } | null {
  if (status.engine && status.engine.health && status.engine.health !== 'Off') {
    const reason = status.engine.live
      ? status.engine.reason
      : `engine not running; last known state: ${status.engine.reason}`;
    return { health: status.engine.health, reason, source: 'engine' };
  }
  if (status.storeStatus) {
    return { health: status.storeStatus.health, reason: status.storeStatus.reason, source: 'store' };
  }
  return null;
}

// ── Jobs ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export type JobState = 'queued' | 'running' | 'done' | 'error' | 'cancelled';

export interface Job {
  jobId: string;
  kind: string;
  store: string;
  state: JobState | string;
  phase: string;
  completed: number;
  total: number;
  unit: string | null;
  startedUtc: string;
  endedUtc: string | null;
  route: string | null;
  note: string | null;
  error: { category: string; message: string } | null;
  raw: BackupJobDto;
}

export function normalizeJob(j: BackupJobDto): Job {
  return {
    jobId: j.jobId,
    kind: s(j.kind),
    store: s(j.store),
    state: s(j.state),
    phase: s(j.phase),
    completed: n(j.completed),
    total: n(j.total),
    unit: j.unit ?? null,
    startedUtc: j.startedUtc,
    endedUtc: j.endedUtc ?? null,
    route: j.route ?? null,
    note: j.note ?? null,
    error: j.error ? { category: s(j.error.category), message: s(j.error.message) } : null,
    raw: j,
  };
}

export function isTerminal(state: string): boolean {
  return state === 'done' || state === 'error' || state === 'cancelled';
}

/**
 * A job's progress, honestly: a percentage only when the operation knows its total (07 §5 — a restore knows its pages, a verify its packs); otherwise
 * the phase alone, with no bar to fill.
 */
export function jobProgress(job: Pick<Job, 'phase' | 'completed' | 'total' | 'unit' | 'state'>): {
  determinate: boolean;
  fraction: number;
  text: string;
} {
  const phase = job.phase || (job.state === 'queued' ? 'queued' : 'working');
  if (job.total > 0) {
    const fraction = Math.min(1, Math.max(0, job.completed / job.total));
    const unit = job.unit ? ` ${job.unit}` : '';
    return {
      determinate: true,
      fraction,
      text: `${phase}: ${job.completed.toLocaleString()} / ${job.total.toLocaleString()}${unit} (${Math.floor(fraction * 100)} %)`,
    };
  }
  return { determinate: false, fraction: 0, text: `${phase}…` };
}

// ── Plans ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export interface RestorePlan {
  canRestore: boolean;
  fingerprint: string | null;
  resolvedTarget: string | null;
  displacedBundle: string | null;
  chain: number[];
  bytesToRead: number;
  pointLsn: number;
  targetLsn: number;
  timelineId: string;
  archivedSegments: number;
  archivedBytes: number;
  sampleUtc: string | null;
  refusal: string | null;
  unreachable: string | null;
  refusalMessage: string | null;
  nearestRestorable: number[];
  heldBySessionId: string | null;
  /** Null when the store could not be read for it: what is lost is then unknown, never "nothing". */
  newestKnownLsn: number | null;
  lostLsns: number | null;
}

export function normalizeRestorePlan(p: BackupRestorePlanDto): RestorePlan {
  return {
    canRestore: !!p.canRestore,
    fingerprint: p.fingerprint ?? null,
    resolvedTarget: p.resolvedTarget ?? null,
    displacedBundle: p.displacedBundle ?? null,
    chain: (p.chain ?? []).map(n),
    bytesToRead: n(p.bytesToRead),
    pointLsn: n(p.pointLsn),
    targetLsn: n(p.targetLsn),
    timelineId: s(p.timelineId),
    archivedSegments: n(p.archivedSegments),
    archivedBytes: n(p.archivedBytes),
    sampleUtc: p.sampleUtc ?? null,
    refusal: p.refusal ?? null,
    unreachable: p.unreachable ?? null,
    refusalMessage: p.refusalMessage ?? null,
    nearestRestorable: (p.nearestRestorable ?? []).map(n),
    heldBySessionId: p.heldBySessionId ?? null,
    newestKnownLsn: p.newestKnownLsn == null ? null : n(p.newestKnownLsn),
    lostLsns: p.lostLsns == null ? null : n(p.lostLsns),
  };
}

/**
 * "What is lost after" the restored LSN, in words (07 §5): the commits the store knows past it, that nothing it knows is lost, or — when the store
 * could not be read for it — that this is unknown.
 */
export function lostAfterText(
  plan: Pick<RestorePlan, 'canRestore' | 'pointLsn' | 'newestKnownLsn' | 'lostLsns'>,
): string {
  if (!plan.canRestore) {
    return '';
  }
  if (plan.lostLsns == null || plan.newestKnownLsn == null) {
    return (
      `The restored database ends at LSN ${plan.pointLsn.toLocaleString()}. What is lost after it is unknown: the store's points and archive ` +
      `could not be read for it.`
    );
  }
  if (plan.lostLsns <= 0) {
    return `Nothing the store knows of lies after LSN ${plan.pointLsn.toLocaleString()}: no committed change it holds is lost.`;
  }
  return (
    `The restored database ends at LSN ${plan.pointLsn.toLocaleString()}. The store knows changes through LSN ` +
    `${plan.newestKnownLsn.toLocaleString()}: ${plan.lostLsns.toLocaleString()} LSNs of committed changes after the restored point are not in it.`
  );
}

export interface RetentionPlan {
  kept: number[];
  removed: number[];
  compactions: number[];
  packDeletions: string[];
  cutoffId: number;
  bytesBefore: number;
  bytesAfterAtMost: number;
  blocked: string;
  blockedReason: string | null;
  fingerprint: string;
}

export function normalizeRetentionPlan(p: BackupRetentionPlanDto): RetentionPlan {
  return {
    kept: (p.kept ?? []).map(n),
    removed: (p.removed ?? []).map(n),
    compactions: (p.compactions ?? []).map(n),
    packDeletions: (p.packDeletions ?? []).map(s),
    cutoffId: n(p.cutoffId),
    bytesBefore: n(p.bytesBefore),
    bytesAfterAtMost: n(p.bytesAfterAtMost),
    blocked: s(p.blocked) || 'None',
    blockedReason: p.blockedReason ?? null,
    fingerprint: s(p.fingerprint),
  };
}

// ── Reports ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export interface VerifyFinding {
  code: string;
  severity: string;
  file: string;
  pageId: number;
  message: string;
}

/** Every finding of a verify, damage first (an operator reads top-down), store-level ones after the packs'. */
export function verifyFindings(r: BackupVerifyReportDto): VerifyFinding[] {
  const all: VerifyFinding[] = [];
  for (const p of r.packs ?? []) {
    for (const f of p.findings ?? []) {
      all.push({
        code: s(f.code),
        severity: s(f.severity),
        file: s(f.file),
        pageId: n(f.pageId),
        message: s(f.message),
      });
    }
  }
  for (const f of r.storeFindings ?? []) {
    all.push({ code: s(f.code), severity: s(f.severity), file: s(f.file), pageId: n(f.pageId), message: s(f.message) });
  }
  return all.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === 'Damage' ? -1 : 1));
}

export function verdictTone(verdict: string): StatusTone {
  switch (verdict) {
    case 'Ok':
      return 'success';
    case 'Warning':
      return 'warn';
    case 'Damaged':
    case 'NoRestorablePoint':
      return 'error';
    default:
      return 'neutral';
  }
}

/** A drill's tone: an Error drill is the drill's own limit (it says nothing about the point), not a red mark. */
export function drillTone(d: Pick<BackupDrillReportDto, 'outcome'>): StatusTone {
  switch (d.outcome) {
    case 'Passed':
      return 'success';
    case 'PassedWithWarnings':
      return 'warn';
    case 'Failed':
      return 'error';
    default:
      return 'neutral';
  }
}

// ── Formatting ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KiB', 'MiB', 'GiB', 'TiB'];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v >= 100 ? 0 : 1)} ${units[i]}`;
}

/** "3 min ago", "2 h ago", "5 d ago"; empty for no instant. */
export function formatAge(utc: string | null, now: number = Date.now()): string {
  if (!utc) {
    return '';
  }
  const seconds = Math.max(0, Math.round((now - Date.parse(utc)) / 1000));
  if (seconds < 60) {
    return `${seconds} s ago`;
  }
  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)} min ago`;
  }
  if (seconds < 86400) {
    return `${Math.floor(seconds / 3600)} h ago`;
  }
  return `${Math.floor(seconds / 86400)} d ago`;
}

/** A local `datetime-local` input value (no offset) → an ISO instant with its offset, in UTC. Empty for an unparsable value. */
export function localInputToUtc(value: string): string {
  if (!value) {
    return '';
  }
  const t = new Date(value).getTime();
  return Number.isNaN(t) ? '' : new Date(t).toISOString();
}
