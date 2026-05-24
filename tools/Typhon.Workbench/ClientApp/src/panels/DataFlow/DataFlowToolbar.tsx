import { useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { type AggregationMode, type GranularityLevel, type XAxisMode, useDataFlowViewStore } from './useDataFlowViewStore';

/**
 * Top toolbar for the Data Flow Timeline (#327). Per design §11:
 * - Granularity slider (L0–L4) — Y-axis altitude
 * - X-axis mode (uniform / equal / log) — phase column scaling
 * - Aggregation mode (replay / envelope / density) — how multi-tick data condenses on the X axis
 * - Hide-untouched + Dim-skipped filter chips
 * - Hover-isolate escape hatch
 * - "?" button — opens a modal-ish overlay describing every control + visual cue (mirrors CriticalPath panel).
 */
export default function DataFlowToolbar() {
  const granularityLevel = useDataFlowViewStore((s) => s.granularityLevel);
  const xMode = useDataFlowViewStore((s) => s.xMode);
  const aggMode = useDataFlowViewStore((s) => s.aggMode);
  const hideUntouched = useDataFlowViewStore((s) => s.hideUntouched);
  const dimSkipped = useDataFlowViewStore((s) => s.dimSkipped);
  const hoverIsolateEnabled = useDataFlowViewStore((s) => s.hoverIsolateEnabled);
  const setGranularity = useDataFlowViewStore((s) => s.setGranularityLevel);
  const setXMode = useDataFlowViewStore((s) => s.setXMode);
  const setAggMode = useDataFlowViewStore((s) => s.setAggMode);
  const setHideUntouched = useDataFlowViewStore((s) => s.setHideUntouched);
  const setDimSkipped = useDataFlowViewStore((s) => s.setDimSkipped);
  const setHoverIsolate = useDataFlowViewStore((s) => s.setHoverIsolateEnabled);
  // App-wide "show legends + help affordances" flag, toggled by the `L` key. When off, the `?` button
  // hides — same convention as the Critical Path toolbar and the profiler's tick-overview help glyph.
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);

  const [helpOpen, setHelpOpen] = useState(false);

  return (
    <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-border bg-card px-2 py-1">
      <span className="text-xs text-muted-foreground">Granularity</span>
      <GranularitySegmented value={granularityLevel} onChange={setGranularity} />

      <Separator orientation="vertical" className="h-6" />

      <span className="text-xs text-muted-foreground">X-axis</span>
      <XModeSegmented value={xMode} onChange={setXMode} />

      <Separator orientation="vertical" className="h-6" />

      <span className="text-xs text-muted-foreground">Aggregate</span>
      <AggModeSegmented value={aggMode} onChange={setAggMode} />

      <Separator orientation="vertical" className="h-6" />

      <Button
        size="sm"
        variant={hideUntouched ? 'default' : 'outline'}
        className="h-7"
        onClick={() => setHideUntouched(!hideUntouched)}
        title="Hide tracks with no bars in the visible range"
      >
        Hide untouched
      </Button>
      <Button
        size="sm"
        variant={dimSkipped ? 'default' : 'outline'}
        className="h-7"
        onClick={() => setDimSkipped(!dimSkipped)}
        title="Dim systems whose summary reports a non-zero skipReason"
      >
        Dim skipped
      </Button>

      <Separator orientation="vertical" className="h-6" />

      <Button
        size="sm"
        variant={hoverIsolateEnabled ? 'default' : 'outline'}
        className="h-7"
        onClick={() => setHoverIsolate(!hoverIsolateEnabled)}
        title="Toggle hover-to-isolate (H)"
      >
        Hover isolate
      </Button>

      <div className="ml-auto" />

      {legendsVisible && (
        <button
          type="button"
          onClick={() => setHelpOpen((o) => !o)}
          className="flex h-5 w-5 items-center justify-center rounded-full border border-border bg-card text-foreground hover:bg-muted"
          title="Show controls + legend (toggle inline help with `L`)"
          aria-label="Show controls and legend"
        >
          ?
        </button>
      )}

      {helpOpen && <HelpOverlay onClose={() => setHelpOpen(false)} />}
    </div>
  );
}

/**
 * Modal overlay describing the Data Flow Timeline's controls + visual elements. Portaled to `document.body`
 * so it escapes any dockview ancestor's transforms / overflow boxes. Clicking outside or pressing Escape
 * closes it.
 */
