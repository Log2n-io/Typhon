import { describe, expect, it } from 'vitest';
import type { BackupListingDto, BackupPointDto, BackupStatusDto } from '@/api/generated/model';
import {
  buildRibbons,
  cardHealth,
  coverageOf,
  healthLabel,
  healthTone,
  jobProgress,
  lostAfterText,
  normalizeListing,
  normalizeStatus,
  verifyFindings,
  type ArchiveTimeline,
} from '../backupsModel';

const point = (id: number, kind: string, chain = 'c1', extra: Partial<BackupPointDto> = {}): BackupPointDto => ({
  backupId: id,
  kind,
  chainId: chain,
  parentId: kind === 'Incremental' ? id - 1 : 0,
  baseId: 1,
  checkpointLsn: id * 100,
  pointLsn: id * 100 + 50,
  imagePages: 64,
  pageCount: 10,
  tailSegments: 1,
  suspect: false,
  offline: false,
  freezeUtc: '2026-10-21T10:00:00Z',
  tailEndUtc: '2026-10-21T10:00:01Z',
  timelineId: 't1',
  formatRevision: 3,
  packFile: `${String(id).padStart(8, '0')}-${kind[0]}.tpack`,
  packBytes: 4096,
  ...extra,
});

const listing = (points: BackupPointDto[], extra: Partial<BackupListingDto> = {}): BackupListingDto => ({
  storeId: 's',
  databaseId: 'd',
  points,
  chains: [],
  unreadablePacks: [],
  temporaryFiles: [],
  restorablePoints: null,
  drills: [],
  ...extra,
});

describe('chain ribbons', () => {
  it('shows an id once, as its compacted pack when it has one (05 §2)', () => {
    const ribbons = buildRibbons(
      normalizeListing(listing([point(1, 'Full'), point(2, 'Incremental'), point(2, 'Compacted')])),
    );
    expect(ribbons).toHaveLength(1);
    expect(ribbons[0].points.map((p) => [p.point.backupId, p.point.kind, p.compacted])).toEqual([
      [1, 'Full', false],
      [2, 'Compacted', true],
    ]);
  });

  it('orders points base → head and chains by their first point', () => {
    const ribbons = buildRibbons(
      normalizeListing(
        listing([
          point(5, 'Full', 'c2'),
          point(3, 'Incremental'),
          point(1, 'Full'),
          point(6, 'Incremental', 'c2'),
          point(2, 'Incremental'),
        ]),
      ),
    );
    expect(ribbons.map((r) => r.chainId)).toEqual(['c1', 'c2']);
    expect(ribbons[0].points.map((p) => p.point.backupId)).toEqual([1, 2, 3]);
    expect(ribbons[1].points.map((p) => p.point.backupId)).toEqual([5, 6]);
  });

  it('marks a point the restorable set leaves out, and says nothing when the set is unknown', () => {
    const known = buildRibbons(
      normalizeListing(listing([point(1, 'Full'), point(2, 'Incremental')], { restorablePoints: [1] })),
    );
    expect(known[0].points.map((p) => p.restorable)).toEqual([true, false]);
    const unknown = buildRibbons(normalizeListing(listing([point(1, 'Full')])));
    expect(unknown[0].points[0].restorable).toBeNull();
  });

  it('carries each point’s drill ✓/✗ (a pass with warnings is a pass)', () => {
    const ribbons = buildRibbons(
      normalizeListing(
        listing([point(1, 'Full'), point(2, 'Incremental'), point(3, 'Incremental')], {
          drills: [
            { backupId: 1, utc: null, outcome: 'PassedWithWarnings', error: null },
            { backupId: 2, utc: null, outcome: 'Failed', error: 'checks DataLoss' },
          ],
        }),
      ),
    );
    expect(ribbons[0].points.map((p) => p.drill)).toEqual([true, false, null]);
  });
});

describe('archive coverage', () => {
  const timeline: ArchiveTimeline = {
    timelineId: 't1',
    parentTimelineId: null,
    originLsn: 0,
    reason: 'Genesis',
    archivedThroughLsn: 400,
    segments: [
      { segmentId: 1, firstLsn: 100, lastLsn: 199, sealedUtc: null, bytes: 1, flags: 'None' },
      { segmentId: 2, firstLsn: 200, lastLsn: 249, sealedUtc: null, bytes: 1, flags: 'None' },
      { segmentId: 4, firstLsn: 301, lastLsn: 400, sealedUtc: null, bytes: 1, flags: 'GapBefore' },
    ],
    gaps: [{ fromLsn: 250, toLsn: 300, recordedUtc: null, reason: 'BacklogReleased', covered: false }],
    samples: [],
  };

  it('merges contiguous segments, draws the gap, and ticks each point at its P', () => {
    const c = coverageOf(
      timeline,
      normalizeListing(
        listing([point(1, 'Full', 'c1', { pointLsn: 150 }), point(9, 'Full', 'c9', { timelineId: 'other' })]),
      ).points,
    );
    expect([c.minLsn, c.maxLsn]).toEqual([100, 400]);
    expect(c.runs.map((r) => [r.kind, r.fromLsn, r.toLsn])).toEqual([
      ['archived', 100, 249],
      ['archived', 301, 400],
      ['gap', 250, 300],
    ]);
    expect(c.runs[2].covered).toBe(false);
    expect(c.ticks).toEqual([{ backupId: 1, at: (150 - 100) / 300, lsn: 150 }]);
  });

  it('draws nothing for an empty timeline', () => {
    const c = coverageOf({ ...timeline, segments: [], gaps: [], archivedThroughLsn: 0 }, []);
    expect(c.runs).toEqual([]);
  });
});

