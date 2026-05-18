import {
  GAUGE_PALETTE,
  OVERVIEW_PALETTE,
  PHASE_COLOR,
  SELECTED_COLOR,
  SPAN_PALETTE,
  SPAN_PALETTE_LIGHT,
  TIMELINE_PALETTE,
  TIMELINE_PALETTE_LIGHT,
} from './canvasUtils';

/**
 * Theme snapshot consumed by all canvas `draw*` functions. Split into three layers:
 *
 * 1. **Semantic tokens** (background, foreground, muted, border, etc.) — sourced from Tailwind's CSS
 *    variables; follow the active theme (dark/light).
 * 2. **Theme-adaptive chrome** (tooltip bg/text/border, bar color, over-P95 accent) — branched on `isDark`
 *    so contrast stays readable in both themes without pinning to semantic tokens (data viz sometimes wants
 *    "always-visible dark navy" which isn't the same thing as "theme.foreground").
 * 3. **Identity palettes** (SPAN, GAUGE, TIMELINE, SELECTED) — stable across themes; a subsystem's color is
 *    its identity.
 *
 * Resolve once per frame via {@link getStudioThemeTokens} and pass as an argument — avoids coupling draw
 * functions to the DOM.
 */
export interface StudioTheme {
  isDark: boolean;

  // Layer 1 — semantic (CSS var-backed)
  background: string;
  foreground: string;
  card: string;
  cardForeground: string;
  muted: string;
  mutedForeground: string;
  primary: string;
  border: string;
  destructive: string;

  // Layer 2 — theme-adaptive chrome
  gridColor: string;
  overviewBar: string;         // baseline per-tick bar
  overviewP95: string;         // tick exceeded P95 — attention-grabbing outlier
  tooltipBackground: string;   // opaque-ish backdrop
  tooltipBorder: string;
  tooltipText: string;

  // Layer 2b — time-area chrome (added in Phase 2b — every hard-coded literal from old GraphArea
  // becomes a token here so the port stays theme-clean).
  summaryStripBg: string;        // collapsed non-slot track strip fill
  selectedOutline: string;       // selected chunk/span outline stroke
  textOnLightBar: string;        // near-black — pair with light bars via per-bar luminance pick
  textOnDarkBar: string;         // near-white — pair with dark bars via per-bar luminance pick
  coalescedText: string;         // "N spans — zoom in" label on striped block
  idleBar: string;               // Scheduler.Worker.Idle / BetweenTick fill — deliberately muted

  // Layer 2c — gauge chrome (added in Phase 2c)
  gaugeNoDataBg: string;         // "no data" background fill inside a gauge track
  gaugeLiveLine: string;         // live-blocks thin line on Memory row 3
  gaugeSparkline: string;        // summary-mode spark-line tone
  gaugeLegendText: string;       // inline legend label text (used by drawInlineLegend + GC legend hints)
  miniRowLabelText: string;      // text inside mini-row label pill
  activitySilhouette: string;    // grey silhouette of activity in collapsed slot lanes
  labelPillBg: string;           // mini-row label-pill background
  crosshairLine: string;         // vertical crosshair line (cursor timestamp)
  crosshairPillBg: string;       // timestamp pill behind the crosshair
  zoomDragFill: string;          // drag-to-zoom selection rect fill
  zoomDragStroke: string;        // drag-to-zoom selection rect stroke

  // Layer 3 — identity (palettes + constants, same across themes)
  selectedStroke: string;
  phaseColor: string;
  overviewSelection: string;   // green selection overlay (identity — reads on both backgrounds)
  spans: readonly string[];
  gauges: readonly string[];
  timelineBands: readonly string[];
  /**
   * Off-CPU overlay palette, indexed by `OffCpuCategory` (SyncWait=0, Preempted=1, QuantumEnd=2, Paging=3, UserWait=4,
   * Idle=5, Other=6). Drawn at low alpha as a band over the thread lane, so the hues are picked saturated enough to read
   * through the compositing. Identity — a wait-reason category's color is its identity, stable across light/dark.
   */
  offCpu: readonly string[];
}

/**
 * Off-CPU category palette — index matches `OffCpuCategory`. Distinct hues; saturated because the renderer composites
 * them at ~0.32 alpha over the lane. Idle is a neutral grey (an idle hand-off is "nothing happened", visually quiet).
 */
export const OFF_CPU_PALETTE: readonly string[] = [
  '#5b8def', // 0 SyncWait    — blue
  '#e8893b', // 1 Preempted   — orange
  '#a87fd6', // 2 QuantumEnd  — purple
  '#3bb7a8', // 3 Paging      — teal
  '#c2b34a', // 4 UserWait    — ochre
  '#6b7280', // 5 Idle        — neutral grey
  '#8a8f99', // 6 Other       — dim grey-blue
];

