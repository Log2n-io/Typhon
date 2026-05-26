import { describe, expect, it } from 'vitest';
import {
  colorForSystem,
  colorForPhase,
  PHASE_PALETTE,
  UNPHASED_COLOR,
  OCCUPANCY_FILL,
  TIMELINE_PALETTE,
} from '@/libs/palettes';
import { categoricalColor } from '@/libs/color/categorical';
import { rgbToHex } from '@/libs/color/contrast';

// DS-2 system-color consolidation: colorForSystem now draws from the shared categorical scale (the dead djb2
// SYSTEM_PALETTE lookup is gone). Phase identity stays as-is; OCCUPANCY_FILL pins the one ex-SYSTEM_PALETTE use.

describe('palettes — colorForSystem (DS-2 system identity)', () => {
  it('fill is the shared categorical hue — cross-view identity, same source as DAG/matrix/timeline', () => {
    for (const name of ['Physics', 'AI', 'Render', 'Network']) {
      expect(colorForSystem(name).fill).toBe(rgbToHex(categoricalColor(name)));
    }
  });

  it('is deterministic and distinct per name', () => {
    expect(colorForSystem('AI')).toEqual(colorForSystem('AI'));
    const fills = new Set(['Physics', 'AI', 'Render', 'Network', 'Audio'].map((n) => colorForSystem(n).fill));
    expect(fills.size).toBe(5);
  });

  it('stroke is a lightened edge distinct from the fill', () => {
    const c = colorForSystem('Physics');
    expect(c.stroke).not.toBe(c.fill);
  });
});

describe('palettes — phase palette unchanged + occupancy pin', () => {
  it('colorForPhase indexes PHASE_PALETTE; unphased for < 0; wraps modulo length', () => {
    expect(colorForPhase(0)).toEqual(PHASE_PALETTE[0]);
    expect(colorForPhase(-1)).toEqual(UNPHASED_COLOR);
    expect(colorForPhase(PHASE_PALETTE.length)).toEqual(PHASE_PALETTE[0]);
  });

  it('OCCUPANCY_FILL stays pinned to the prior SYSTEM_PALETTE[5].fill value (Turbo ramp slot 10)', () => {
    expect(OCCUPANCY_FILL).toBe(TIMELINE_PALETTE[10]);
  });
});