function HelpOverlay({ onClose }: { onClose: () => void }) {
  return createPortal(
    <div
      className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/60"
      onClick={onClose}
      onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
      role="dialog"
      aria-modal="true"
      tabIndex={-1}
    >
      <div
        className="max-h-[85vh] w-[680px] max-w-[92vw] overflow-auto rounded-lg border border-border bg-card p-5 text-fs-base leading-snug text-foreground shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-fs-xl font-semibold">Data Flow — controls &amp; legend</h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded px-2 py-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
            aria-label="Close"
          >
            ✕
          </button>
        </div>

        <Section title="What you're looking at">
          <p>
            A Marey-style timeline where rows on the Y axis are <strong>data tracks</strong> (component domains, families,
            component types, or archetype × component pairs depending on granularity) and the X axis is
            <strong> phase-segmented time within one tick</strong>. Each system run that touched a row's data
            renders as a coloured bar; gaps between bars and at the end of a phase appear as hatched grey strips
            (waits).
          </p>
          <p className="mt-1 text-muted-foreground">
            The phase-header strip above the canvas is the structural skeleton — phases run sequentially, so each
            column is a discrete time slice within the tick. Click a header to cycle its width: default → collapsed
            → manually expanded → default.
          </p>
        </Section>

        <Section title="Granularity (Y axis)">
          <KeyTable rows={[
            ['L0',  'Domain — Components / Event Queues / Resources (3 rows)'],
            ['L1',  'Phase × Domain (3 × N phases)'],
            ['L2',  'Component-family (default; auto-falls-back to L1 when fewer than 8 families)'],
            ['L3',  'Component type — one row per component'],
            ['L4',  'Archetype × component — finest; one row per (archetype, component) pair'],
          ]} />
        </Section>

        <Section title="X-axis mode (phase column scaling)">
          <KeyTable rows={[
            ['Uniform', 'Phase column width proportional to wall-clock contribution (default — honest representation)'],
            ['Equal',   'Each phase gets 1/N of the screen — easier to inspect the structure of small phases'],
            ['Log',     'log1p compression — dominant phase shrinks, long tail of small phases stays readable'],
          ]} />
        </Section>

        <Section title="Aggregation mode (multi-tick condensation)">
          <KeyTable rows={[
            ['Replay',   'Single-tick replay on the dominant tick of the selection (default per spec D8)'],
            ['p5–p95',   'Range-aggregate envelope: per (track, system), one bar covering the 5th–95th percentile of bar starts/ends across selected ticks'],
            ['Density',  'Per-(track, phase) heat strip — alpha-modulated by touch count across the range. Loses per-system identity'],
          ]} />
        </Section>

        <Section title="Mouse">
          <KeyTable rows={[
            ['Wheel',          'Zoom in / out, anchored under the cursor (per-event deltaY normalized + RAF-batched, so trackpads stay smooth)'],
            ['Middle-click',   'Reset zoom — restore the full visible range'],
            ['Click bar',      'Select the system → mirrors to System DAG node + Access Matrix column'],
            ['Click row',      'Select the data track → mirrors to Access Matrix row + System DAG (highlights every system that touches it)'],
            ['Click phase',    'Cycle the phase column: default → collapsed → manually expanded → default'],
            ['Hover bar',      'Tooltip with system, phase, time range, access set, entity counts, gating'],
          ]} />
        </Section>

        <Section title="Keyboard">
          <KeyTable rows={[
            ['[ / ]',     'Step granularity coarser / finer'],
            ['1 / 2 / 3', 'X-axis mode (uniform / equal / log)'],
            ['F',         'Fit X axis to visible range — clears wheel-zoom (no-op if not zoomed)'],
            ['H',         'Toggle hover-to-isolate'],
            ['L',         'Toggle inline help affordances (this `?` button + the profiler\'s overview glyphs) — app-wide'],
            ['Esc',       'Clear cross-panel selection (system / track / phase)'],
          ]} />
        </Section>

        <Section title="Bar colour legend (access kind)">
          <ul className="list-disc space-y-0.5 pl-5">
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#dc2626' }} /> <strong>Write</strong> — system writes the row's component</li>
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#ea580c' }} /> <strong>Side-write</strong> — auxiliary write (component-other-than-primary-update)</li>
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#2563eb' }} /> <strong>Reads-fresh</strong> — fresh-read (current-tick, after the writer)</li>
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#0891b2' }} /> <strong>Reads-snapshot</strong> — snapshot read (previous-tick value, parallel-safe)</li>
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#65a30d' }} /> <strong>Reads</strong> — generic read (plus <span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#84cc16' }} /> <strong>additional-reads</strong>)</li>
            <li><span className="inline-block h-2.5 w-2.5 rounded-sm align-middle" style={{ backgroundColor: '#94a3b8' }} /> <strong>None</strong> — system listed in the archetype but doesn't touch the row's component</li>
          </ul>
          <p className="mt-1 text-muted-foreground">
            On L0 / L1 domain rows (no specific component), bar colour is the <em>strongest</em> access kind the
            system declares anywhere in the domain — so you still see "this system writes Components somewhere"
            without losing the colour cue.
          </p>
        </Section>

        <Section title="Hatched strips (waits)">
          <ul className="list-disc space-y-0.5 pl-5">
            <li><strong>Per-row gap</strong> (thin, single-row height) — between consecutive bars on the same track. Reveals serialized execution forced by topology or worker-claim stalls.</li>
            <li><strong>Phase-fence wait</strong> (wider, spans every row the phase touched) — the last visible bar in a phase ends before the phase fence. Indicates "all visible work is done in this phase, but the engine is still waiting for the fence".</li>
          </ul>
        </Section>

        <Section title="Filter chips">
          <KeyTable rows={[
            ['Hide untouched',  'Hide rows with zero bars in the visible range. Stays as a fallback row when nothing matches so the canvas isn\'t blank.'],
            ['Dim skipped',     'Render bars at 40% opacity when the system\'s SystemTickSummary reports a non-zero skipReason for that tick'],
            ['Hover isolate',   'When hovering a bar, dim every other bar that doesn\'t share its (system, tick) key — D3 unification mechanism'],
          ]} />
        </Section>

        <Section title="Tick source">
          <p>
            Replay mode shows the <strong>dominant tick</strong> in the selected range — the longest{' '}
            <code>durationUs</code>. Pick a different range on the Profiler tick overview to drive the dominant
            tick's selection. Envelope and density modes use the <em>full</em> selected range.
          </p>
        </Section>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="mb-4">
      <h3 className="mb-1.5 text-fs-sm font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      <div className="space-y-1 leading-snug">{children}</div>
    </div>
  );
}