/**
 * Theme-adaptive chrome — two parallel tables keyed by dark/light. Picked by `getStudioThemeTokens` based on
 * the root element's `.dark` class. Kept hex (not CSS vars) so canvas `rgba()`-style compositing works
 * without oklch conversion headaches.
 *
 * The shared `AdaptiveTheme` type below forces both tables to carry an identical key set — adding a token
 * to one without the other is a compile error instead of a silent `undefined` at runtime.
 */
interface AdaptiveTheme {
  gridColor: string;
  overviewBar: string;
  overviewP95: string;
  tooltipBackground: string;
  tooltipBorder: string;
  tooltipText: string;
  summaryStripBg: string;
  selectedOutline: string;
  textOnLightBar: string;
  textOnDarkBar: string;
  coalescedText: string;
  idleBar: string;
  gaugeNoDataBg: string;
  gaugeLiveLine: string;
  gaugeSparkline: string;
  gaugeLegendText: string;
  miniRowLabelText: string;
  activitySilhouette: string;
  labelPillBg: string;
  crosshairLine: string;
  crosshairPillBg: string;
  zoomDragFill: string;
  zoomDragStroke: string;
}

const ADAPTIVE_DARK: AdaptiveTheme = {
  gridColor: '#202025',
  overviewBar: OVERVIEW_PALETTE.bar,             // dark navy
  overviewP95: OVERVIEW_PALETTE.overP95,         // bright cyan
  tooltipBackground: 'rgba(29, 30, 32, 0.95)',
  tooltipBorder: '#34343a',
  tooltipText: '#e0e0e0',

  // Time-area chrome — dark values preserve the old GraphArea look.
  summaryStripBg: '#222',
  selectedOutline: '#ffffff',
  // Text on coloured bars — literal values from the old `GraphArea.tsx` (chunks: #000, spans: #eee).
  // Going any brighter than #eee looked "too clean" in side-by-side.
  textOnLightBar: '#000',
  textOnDarkBar: '#eee',
  coalescedText: '#ccc',
  idleBar: '#3f3f46',            // zinc-700 — recedes against the dark navy backdrop
  // Gauge chrome — dark values match the old gaugeDraw.ts literals.
  gaugeNoDataBg: 'rgba(180, 180, 200, 0.4)',
  gaugeLiveLine: 'rgba(170, 170, 170, 0.9)',
  gaugeSparkline: 'rgba(170, 170, 170, 0.75)',
  gaugeLegendText: '#e0e0e0',
  miniRowLabelText: '#ffffff',
  activitySilhouette: 'rgba(170, 170, 170, 0.55)',
  labelPillBg: 'rgba(0, 0, 0, 0.35)',
  crosshairLine: 'rgba(255, 255, 255, 0.3)',
  crosshairPillBg: 'rgba(30, 32, 36, 0.9)',
  zoomDragFill: 'rgba(100, 150, 255, 0.12)',
  zoomDragStroke: 'rgba(100, 150, 255, 0.6)',
};

const ADAPTIVE_LIGHT: AdaptiveTheme = {
  gridColor: '#d4d4d8',
  // 25% brighter than the earlier #6B7BA8 / #D63384 picks — each channel shifted 25% of the distance to 255.
  // Rationale: the prior hues were usable but too dense against the off-white card; the brighter shades sit
  // in the same family but carry less visual weight, which reads better for "data at a glance."
  overviewBar: '#909CBE',                        // lighter periwinkle
  overviewP95: '#E066A3',                        // lighter pink — still attention-grabbing against peers
  tooltipBackground: 'rgba(252, 252, 254, 0.97)',
  tooltipBorder: '#d4d4d8',
  tooltipText: '#1a1a1a',

  // Time-area chrome — light values chosen for contrast against the off-white card. Slate-based
  // neutrals (Tailwind slate scale) for foreground text; translucent greys for overlays.
  summaryStripBg: '#e5e7eb',
  selectedOutline: '#111827',
  // Match the dark-theme tones — a slightly darker `#1a1a1a` reads the same as `#000` against
  // light mode's card while taking slightly less retinal punch; keep `#eee` so the contrast
  // table across both themes stays symmetric.
  textOnLightBar: '#000',
  textOnDarkBar: '#eee',
  coalescedText: '#475569',
  idleBar: '#cbd5e1',            // slate-300 — neutral grey that recedes against the off-white card
  // Gauge chrome — light values use slate-scale neutrals so the muted greys of the old dark
  // values stay recognisable but read cleanly over off-white card bg.
  gaugeNoDataBg: 'rgba(148, 163, 184, 0.35)',
  gaugeLiveLine: 'rgba(71, 85, 105, 0.85)',
  gaugeSparkline: 'rgba(100, 116, 139, 0.7)',
  gaugeLegendText: '#334155',
  miniRowLabelText: '#0f172a',
  activitySilhouette: 'rgba(100, 116, 139, 0.45)',
  labelPillBg: 'rgba(255, 255, 255, 0.85)',
  crosshairLine: 'rgba(15, 23, 42, 0.35)',
  crosshairPillBg: 'rgba(252, 252, 254, 0.95)',
  zoomDragFill: 'rgba(59, 130, 246, 0.15)',
  zoomDragStroke: 'rgba(59, 130, 246, 0.7)',
};

