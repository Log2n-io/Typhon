// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { QueryDefinitionDto } from '@/api/generated/model';

/**
 * #618 §4.3 — the capture knows a query's cost and nothing about why; the database knows the indexes and the entity
 * count and nothing about cost. These cover the join between them, and the cases where it must stay silent.
 */
const hoisted = vi.hoisted(() => ({
  reality: { indexedFields: new Set<string>(), entityCount: null as number | null, resolved: false },
}));

vi.mock('@/stores/useOptionsStore', () => ({
  useOptionsStore: (sel: (s: unknown) => unknown) => sel({ openInEditor: () => undefined }),
}));

import { QueryDetailHeader } from '../QueryDetailHeader';

function makeDefinition(fieldNames: string[]): QueryDefinitionDto {
  return {
    instanceId: { kind: 0, localId: 7 },
    targetComponentType: 1,
    primaryIndexFieldIdx: 0,
    sortFieldIdx: -1,
    sortDescending: false,
    // The capture names the fields a query filters on, so no id mapping is needed to say which lacks an index.
    evaluators: fieldNames.map((fieldName, i) => ({ fieldIdx: i, fieldName, op: 0, opDisplay: '==' })),
    fieldDependencies: [],
    ownerSystemIds: [],
    aggregate: {
      executionCount: 12_000, totalWallNs: 1, avgWallNs: 1, p50WallNs: 1, p95WallNs: 1, p99WallNs: 4_000_000,
      totalRowsScanned: 10, totalRowsReturned: 1, avgSelectivity: 0.02,
    },
    userSource: { file: '', line: 0, method: '' },
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any;
}

function renderHeader(definition: QueryDefinitionDto, targetName = 'Unit', targetIsComponent = true) {
  return render(
    <QueryDetailHeader
      definition={definition}
      archetypeName={targetName}
      ownerNames={[]}
      targetId={1}
      targetIsComponent={targetIsComponent}
      reality={hoisted.reality}
    />,
  );
}

describe('QueryDetailHeader — index reality (#618 §4.3)', () => {
  beforeEach(() => {
    hoisted.reality = { indexedFields: new Set(), entityCount: null, resolved: false };
  });
  afterEach(cleanup);

  it('is absent entirely when the session has no database to ask', () => {
    // A standalone trace session: the Query Analyzer must render exactly as it did before this bridge existed.
    renderHeader(makeDefinition(['Position']));
    expect(screen.queryByTestId('query-index-reality')).toBeNull();
  });

  it('is absent when the parent supplies nothing at all', () => {
    // The header stays usable standalone — its contract is "everything pre-resolved", so an omitted prop must be a
    // no-op rather than a crash.
    render(
      <QueryDetailHeader
        definition={makeDefinition(['Position'])}
        archetypeName="Unit"
        ownerNames={[]}
        targetId={1}
        targetIsComponent
      />,
    );
    expect(screen.queryByTestId('query-index-reality')).toBeNull();
  });

  it('completes the diagnosis: no index on the filtered field, and how much data is really there', () => {
    hoisted.reality = { indexedFields: new Set(['Health']), entityCount: 340_000, resolved: true };

    renderHeader(makeDefinition(['Position']));

    const strip = screen.getByTestId('query-index-reality');
    expect(strip.textContent).toContain('340,000');
    expect(screen.getByTestId('query-index-verdict').textContent).toContain('no index on');
    expect(screen.getByTestId('query-index-verdict').textContent).toContain('Position');
  });

  it('names only the fields that actually lack an index', () => {
    hoisted.reality = { indexedFields: new Set(['Health']), entityCount: 10, resolved: true };

    renderHeader(makeDefinition(['Position', 'Health']));

    const verdict = screen.getByTestId('query-index-verdict').textContent ?? '';
    expect(verdict).toContain('Position');
    expect(verdict).not.toContain('Health');
  });

  it('says so when every filtered field is indexed', () => {
    hoisted.reality = { indexedFields: new Set(['Position']), entityCount: 5, resolved: true };

    renderHeader(makeDefinition(['Position']));

    expect(screen.getByTestId('query-index-verdict').textContent).toContain('every filtered field is indexed');
  });

  it('an archetype target reports the entity count and makes no index claim', () => {
    // Indexes live on an archetype's components, not the archetype — claiming anything here would be inventing.
    hoisted.reality = { indexedFields: new Set(), entityCount: 340_000, resolved: true };

    renderHeader(makeDefinition(['Position']), 'Unit', false);

    expect(screen.getByTestId('query-index-reality').textContent).toContain('340,000');
    expect(screen.queryByTestId('query-index-verdict')).toBeNull();
  });

  it('labels the figures as current, not as trace-time', () => {
    hoisted.reality = { indexedFields: new Set(), entityCount: 1, resolved: true };
    renderHeader(makeDefinition([]));
    // The §4.2 honesty caveat applies here too: an index added since the capture must not read as an explanation for
    // a slow run that predates it.
    expect(screen.getByTestId('query-index-reality').textContent).toContain('now');
  });
});