function KeyTable({ rows }: { rows: Array<[string, string]> }) {
  return (
    <table className="w-full">
      <tbody>
        {rows.map(([k, v]) => (
          <tr key={k} className="align-top">
            <td className="w-32 py-0.5 pr-3"><kbd className="rounded border border-border bg-muted px-1.5 py-0.5 text-fs-xs">{k}</kbd></td>
            <td className="py-0.5 text-muted-foreground">{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

const GRANULARITY_LABELS: Record<GranularityLevel, string> = {
  L0: 'L0',
  L1: 'L1',
  L2: 'L2',
  L3: 'L3',
  L4: 'L4',
};

const GRANULARITY_DESCRIPTIONS: Record<GranularityLevel, string> = {
  L0: 'Domain — Components / Queues / Resources',
  L1: 'Phase × Domain',
  L2: 'Component-family (default)',
  L3: 'Component type',
  L4: 'Archetype × component (finest)',
};

function GranularitySegmented({
  value,
  onChange,
}: {
  value: GranularityLevel;
  onChange: (level: GranularityLevel) => void;
}) {
  const levels: GranularityLevel[] = ['L0', 'L1', 'L2', 'L3', 'L4'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {levels.map((level) => (
        <button
          key={level}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === level
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          title={GRANULARITY_DESCRIPTIONS[level]}
          onClick={() => onChange(level)}
        >
          {GRANULARITY_LABELS[level]}
        </button>
      ))}
    </div>
  );
}

const X_MODE_LABELS: Record<XAxisMode, string> = {
  uniform: 'Uniform',
  equal: 'Equal',
  log: 'Log',
};

function XModeSegmented({
  value,
  onChange,
}: {
  value: XAxisMode;
  onChange: (mode: XAxisMode) => void;
}) {
  const modes: XAxisMode[] = ['uniform', 'equal', 'log'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {modes.map((mode) => (
        <button
          key={mode}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === mode
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          onClick={() => onChange(mode)}
        >
          {X_MODE_LABELS[mode]}
        </button>
      ))}
    </div>
  );
}

const AGG_MODE_LABELS: Record<AggregationMode, string> = {
  replay:   'Replay',
  envelope: 'p5–p95',
  density:  'Density',
};

const AGG_MODE_DESCRIPTIONS: Record<AggregationMode, string> = {
  replay:   'Single-tick replay on the dominant tick (default)',
  envelope: 'Range-aggregate p5–p95 envelope across selected ticks',
  density:  'Per-(track, phase) heat strip — touch density across the range',
};

function AggModeSegmented({
  value,
  onChange,
}: {
  value: AggregationMode;
  onChange: (mode: AggregationMode) => void;
}) {
  const modes: AggregationMode[] = ['replay', 'envelope', 'density'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {modes.map((mode) => (
        <button
          key={mode}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === mode
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          title={AGG_MODE_DESCRIPTIONS[mode]}
          onClick={() => onChange(mode)}
        >
          {AGG_MODE_LABELS[mode]}
        </button>
      ))}
    </div>
  );
}
