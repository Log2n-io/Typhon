import { describe, expect, it, beforeEach } from 'vitest';
import {
  SchemaLayoutRenderer,
  CACHE_LINE_BYTES,
  LAYOUT_METRICS,
  DEFAULT_THEME,
} from '../SchemaLayoutRenderer';
import type { ComponentSchema, Field } from '../../hooks/schema/types';

// Minimal HTMLCanvasElement stand-in — only implements what the renderer touches. Keeping this in
// the test file (rather than a shared helper) makes the failure mode obvious when the renderer
// starts calling new 2D-context methods.
function makeMockCanvas(width: number, height: number): HTMLCanvasElement {
  const calls: { op: string; args: unknown[] }[] = [];
  const record = (op: string) => (...args: unknown[]) => {
    calls.push({ op, args });
  };

  const ctx = {
    // Property sinks. Assigning to these on the real context updates rendering state; the mock
    // just accepts writes so the renderer code runs without errors.
    fillStyle: '',
    strokeStyle: '',
    lineWidth: 0,
    font: '',
    textBaseline: '',
    textAlign: '',
    // Method sinks. Every rendering method called by the renderer.
    setTransform: record('setTransform'),
    fillRect: record('fillRect'),
    strokeRect: record('strokeRect'),
    fillText: record('fillText'),
    beginPath: record('beginPath'),
    moveTo: record('moveTo'),
    lineTo: record('lineTo'),
    stroke: record('stroke'),
    rect: record('rect'),
    clip: record('clip'),
    save: record('save'),
    restore: record('restore'),
    closePath: record('closePath'),
    fill: record('fill'),
    // Approximate monospace width — 6px per character. Close enough for the fitText ellipsis logic
    // to behave deterministically under test; exact metrics aren't what these tests check.
    measureText: (text: string) => ({ width: text.length * 6 }),
    __calls: calls,
  };

  return {
    width,
    height,
    getContext: (kind: string) => (kind === '2d' ? ctx : null),
    getBoundingClientRect: () => ({
      x: 0,
      y: 0,
      width,
      height,
      top: 0,
      left: 0,
      right: width,
      bottom: height,
      toJSON() {
        return {};
      },
    }),
  } as unknown as HTMLCanvasElement;
}

function callsOf(canvas: HTMLCanvasElement): { op: string; args: unknown[] }[] {
  const ctx = canvas.getContext('2d') as unknown as { __calls: { op: string; args: unknown[] }[] };
  return ctx.__calls;
}

function field(name: string, offset: number, size: number, overrides: Partial<Field> = {}): Field {
  return {
    name,
    typeName: 'int',
    typeFullName: 'System.Int32',
    offset,
    size,
    fieldId: 0,
    isIndexed: false,
    indexAllowsMultiple: false,
    ...overrides,
  };
}

function schema(storageSize: number, fields: Field[]): ComponentSchema {
  return {
    typeName: 'Test',
    fullName: 'Test.Component',
    storageSize,
    totalSize: storageSize,
    allowMultiple: false,
    revision: 1,
    fields,
    storageMode: 'Versioned',
  };
}

