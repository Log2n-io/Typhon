import { describe, expect, it } from 'vitest';
import type { IntegrityFindingDto, IntegrityReportDto, RepairPlanDto } from '@/api/generated/model';
import {
  countBySeverity,
  normalizePlan,
  normalizeReport,
  severityByPage,
  sortFindings,
  verdictIsDamage,
  type Finding,
  type Severity,
} from '../integrityModel';

function finding(severity: Severity, page: number, code = 'CHK-X-01'): Finding {
  return {
    code,
    severity,
    confidence: 'Confirmed',
    summary: '',
    detail: '',
    ruleId: '',
    repair: '',
    occurrences: 1,
    locus: { filePageIndex: page, segmentRootPage: -1, kind: '', archetype: '', component: '', text: '' },
    loss: { kind: 'None', count: '', archetype: '', component: '', explanation: '' },
  };
}

describe('sortFindings', () => {
  it('ranks by severity before page, so the worst thing is always the first row', () => {
    const sorted = sortFindings([finding('Leak', 1), finding('Fatal', 900), finding('Divergence', 2)]);
    expect(sorted.map((f) => f.severity)).toEqual(['Fatal', 'Divergence', 'Leak']);
  });

  it('groups equal severities by page, so contiguous damage reads as one region', () => {
    const sorted = sortFindings([finding('Leak', 40), finding('Leak', 8), finding('Leak', 12)]);
    expect(sorted.map((f) => f.locus.filePageIndex)).toEqual([8, 12, 40]);
  });
});

describe('severityByPage', () => {
  it('keeps the worst severity when several findings hit one page', () => {
    const map = severityByPage([finding('Leak', 7), finding('DataLoss', 7), finding('Divergence', 7)]);
    expect(map.get(7)).toBe('DataLoss');
  });

  it('drops findings with no page, because the map has no cell to paint for them', () => {
    // A database-wide finding carries filePageIndex -1. Painting it would have to pick an arbitrary cell,
    // which is worse than not painting it: it would accuse a page that is fine.
    const map = severityByPage([finding('Fatal', -1), finding('Leak', 3)]);
    expect(map.has(-1)).toBe(false);
    expect([...map.keys()]).toEqual([3]);
  });
});

describe('countBySeverity', () => {
  it('counts every severity, including the ones with no findings', () => {
    const counts = countBySeverity([finding('Leak', 1), finding('Leak', 2), finding('Fatal', 3)]);
    expect(counts.Leak).toBe(2);
    expect(counts.Fatal).toBe(1);
    expect(counts.Advisory).toBe(0);
  });
});

describe('verdictIsDamage', () => {
  it('does not treat leaks as damage', () => {
    // Leaks waste space; they are not a correctness problem. Calling them damage would push someone into a
    // repair they do not need — the exact over-reaction the separate verdict exists to prevent.
    expect(verdictIsDamage('SoundWithLeaks')).toBe(false);
    expect(verdictIsDamage('Sound')).toBe(false);
    expect(verdictIsDamage('Divergent')).toBe(true);
    expect(verdictIsDamage('DataLoss')).toBe(true);
    expect(verdictIsDamage('Unopenable')).toBe(true);
  });
});

