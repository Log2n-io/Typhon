import type {
  IntegrityFindingDto,
  IntegrityLimitsDto,
  IntegrityLocusDto,
  IntegrityLossDto,
  IntegrityReportDto,
  RepairOutcomeDto,
  RepairPlanDto,
  RepairStepDto,
  RepairStepResultDto,
} from '@/api/generated/model';
import type { StatusTone } from '@/components/ui/status-badge';

/**
 * Normalized mirrors of the integrity DTOs, plus the ordering and tone rules the views share.
 *
 * Orval emits `string | null` for every C# string and `number | string` for every int64/double, because
 * ASP.NET Core doesn't propagate non-nullable reference types into OpenAPI. Panels can't `switch` on
 * `string | null`, so severity and verdict are narrowed to real unions here — at the one boundary where an
 * unrecognized value can be handled deliberately rather than crashing a render three components deep.
 */

/** How bad a finding is. Ordering matters — see {@link SEVERITY_RANK}. */
export type Severity = 'Fatal' | 'DataLoss' | 'Divergence' | 'Leak' | 'Advisory';

/** The one-word answer to "is this database sound?". */
export type Verdict = 'Sound' | 'SoundWithLeaks' | 'Divergent' | 'DataLoss' | 'Unopenable';

/** How much work a scan does. `spine` is the open-time tier — O(segments), not O(pages). */
export type ScanDepth = 'spine' | 'quick' | 'standard' | 'deep';

/** Whether the source was quiescent when the finding was observed. */
export type Confidence = 'Confirmed' | 'Suspected' | 'Sampled';

/** Whether a repair step regenerates (lossless) or excises (lossy). */
export type RepairClass = 'Regenerate' | 'Excise' | 'Advisory';

export const SCAN_DEPTHS: readonly ScanDepth[] = ['spine', 'quick', 'standard', 'deep'];

/** Human blurb for each depth, shown in the picker so the cost/coverage trade is explicit. */
export const DEPTH_BLURB: Readonly<Record<ScanDepth, string>> = {
  spine: 'Bootstrap + segment roots only. Bounded by segment count, not database size.',
  quick: 'Adds the occupancy bitmap and per-segment structure.',
  standard: 'Full page sweep with checksum and sector verification.',
  deep: 'Everything, including WAL inspection. The depth repair planning uses.',
};

/**
 * Severity ordering, worst first. Findings sort by this and never alphabetically — an operator scanning a
 * report reads top-down and must hit the thing that loses data before the thing that wastes space.
 */
export const SEVERITY_RANK: Readonly<Record<Severity, number>> = {
  Fatal: 0,
  DataLoss: 1,
  Divergence: 2,
  Leak: 3,
  Advisory: 4,
};

const SEVERITY_TONE: Readonly<Record<Severity, StatusTone>> = {
  Fatal: 'error',
  DataLoss: 'error',
  Divergence: 'warn',
  Leak: 'info',
  Advisory: 'neutral',
};

const VERDICT_TONE: Readonly<Record<Verdict, StatusTone>> = {
  Sound: 'success',
  SoundWithLeaks: 'info',
  Divergent: 'warn',
  DataLoss: 'error',
  Unopenable: 'error',
};

/**
 * One-line gloss per verdict. `SoundWithLeaks` is deliberately reassuring: leaks are not correctness
 * problems and must not scare anyone into running a repair they don't need.
 */
const VERDICT_BLURB: Readonly<Record<Verdict, string>> = {
  Sound: 'No damage found.',
  SoundWithLeaks: 'No damage. Some space is allocated but unreachable — reclaimable, nothing is wrong.',
  Divergent: 'Derived structures disagree with the data they are derived from. Rebuildable without loss.',
  DataLoss: 'Damage that cannot be repaired without losing something. Read the loss manifest before repairing.',
  Unopenable: 'The database cannot be opened. Repair may recover part of it.',
};

/**
 * Canvas colours for the File Map integrity lens, keyed to the same semantics as the badge tones.
 *
 * Literal `rgb()` rather than CSS custom properties because the map renders to a canvas: it cannot inherit a
 * token, and reading computed styles per frame is exactly the cost the renderer is built to avoid. These
 * track the Tailwind palette the tones resolve to (red-400 / amber-400 / sky-400 / slate-400).
 */
export const SEVERITY_CANVAS_COLOR: Readonly<Record<Severity, string>> = {
  Fatal: 'rgb(248, 113, 113)',
  DataLoss: 'rgb(248, 113, 113)',
  Divergence: 'rgb(251, 191, 36)',
  Leak: 'rgb(56, 189, 248)',
  Advisory: 'rgb(148, 163, 184)',
};

export function severityTone(severity: Severity): StatusTone {
  return SEVERITY_TONE[severity] ?? 'neutral';
}

