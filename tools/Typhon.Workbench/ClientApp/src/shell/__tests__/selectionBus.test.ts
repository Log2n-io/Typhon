import { beforeEach, describe, expect, it } from 'vitest';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { resolveChain } from '@/stores/selectionChain';
import { useSelectedResourceStore, type SelectedResource } from '@/stores/useSelectedResourceStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';

// Conformance suite A — selection-bus laws (IA §3, GAP-05). The keystone, tested before its consumers.

const bus = () => useSelectionStore.getState();

function resetAll() {
  useSelectionStore.getState().clear();
  useSelectedResourceStore.getState().clear();
  useSchemaInspectorStore.getState().reset();
  useDataBrowserStore.getState().reset();
  useProfilerSelectionStore.getState().clear();
  // reset() / clear() on the silos re-touch the bus via mirrors; clear the leaf last.
  useSelectionStore.getState().clearLeaf();
}

const sampleResource: SelectedResource = {
  resourceId: 'r-1',
  kind: 'ComponentTable',
  name: 'ComponentTable_Position',
  path: ['Storage', 'ComponentTable_Position'],
  raw: {} as SelectedResource['raw'],
};

describe('suite A — selection-bus laws', () => {
  beforeEach(resetAll);

  describe('A.1 write-through (one law per object type)', () => {
    it('select() writes the leaf for a primitive object type', () => {
      bus().select('system', 'Movement');
      expect(bus().leaf).toMatchObject({ type: 'system', ref: 'Movement' });
    });

    it('resource-tree selection mirrors to the bus leaf', () => {
      useSelectedResourceStore.getState().setSelected(sampleResource);
      expect(bus().leaf?.type).toBe('resource');
      expect(bus().leaf?.ref).toBe(sampleResource);
    });

    it('schema component + field selections mirror to the bus leaf', () => {
      useSchemaInspectorStore.getState().selectComponent('Position');
      expect(bus().leaf).toMatchObject({ type: 'component', ref: 'Position' });
      useSchemaInspectorStore.getState().selectField('X');
      expect(bus().leaf).toMatchObject({ type: 'field', ref: { component: 'Position', field: 'X' } });
    });

    it('data-browser archetype + entity selections mirror to the bus leaf', () => {
      useDataBrowserStore.getState().setArchetype('2002');
      expect(bus().leaf).toMatchObject({ type: 'archetype', ref: '2002' });
      useDataBrowserStore.getState().selectEntity('104656996');
      expect(bus().leaf).toMatchObject({ type: 'entity', ref: { archetypeId: '2002', entityId: '104656996' } });
    });

    it('file-map selection writes the bus leaf with its kind as the type', () => {
      // Stage 2 (#375): the File Map writes the bus directly (the `useDbMapSelectionStore` silo was retired);
      // the selection `kind` doubles as the leaf `type`.
      bus().select('page', { kind: 'page', pageIndex: 7 });
      expect(bus().leaf).toMatchObject({ type: 'page', ref: { kind: 'page', pageIndex: 7 } });
    });

    it('profiler selection mirrors: tick → tick, others → span', () => {
      useProfilerSelectionStore.getState().setSelected({ kind: 'tick', tickNumber: 12 });
      expect(bus().leaf?.type).toBe('tick');
      useProfilerSelectionStore.getState().setSelected({ kind: 'span', span: { id: 1 } as never });
      expect(bus().leaf?.type).toBe('span');
    });
  });

  describe('A.2 idempotence', () => {
    it('re-selecting the same primitive object does not re-stamp the leaf', () => {
      bus().select('resource', 'r-1');
      const first = bus().leaf;
      bus().select('resource', 'r-1');
      expect(bus().leaf).toBe(first); // same object reference → no re-notify
    });
  });

  describe('A.3 recency = leaf', () => {
    it('the most-recent primary selection is the leaf', () => {
      bus().select('resource', 'r-1');
      bus().select('system', 'Movement');
      expect(bus().leaf).toMatchObject({ type: 'system', ref: 'Movement' });
    });
  });

  describe('A.4 projection ≠ leaf', () => {
    it('a scalar projection updates its slot but never steals the leaf', () => {
      bus().select('span', { kind: 'span' });
      bus().setSystem('Movement'); // projection (e.g. the span projecting its system)
      expect(bus().system).toBe('Movement');
      expect(bus().leaf?.type).toBe('span'); // leaf unchanged
    });
  });

  describe('A.5 containment chain', () => {
    it('a field leaf resolves its component ancestor', () => {
      bus().select('field', { component: 'Position', field: 'X' });
      expect(resolveChain(bus().leaf, bus())).toEqual([{ type: 'component', ref: 'Position' }]);
    });

    it('a file-map cell leaf resolves segment ⊃ page ⊃ chunk', () => {
      const cell = { kind: 'cell', pageIndex: 5, segmentId: 3, chunkId: 2, cellOffset: 64 };
      bus().select('cell', cell);
      expect(resolveChain(bus().leaf, bus())).toEqual([
        { type: 'segment', ref: 3 },
        { type: 'page', ref: 5 },
        { type: 'chunk', ref: 2 },
      ]);
    });

    it('a file-map page leaf resolves its owning segment ancestor', () => {
      bus().select('page', { kind: 'page', pageIndex: 5, segmentId: 3 });
      expect(resolveChain(bus().leaf, bus())).toEqual([{ type: 'segment', ref: 3 }]);
    });

    it('a free page leaf (no owner) resolves no ancestor — not a bogus segment', () => {
      bus().select('page', { kind: 'page', pageIndex: 9 });
      expect(resolveChain(bus().leaf, bus())).toEqual([]);
    });

    it('a top-of-chain object has no ancestors', () => {
      bus().select('system', 'Movement');
      expect(resolveChain(bus().leaf, bus())).toEqual([]);
    });
  });

  describe('A.6 mirror parity (strangler migration)', () => {
    it('the silo path and the direct bus path produce the same leaf', () => {
      useSelectedResourceStore.getState().setSelected(sampleResource);
      const viaSilo = bus().leaf;
      resetAll();
      bus().select('resource', sampleResource);
      const viaBus = bus().leaf;
      expect({ type: viaSilo?.type, ref: viaSilo?.ref }).toEqual({ type: viaBus?.type, ref: viaBus?.ref });
    });
  });
});