describe('SchemaLayoutRenderer', () => {
  let canvas: HTMLCanvasElement;
  let renderer: SchemaLayoutRenderer;

  beforeEach(() => {
    canvas = makeMockCanvas(800, 400);
    renderer = new SchemaLayoutRenderer(canvas);
    renderer.setTheme(DEFAULT_THEME);
  });

  describe('CACHE_LINE_BYTES', () => {
    it('is exactly 64 (x64 target)', () => {
      expect(CACHE_LINE_BYTES).toBe(64);
    });
  });

  describe('computePadding', () => {
    it('returns empty for empty schema', () => {
      renderer.setSchema(null);
      expect(renderer.computePadding()).toEqual([]);
    });

    it('returns a gap between two fields', () => {
      renderer.setSchema(
        schema(8, [field('A', 0, 1), field('B', 4, 4)]),
      );
      expect(renderer.computePadding()).toEqual([{ offset: 1, size: 3 }]);
    });

    it('returns tail padding when fields do not reach storageSize', () => {
      renderer.setSchema(schema(16, [field('A', 0, 4)]));
      expect(renderer.computePadding()).toEqual([{ offset: 4, size: 12 }]);
    });

    it('returns no padding when fields are tightly packed', () => {
      renderer.setSchema(
        schema(8, [field('A', 0, 4), field('B', 4, 4)]),
      );
      expect(renderer.computePadding()).toEqual([]);
    });

    it('returns a leading gap when first field starts after 0', () => {
      renderer.setSchema(schema(12, [field('A', 4, 8)]));
      expect(renderer.computePadding()).toEqual([{ offset: 0, size: 4 }]);
    });
  });

  describe('computeCrossBoundary', () => {
    it('returns empty when no field crosses a 64-byte boundary', () => {
      renderer.setSchema(
        schema(128, [field('A', 0, 8), field('B', 56, 8)]),
      );
      expect(renderer.computeCrossBoundary()).toEqual([]);
    });

    it('flags a field that straddles a cache-line boundary', () => {
      renderer.setSchema(
        schema(128, [field('A', 60, 8)]), // 60..67 — crosses 64
      );
      const flagged = renderer.computeCrossBoundary();
      expect(flagged).toHaveLength(1);
      expect(flagged[0].name).toBe('A');
    });

    it('does not flag a field that ends exactly on a boundary', () => {
      renderer.setSchema(
        schema(128, [field('A', 56, 8)]), // 56..63 — ends on 63, boundary at 64 is safe
      );
      expect(renderer.computeCrossBoundary()).toEqual([]);
    });
  });

  describe('hitTest', () => {
    const LEFT = LAYOUT_METRICS.LEFT_MARGIN;
    const TOP = LAYOUT_METRICS.TOP_MARGIN + LAYOUT_METRICS.RULER_HEIGHT;
    const ROW = LAYOUT_METRICS.ROW_HEIGHT;

    it('returns null before schema is set', () => {
      expect(renderer.hitTest(LEFT + 10, TOP + 10)).toBeNull();
    });

    it('returns null for clicks in the gutter', () => {
      renderer.setSchema(schema(8, [field('A', 0, 8)]));
      expect(renderer.hitTest(5, TOP + 10)).toBeNull();
    });

    it('returns null for clicks in the ruler', () => {
      renderer.setSchema(schema(8, [field('A', 0, 8)]));
      expect(renderer.hitTest(LEFT + 10, LAYOUT_METRICS.TOP_MARGIN + 5)).toBeNull();
    });

    it('returns null for clicks on padding', () => {
      renderer.setSchema(schema(16, [field('A', 0, 4), field('B', 8, 8)]));
      // Click at byte offset 5 (in the gap 4..7)
      const bytePx = Math.max(LAYOUT_METRICS.MIN_BYTE_PX, Math.floor((800 - LEFT - 8) / 64));
      const x = LEFT + 5 * bytePx + 1;
      expect(renderer.hitTest(x, TOP + 5)).toBeNull();
    });

    it('returns the field for a click inside its bounds', () => {
      renderer.setSchema(schema(16, [field('A', 0, 4), field('B', 8, 8)]));
      const bytePx = Math.max(LAYOUT_METRICS.MIN_BYTE_PX, Math.floor((800 - LEFT - 8) / 64));
      // Click at byte offset 10 (inside B which is 8..15)
      const x = LEFT + 10 * bytePx + 1;
      const hit = renderer.hitTest(x, TOP + 5);
      expect(hit?.name).toBe('B');
    });

    it('resolves clicks in the second cache-line row to fields at higher offsets', () => {
      // Build a 128-byte component with a field at offset 64
      renderer.setSchema(schema(128, [field('X', 0, 4), field('Y', 64, 8)]));
      const bytePx = Math.max(LAYOUT_METRICS.MIN_BYTE_PX, Math.floor((800 - LEFT - 8) / 64));
      // Byte 64 in the second row (because BYTES_PER_ROW = 64) — column 0 of row 1
      const x = LEFT + 0 * bytePx + 1;
      const hit = renderer.hitTest(x, TOP + ROW + 5);
      expect(hit?.name).toBe('Y');
    });
  });

  describe('render', () => {
    it('draws the background rectangle even with no schema', () => {
      renderer.render();
      const ops = callsOf(canvas).map((c) => c.op);
      expect(ops).toContain('setTransform');
      expect(ops).toContain('fillRect');
    });

    it('calls fillRect at least once per field when schema is present', () => {
      renderer.setSchema(schema(16, [field('A', 0, 4), field('B', 4, 4)]));
      renderer.render();
      const fillRectCount = callsOf(canvas).filter((c) => c.op === 'fillRect').length;
      // At minimum: 1 background + 2 field bodies + any indexed accents. Expect ≥ 3.
      expect(fillRectCount).toBeGreaterThanOrEqual(3);
    });

    it('draws a bookmark index glyph (polygon) for indexed fields', () => {
      renderer.setSchema(
        schema(8, [field('Key', 0, 4, { isIndexed: true }), field('Value', 4, 4)]),
      );
      renderer.render();
      // The bookmark is drawn via beginPath → 5× moveTo/lineTo → closePath → fill. At least one
      // closePath+fill pair distinguishes the icon from the padding diagonals (which use stroke).
      const ops = callsOf(canvas).map((c) => c.op);
      expect(ops).toContain('closePath');
      expect(ops).toContain('fill');
    });

    it('draws cache-line boundary lines for components that span multiple cache lines', () => {
      // 128-byte component has one internal cache-line boundary at byte 64
      renderer.setSchema(schema(128, [field('A', 0, 128)]));
      renderer.render();
      const strokeCount = callsOf(canvas).filter((c) => c.op === 'stroke').length;
      expect(strokeCount).toBeGreaterThan(0);
    });
  });
});