/**
 * Resolve the current `StudioTheme` from Tailwind CSS variables + the active theme class. Called at the top
 * of every canvas frame so theme switches propagate without extra plumbing. `getComputedStyle` is ~1 µs per
 * call — negligible.
 */
export function getStudioThemeTokens(): StudioTheme {
  // SSR / test safety — document may be undefined.
  if (typeof document === 'undefined') {
    return buildTheme(true, null);
  }
  const isDark = document.documentElement.classList.contains('dark');
  return buildTheme(isDark, getComputedStyle(document.documentElement));
}

function buildTheme(isDark: boolean, cs: CSSStyleDeclaration | null): StudioTheme {
  const v = (name: string, fallback: string): string => {
    if (!cs) return fallback;
    const raw = cs.getPropertyValue(name).trim();
    return raw.length > 0 ? raw : fallback;
  };
  const adaptive = isDark ? ADAPTIVE_DARK : ADAPTIVE_LIGHT;

  return {
    isDark,

    // Semantic — CSS var-backed
    background: v('--background', isDark ? '#0f172a' : '#ffffff'),
    foreground: v('--foreground', isDark ? '#f1f5f9' : '#0f172a'),
    card: v('--card', isDark ? '#1e293b' : '#ffffff'),
    cardForeground: v('--card-foreground', isDark ? '#f1f5f9' : '#0f172a'),
    muted: v('--muted', isDark ? '#1e293b' : '#f1f5f9'),
    mutedForeground: v('--muted-foreground', isDark ? '#94a3b8' : '#64748b'),
    primary: v('--primary', isDark ? '#60a5fa' : '#3b82f6'),
    border: v('--border', isDark ? '#334155' : '#e2e8f0'),
    destructive: v('--destructive', isDark ? '#ef4444' : '#dc2626'),

    // Adaptive chrome
    gridColor: adaptive.gridColor,
    overviewBar: adaptive.overviewBar,
    overviewP95: adaptive.overviewP95,
    tooltipBackground: adaptive.tooltipBackground,
    tooltipBorder: adaptive.tooltipBorder,
    tooltipText: adaptive.tooltipText,

    // Time-area chrome (Phase 2b)
    summaryStripBg: adaptive.summaryStripBg,
    selectedOutline: adaptive.selectedOutline,
    textOnLightBar: adaptive.textOnLightBar,
    textOnDarkBar: adaptive.textOnDarkBar,
    coalescedText: adaptive.coalescedText,
    idleBar: adaptive.idleBar,
    gaugeNoDataBg: adaptive.gaugeNoDataBg,
    gaugeLiveLine: adaptive.gaugeLiveLine,
    gaugeSparkline: adaptive.gaugeSparkline,
    gaugeLegendText: adaptive.gaugeLegendText,
    miniRowLabelText: adaptive.miniRowLabelText,
    activitySilhouette: adaptive.activitySilhouette,
    labelPillBg: adaptive.labelPillBg,
    crosshairLine: adaptive.crosshairLine,
    crosshairPillBg: adaptive.crosshairPillBg,
    zoomDragFill: adaptive.zoomDragFill,
    zoomDragStroke: adaptive.zoomDragStroke,

    // Identity (stable across themes)
    selectedStroke: SELECTED_COLOR,
    phaseColor: PHASE_COLOR,
    overviewSelection: OVERVIEW_PALETTE.selection,
    // Span palette is theme-adaptive: dark mode uses the deep-violet → warm-amber ramp; light mode
    // uses rocket-custom so the selected-span outline (#111827) stays visible against every bar.
    spans: isDark ? SPAN_PALETTE : SPAN_PALETTE_LIGHT,
    gauges: GAUGE_PALETTE,
    timelineBands: isDark ? TIMELINE_PALETTE : TIMELINE_PALETTE_LIGHT,
    offCpu: OFF_CPU_PALETTE,
  };
}
