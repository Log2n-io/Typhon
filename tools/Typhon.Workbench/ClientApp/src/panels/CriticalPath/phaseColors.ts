/**
 * Stable phase → colour mapping for the Critical-Path view's 5 px phase marker (per design
 * intent: visually associate every system bar with its phase without relying on labels alone).
 *
 * The palette is curated rather than hue-hashed: a small set of well-spaced HSL hues that stay
 * legible in both dark and light themes. Phase index drives the lookup (modulo palette size), so
 * the same phase always gets the same colour within a session, and across sessions for any
 * topology that declares phases in the same order.
 */
const PALETTE: Array<{ stroke: string; fill: string }> = [
  // Hues spread across the wheel. Saturation/lightness chosen for dark theme readability against
  // the muted bar background; the same values are still distinguishable in light theme.
  { stroke: 'hsl(210, 70%, 60%)', fill: 'hsl(210, 50%, 35%)' }, // blue
  { stroke: 'hsl(140, 60%, 55%)', fill: 'hsl(140, 45%, 30%)' }, // green
  { stroke: 'hsl(40, 80%, 60%)',  fill: 'hsl(40, 60%, 35%)'  }, // amber
  { stroke: 'hsl(280, 60%, 65%)', fill: 'hsl(280, 45%, 35%)' }, // violet
  { stroke: 'hsl(0, 70%, 60%)',   fill: 'hsl(0, 50%, 35%)'   }, // red
  { stroke: 'hsl(180, 60%, 55%)', fill: 'hsl(180, 45%, 30%)' }, // teal
  { stroke: 'hsl(320, 65%, 65%)', fill: 'hsl(320, 50%, 35%)' }, // pink
  { stroke: 'hsl(60, 60%, 55%)',  fill: 'hsl(60, 45%, 30%)'  }, // yellow-green
];

/**
 * Returns the marker colour for a given phase. The marker is a 5 px line on the leading edge of
 * each system bar in the requested orientation; `stroke` is the visible stripe, `fill` is the
 * matching darker swatch used in the phase header.
 *
 * `phaseIndex` is the position in `topology.phases` (declared order). Pass `-1` for unphased
 * systems — they get a neutral grey.
 */
export function colorForPhase(phaseIndex: number): { stroke: string; fill: string } {
  if (phaseIndex < 0) return { stroke: 'hsl(220, 10%, 55%)', fill: 'hsl(220, 10%, 30%)' };
  return PALETTE[phaseIndex % PALETTE.length];
}