describe('normalizeReport', () => {
  const bare: IntegrityReportDto = {
    verdict: 'Sound',
    exitCode: '0',
    source: 'C:\\db.typhon',
    mode: 'Offline',
    depth: 'Standard',
    durationMs: '12.5',
    identity: {
      name: 'db',
      formatRevision: 6,
      pageCount: 100,
      sizeBytes: '819200',
      checkpointLsn: '42',
      cleanShutdown: true,
      walSegmentCount: 0,
      walBytes: '0',
    },
    totals: {
      pagesScanned: 100,
      pagesAllocated: 90,
      checksumFailures: 0,
      pagesWithSectorFooters: 100,
      sectorFailures: 0,
      segmentsWalked: 4,
      bytesLeaked: '0',
    },
    findings: [],
    limits: { structural: 'internal consistency only', checksSkipped: [], caveats: [] },
  };

  it('coerces the string-typed numerics ASP.NET emits for int64/double', () => {
    const r = normalizeReport(bare);
    expect(r.identity.sizeBytes).toBe(819200);
    expect(r.identity.checkpointLsn).toBe(42);
    expect(r.durationMs).toBe(12.5);
    expect(r.exitCode).toBe(0);
  });

  it('sorts findings on the way in, so no view can render them out of order', () => {
    const raw: IntegrityFindingDto[] = [
      { ...rawFinding('Leak', 1) },
      { ...rawFinding('DataLoss', 50) },
    ];
    const r = normalizeReport({ ...bare, verdict: 'DataLoss', findings: raw });
    expect(r.findings.map((f) => f.severity)).toEqual(['DataLoss', 'Leak']);
  });

  it('degrades an unknown severity to Advisory rather than throwing', () => {
    // A checker that grows a sixth severity should under-rank one row on an old client, not blank the report.
    const r = normalizeReport({ ...bare, findings: [rawFinding('Apocalyptic' as string, 5)] });
    expect(r.findings[0].severity).toBe('Advisory');
  });

  it('degrades an unknown verdict to Divergent, never to Sound', () => {
    // Failing toward "look at this" is the only safe direction: an unrecognised verdict rendered as Sound
    // would be the client inventing a clean bill of health the server never gave.
    const r = normalizeReport({ ...bare, verdict: 'Whatever' });
    expect(r.verdict).toBe('Divergent');
  });

  it('survives null collections, which ASP.NET emits for empty ones', () => {
    const r = normalizeReport({ ...bare, findings: null, limits: { structural: null, checksSkipped: null, caveats: null } });
    expect(r.findings).toEqual([]);
    expect(r.limits.checksSkipped).toEqual([]);
    expect(r.limits.structural).toBe('');
  });

  function rawFinding(severity: string, page: number): IntegrityFindingDto {
    return {
      code: 'CHK-X-01',
      severity,
      confidence: 'Confirmed',
      summary: 's',
      detail: 'd',
      ruleId: 'RB-01',
      repair: 'Rebuild',
      occurrences: 1,
      locus: { filePageIndex: page, segmentRootPage: -1, kind: 'k', archetype: '', component: '', text: `page ${page}` },
      loss: { kind: 'None', count: '', archetype: '', component: '', explanation: '' },
    };
  }
});

describe('normalizePlan', () => {
  const base: RepairPlanDto = {
    source: 'game.typhon',
    fingerprint: 'abc',
    verdict: 'Sound',
    requiresLossyConsent: false,
    steps: [],
    loss: [],
    unaddressed: [],
    blockedReason: null,
  };

  it('keeps "blocked" distinguishable from "nothing to repair"', () => {
    // Both plans have zero steps, and the panel says opposite things about them: one is a healthy database,
    // the other is one this build refuses to touch. A truthiness check over `steps.length` collapses them.
    expect(normalizePlan(base).blockedReason).toBeNull();
    expect(
      normalizePlan({ ...base, blockedReason: 'format revision 6, this build speaks 7' }).blockedReason,
    ).toContain('revision 6');
  });

  it('normalizes a missing reason to null rather than an empty string', () => {
    // ASP.NET omits nulls; an absent field must not become '' — that is falsy but not null, and every
    // downstream check would then be a truthiness test that happened to work.
    // `as unknown` first: the point of the test is to hand normalizePlan a shape the DTO says is impossible, so a
    // direct cast is rightly rejected by the compiler rather than being a mistake to work around.
    const raw = { ...base } as Record<string, unknown>;
    delete raw.blockedReason;
    expect(normalizePlan(raw as unknown as RepairPlanDto).blockedReason).toBeNull();
  });
});