describe('health', () => {
  it('maps the engine’s names to tones, and an unknown name renders as itself', () => {
    expect(healthTone('Ok')).toBe('success');
    expect(healthTone('Degraded')).toBe('warn');
    expect(healthTone('Failing')).toBe('error');
    expect(healthTone('Off')).toBe('neutral');
    expect(healthTone('Elevated')).toBe('neutral');
    expect(healthLabel('Elevated')).toBe('Elevated');
    expect(healthLabel('')).toBe('Unknown');
  });

  it('prefers the engine’s verdict, says when its engine is not running, and falls back to the store’s', () => {
    const base = {
      store: 'S',
      bundle: 'B',
      storeStatus: {
        health: 'Ok',
        reason: 'the newest point is restorable',
        points: 2,
        latest: null,
        latestRestorable: null,
      },
      storeError: null,
      engine: null,
      route: { kind: 'offline', reason: '', sessionId: null },
      runningJobId: null,
      defaultScratch: null,
    };
    // The server omits null members (WhenWritingNull) and Orval types nested DTOs as present: the wire shape is cast, the normalizer copes.
    const wire = (x: object) => x as unknown as BackupStatusDto;
    expect(cardHealth(normalizeStatus(wire(base)))).toEqual({
      health: 'Ok',
      reason: 'the newest point is restorable',
      source: 'store',
    });

    const engine = {
      source: 'statusFile',
      engineLive: false,
      pid: 4,
      machine: 'm',
      storePath: 'S',
      acceptsRequests: false,
      health: 'Degraded',
      healthReason: 'Stale',
      reasonText: 'the newest point is 3 h old',
      state: 'Stopped',
      phase: 'None',
      progressPages: 0,
      progressOfPages: 0,
      progressBytes: 0,
      lastPoint: null,
      lastSuccessUtc: null,
      nextDueUtc: null,
      consecutiveFailures: 0,
      lastFailure: null,
      chainDepth: 1,
      storeBytes: 0,
      restorablePoints: 1,
      lastDrill: null,
      lastPassedDrill: null,
      retentionBlocked: 'None',
      lastRequestId: null,
      updatedUtc: null,
      archive: {
        state: 'Off',
        timelineId: null,
        archivedThroughLsn: 0,
        lagBytes: 0,
        backlogBytes: 0,
        lagSeconds: 0,
        gapsTotal: 0,
        lastGapFromLsn: 0,
        lastGapToLsn: 0,
        lastArchivedSealUtc: null,
        consecutiveFailures: 0,
        lastError: null,
      },
    };
    const stopped = cardHealth(normalizeStatus(wire({ ...base, engine })));
    expect(stopped).toEqual({
      health: 'Degraded',
      reason: 'engine not running; last known state: the newest point is 3 h old',
      source: 'engine',
    });
  });
});

describe('job progress', () => {
  it('is a percentage only when the operation knows its total', () => {
    expect(jobProgress({ phase: 'restore', completed: 250, total: 1000, unit: 'pages', state: 'running' })).toEqual({
      determinate: true,
      fraction: 0.25,
      text: `restore: ${(250).toLocaleString()} / ${(1000).toLocaleString()} pages (25 %)`,
    });
    expect(jobProgress({ phase: 'capture', completed: 0, total: 0, unit: null, state: 'running' })).toEqual({
      determinate: false,
      fraction: 0,
      text: 'capture…',
    });
  });
});

describe('what is lost after the restored LSN', () => {
  it('names the LSNs past the point, or says nothing is lost', () => {
    expect(lostAfterText({ canRestore: true, pointLsn: 150, newestKnownLsn: 450, lostLsns: 300 })).toContain(
      '300 LSNs',
    );
    expect(lostAfterText({ canRestore: true, pointLsn: 450, newestKnownLsn: 450, lostLsns: 0 })).toContain(
      'no committed change it holds is lost',
    );
    expect(lostAfterText({ canRestore: false, pointLsn: 0, newestKnownLsn: 0, lostLsns: 0 })).toBe('');
  });

  it('a store that could not be read for it says what is lost is unknown, never that nothing is', () => {
    const text = lostAfterText({ canRestore: true, pointLsn: 150, newestKnownLsn: null, lostLsns: null });
    expect(text).toContain('unknown');
    expect(text).not.toContain('no committed change');
  });
});

describe('verify findings', () => {
  it('lists damage before advisories, packs’ and store’s together', () => {
    const findings = verifyFindings({
      level: 'Full',
      verdict: 'Damaged',
      packs: [
        {
          file: 'p1',
          backupId: 1,
          status: 'Damaged',
          findings: [{ code: 'BKP-VER-08', severity: 'Advisory', file: 't', pageId: -1, message: 'temp' }],
        },
      ],
      points: [],
      storeFindings: [{ code: 'BKP-VER-10', severity: 'Damage', file: 'p1', pageId: -1, message: 'FileCRC mismatch' }],
      durationMs: 1,
      bytesRead: 0,
      removedPoints: 0,
    });
    expect(findings.map((f) => f.code)).toEqual(['BKP-VER-10', 'BKP-VER-08']);
  });
});
