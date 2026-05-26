/**
 * Global colour-palette module for the Workbench. Identity palettes — stable across the light /
 * dark themes — live here so panels and renderers share one source of truth instead of
 * re-deriving colours locally. Previously the phase palette lived inside the Critical-Path panel
 * folder; it is promoted here because the System DAG and Access Matrix consume it too.
 *
 * Theme-adaptive chrome (grid lines, tooltip backgrounds, …) is deliberately NOT here — that is
 * resolved per-frame against the active theme by the canvas theme module.
 */

import { TIMELINE_PALETTE } from '@/libs/profiler/canvas/canvasUtils';
import { lighten, rgbToHex } from '@/libs/color/contrast';
import { categoricalColor } from '@/libs/color/categorical';

/** One categorical colour as a stroke (bright edge) + fill (muted body) pair. */
export interface PaletteColor {
  stroke: string;
  fill: string;
}

/**
 * Categorical palettes are derived from {@link TIMELINE_PALETTE} — the 13-colour Turbo ramp — so
 * the whole Workbench draws from one set of hues. A Turbo ramp is perceptually *sequential*:
 * neighbouring entries look alike, which is wrong for a categorical lookup. `RAMP_ORDER` walks
 * the ramp in a strided order (every other slot, then the gaps) so consecutive categorical
 * indices land on widely-separated hues.
 */
const RAMP_ORDER: readonly number[] = [0, 2, 4, 6, 8, 10, 12, 1, 3, 5, 7, 9, 11];

/**
 * Turn a ramp colour into a categorical pair: the `fill` body is the {@link TIMELINE_PALETTE} hue
 * itself (unchanged), the `stroke` is a lightened copy for a bright edge. Label contrast is left
 * to the caller — `pickTextColorFor(fill)` resolves black-or-white per swatch.
 */
function pairFromRamp(rampIndex: number): PaletteColor {
  const hue = TIMELINE_PALETTE[rampIndex];
  return { stroke: lighten(hue, 0.3), fill: hue };
}

/**
 * Curated phase palette — the {@link TIMELINE_PALETTE} hues in strided categorical order. The
 * phase index drives the lookup (modulo size), so a phase keeps its colour within a session and
 * across sessions for any topology that declares phases in the same order.
 */
export const PHASE_PALETTE: readonly PaletteColor[] = RAMP_ORDER.map(pairFromRamp);

/** Neutral grey for an unphased system (<c>phaseIndex &lt; 0</c>). */
export const UNPHASED_COLOR: PaletteColor = { stroke: '#818898', fill: '#2b2f36' };

/**
 * Marker colour for a phase. <paramref name="phaseIndex"/> is the position in
 * <c>topology.phases</c> (declared order); pass <c>-1</c> for an unphased system.
 */
export function colorForPhase(phaseIndex: number): PaletteColor {
  return phaseIndex < 0 ? UNPHASED_COLOR : PHASE_PALETTE[phaseIndex % PHASE_PALETTE.length];
}

/**
 * Fixed worker-occupancy ribbon fill for the Critical Path (was `SYSTEM_PALETTE[5].fill` before the DS-2
 * system-colour consolidation). NOT a system-identity colour — one chosen warm tint for the occupancy band;
 * pinned to its prior value (the Turbo orange, ramp slot 10) so the ribbon stays pixel-identical.
 */
export const OCCUPANCY_FILL = TIMELINE_PALETTE[10];

/**
 * Stable identity colour for a system, keyed by name — the DS-2 shared categorical hue ({@link categoricalColor}),
 * so a system reads the SAME hue here as in the timeline lanes, the System DAG accent, the Access-Matrix header,
 * and the Query Analyzer (DS-2 "stable hue-per-object"). `fill` is the categorical swatch; `stroke` a lightened
 * edge for bar outlines. Replaces the former djb2-hash-into-`SYSTEM_PALETTE` lookup — a parallel palette, now removed.
 */
export function colorForSystem(systemName: string): PaletteColor {
  const fill = rgbToHex(categoricalColor(systemName));
  return { stroke: lighten(fill, 0.3), fill };
}

// ── Canvas identity palettes ────────────────────────────────────────────────
// The profiler canvas renderers' palettes already live in `libs/`; they are re-exported here so
// `@/libs/palettes` is the single discovery point for every Workbench palette.
export {
  TIMELINE_PALETTE,
  TIMELINE_PALETTE_LIGHT,
  SPAN_PALETTE,
  SPAN_PALETTE_LIGHT,
  GAUGE_PALETTE,
  OVERVIEW_PALETTE,
  SELECTED_COLOR,
  CACHE_EXCLUSIVE_COLOR,
} from '@/libs/profiler/canvas/canvasUtils';