export function verdictTone(verdict: Verdict): StatusTone {
  return VERDICT_TONE[verdict] ?? 'neutral';
}

export function verdictBlurb(verdict: Verdict): string {
  return VERDICT_BLURB[verdict] ?? '';
}

/** True when the verdict means something is actually wrong (as opposed to merely wasteful). */
export function verdictIsDamage(verdict: Verdict): boolean {
  return verdict === 'Divergent' || verdict === 'DataLoss' || verdict === 'Unopenable';
}

export interface Locus {
  filePageIndex: number;
  segmentRootPage: number;
  kind: string;
  archetype: string;
  component: string;
  /** Server-rendered human form — always prefer this over recomposing the parts. */
  text: string;
}

export interface LossEstimate {
  kind: string;
  /** Rendered count: an exact number or an honest range. Never parse it back into a number. */
  count: string;
  archetype: string;
  component: string;
  explanation: string;
}

export interface Finding {
  code: string;
  severity: Severity;
  confidence: Confidence;
  summary: string;
  detail: string;
  ruleId: string;
  repair: string;
  occurrences: number;
  locus: Locus;
  loss: LossEstimate;
}

export interface Identity {
  name: string;
  formatRevision: number;
  pageCount: number;
  sizeBytes: number;
  checkpointLsn: number;
  cleanShutdown: boolean;
  walSegmentCount: number;
  walBytes: number;
}

export interface Totals {
  pagesScanned: number;
  pagesAllocated: number;
  checksumFailures: number;
  pagesWithSectorFooters: number;
  sectorFailures: number;
  segmentsWalked: number;
  bytesLeaked: number;
}

export interface Limits {
  structural: string;
  checksSkipped: string[];
  caveats: string[];
}

export interface Report {
  verdict: Verdict;
  exitCode: number;
  source: string;
  mode: string;
  depth: string;
  durationMs: number;
  identity: Identity;
  totals: Totals;
  findings: Finding[];
  limits: Limits;
}

export interface RepairStep {
  order: number;
  action: string;
  class: RepairClass;
  description: string;
  rationale: string;
  addresses: string[];
}

export interface RepairPlan {
  source: string;
  /** Binds the plan to the exact database state it was built for. Apply refuses on drift. */
  fingerprint: string;
  verdict: Verdict;
  requiresLossyConsent: boolean;
  steps: RepairStep[];
  /** The full enumeration. This *is* the consent — never render a count in its place. */
  loss: LossEstimate[];
  unaddressed: string[];
}

export interface RepairStepResult {
  order: number;
  action: string;
  outcome: string;
  detail: string;
}

export interface RepairOutcome {
  succeeded: boolean;
  backupPath: string;
  results: RepairStepResult[];
  verification: Report | null;
}

const num = (v: number | string | null | undefined, fallback = 0): number =>
  v == null ? fallback : typeof v === 'number' ? v : Number(v);

const str = (v: string | null | undefined, fallback = ''): string => v ?? fallback;

const list = <T,>(v: T[] | null | undefined): T[] => v ?? [];

/**
 * Narrows a server severity string. An unknown value degrades to `Advisory` rather than throwing:
 * a checker that grows a sixth severity should make an old client under-rank one row, not blank the
 * whole report.
 */
function toSeverity(v: string | null | undefined): Severity {
  return v != null && v in SEVERITY_RANK ? (v as Severity) : 'Advisory';
}

/** Unknown verdicts degrade to `Divergent` — the "something is off, look at it" bucket, never to `Sound`. */
function toVerdict(v: string | null | undefined): Verdict {
  return v != null && v in VERDICT_TONE ? (v as Verdict) : 'Divergent';
}

function normalizeLocus(raw: IntegrityLocusDto | null | undefined): Locus {
  return {
    filePageIndex: num(raw?.filePageIndex, -1),
    segmentRootPage: num(raw?.segmentRootPage, -1),
    kind: str(raw?.kind),
    archetype: str(raw?.archetype),
    component: str(raw?.component),
    text: str(raw?.text),
  };
}

function normalizeLoss(raw: IntegrityLossDto | null | undefined): LossEstimate {
  return {
    kind: str(raw?.kind, 'None'),
    count: str(raw?.count),
    archetype: str(raw?.archetype),
    component: str(raw?.component),
    explanation: str(raw?.explanation),
  };
}

export function normalizeFinding(raw: IntegrityFindingDto): Finding {
  return {
    code: str(raw.code),
    severity: toSeverity(raw.severity),
    confidence: (str(raw.confidence, 'Suspected') as Confidence),
    summary: str(raw.summary),
    detail: str(raw.detail),
    ruleId: str(raw.ruleId),
    repair: str(raw.repair),
    occurrences: num(raw.occurrences),
    locus: normalizeLocus(raw.locus),
    loss: normalizeLoss(raw.loss),
  };
}

function normalizeLimits(raw: IntegrityLimitsDto | null | undefined): Limits {
  return {
    structural: str(raw?.structural),
    checksSkipped: list(raw?.checksSkipped),
    caveats: list(raw?.caveats),
  };
}

export function normalizeReport(raw: IntegrityReportDto): Report {
  return {
    verdict: toVerdict(raw.verdict),
    exitCode: num(raw.exitCode),
    source: str(raw.source),
    mode: str(raw.mode),
    depth: str(raw.depth),
    durationMs: num(raw.durationMs),
    identity: {
      name: str(raw.identity?.name),
      formatRevision: num(raw.identity?.formatRevision),
      pageCount: num(raw.identity?.pageCount),
      sizeBytes: num(raw.identity?.sizeBytes),
      checkpointLsn: num(raw.identity?.checkpointLsn),
      cleanShutdown: raw.identity?.cleanShutdown ?? false,
      walSegmentCount: num(raw.identity?.walSegmentCount),
      walBytes: num(raw.identity?.walBytes),
    },
    totals: {
      pagesScanned: num(raw.totals?.pagesScanned),
      pagesAllocated: num(raw.totals?.pagesAllocated),
      checksumFailures: num(raw.totals?.checksumFailures),
      pagesWithSectorFooters: num(raw.totals?.pagesWithSectorFooters),
      sectorFailures: num(raw.totals?.sectorFailures),
      segmentsWalked: num(raw.totals?.segmentsWalked),
      bytesLeaked: num(raw.totals?.bytesLeaked),
    },
    findings: sortFindings(list(raw.findings).map(normalizeFinding)),
    limits: normalizeLimits(raw.limits),
  };
}

function normalizeStep(raw: RepairStepDto): RepairStep {
  return {
    order: num(raw.order),
    action: str(raw.action),
    class: (str(raw.class, 'Advisory') as RepairClass),
    description: str(raw.description),
    rationale: str(raw.rationale),
    addresses: list(raw.addresses),
  };
}

export function normalizePlan(raw: RepairPlanDto): RepairPlan {
  return {
    source: str(raw.source),
    fingerprint: str(raw.fingerprint),
    verdict: toVerdict(raw.verdict),
    requiresLossyConsent: raw.requiresLossyConsent ?? false,
    // Step order is a correctness constraint (RB-02), not a display preference — sort by it explicitly
    // rather than trusting array order to survive serialization.
    steps: list(raw.steps).map(normalizeStep).sort((a, b) => a.order - b.order),
    loss: list(raw.loss).map(normalizeLoss),
    unaddressed: list(raw.unaddressed),
  };
}

export function normalizeOutcome(raw: RepairOutcomeDto): RepairOutcome {
  return {
    succeeded: raw.succeeded ?? false,
    backupPath: str(raw.backupPath),
    results: list(raw.results as RepairStepResultDto[] | null).map((r) => ({
      order: num(r.order),
      action: str(r.action),
      outcome: str(r.outcome),
      detail: str(r.detail),
    })),
    verification: raw.verification ? normalizeReport(raw.verification) : null,
  };
}

/**
 * Worst first, then by page so a run of damage in one region reads as one region. Stable within a
 * (severity, page) pair by falling back to the finding code.
 */
export function sortFindings(findings: Finding[]): Finding[] {
  return [...findings].sort((a, b) => {
    const bySeverity = SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity];
    if (bySeverity !== 0) {
      return bySeverity;
    }
    const byPage = a.locus.filePageIndex - b.locus.filePageIndex;
    return byPage !== 0 ? byPage : a.code.localeCompare(b.code);
  });
}

/** Counts per severity, for the banner's chip row. Zero-count severities are omitted by the caller. */
export function countBySeverity(findings: Finding[]): Record<Severity, number> {
  const counts: Record<Severity, number> = { Fatal: 0, DataLoss: 0, Divergence: 0, Leak: 0, Advisory: 0 };
  for (const f of findings) {
    counts[f.severity]++;
  }
  return counts;
}

/**
 * Worst severity per physical page, for the File Map integrity lens. Findings with no page locus
 * (`filePageIndex < 0` — database-wide findings) are excluded: the map has no cell to paint for them.
 */
export function severityByPage(findings: Finding[]): Map<number, Severity> {
  const byPage = new Map<number, Severity>();
  for (const f of findings) {
    const page = f.locus.filePageIndex;
    if (page < 0) {
      continue;
    }
    const current = byPage.get(page);
    if (current === undefined || SEVERITY_RANK[f.severity] < SEVERITY_RANK[current]) {
      byPage.set(page, f.severity);
    }
  }
  return byPage;
}
