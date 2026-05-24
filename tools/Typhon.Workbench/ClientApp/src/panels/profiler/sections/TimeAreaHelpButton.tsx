import { type ReactNode, useState } from 'react';
import { createPortal } from 'react-dom';

/**
 * "?" help button mounted in the TimeArea gutter icon cluster, beside the section-filter funnel.
 * Clicking toggles a portaled modal that documents every control + visual element of the time area.
 *
 * Modelled on `CriticalPathToolbar`'s help overlay — same portal-to-body / click-outside / Escape
 * pattern, same `Section` / `KeyTable` building blocks, so the two help surfaces feel identical.
 *
 * The caller (TimeArea) only mounts this when `legendsVisible` is on, mirroring the per-track "?"
 * canvas glyphs — inline help is an opt-in chrome layer toggled app-wide by the `l` palette command.
 */
export function TimeAreaHelpButton(): React.JSX.Element {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex h-5 w-5 items-center justify-center rounded-full border border-border bg-card font-mono text-fs-sm text-foreground hover:bg-muted"
        title="How to use the time area (toggle inline help with `l`)"
        aria-label="How to use the time area"
      >
        ?
      </button>
      {open && <HelpDialog onClose={() => setOpen(false)} />}
    </>
  );
}

/**
 * Modal overlay describing the TimeArea — what each track shows, every mouse gesture, the gutter
 * controls, and the colour / shape legend. Portaled to `document.body` so it escapes any dockview
 * ancestor's transform / overflow clip. Click-outside + Escape close it.
 */
function HelpDialog({ onClose }: { onClose: () => void }): React.JSX.Element {
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
        className="max-h-[85vh] w-[720px] max-w-[92vw] overflow-auto rounded-lg border border-border bg-card p-5 font-mono text-fs-sm text-foreground shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-fs-lg font-semibold">Time Area — controls &amp; legend</h2>
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
            The detailed timeline for the tick range selected in the <strong>Tick Overview</strong>{' '}
            above — drag a range there (the green selection) and it drives this view. The X axis is{' '}
            wall-clock time in microseconds; tracks stack vertically. The <strong>gutter</strong> on{' '}
            the left names each track and carries its collapse chevron.
          </p>
          <p className="mt-1 text-muted-foreground">
            Top to bottom: a time <strong>ruler</strong>, then — when present — gauge charts, one{' '}
            lane per worker <strong>thread</strong>, one lane per ECS <strong>system</strong>, and{' '}
            engine-operation <strong>mini-rows</strong>. Any section can be hidden or collapsed from{' '}
            the gutter controls below.
          </p>
        </Section>

        <Section title="Mouse">
          <KeyTable rows={[
            ['Wheel',                   'Zoom the time axis in / out, anchored under the cursor'],
            ['Shift + Wheel',           'Pan along the time axis'],
            ['Ctrl + Wheel',            'Scroll vertically through the track stack'],
            ['Horizontal wheel / tilt', 'Pan along the time axis'],
            ['Left-drag',               'Drag-to-zoom — rubber-band a time range, 800 ms ease into it'],
            ['Shift + Left-drag',       'Pan the viewport (horizontal + vertical)'],
            ['Middle-drag',             'Pan the viewport (horizontal + vertical)'],
            ['Click a bar / event',     'Select it → details in the right-hand pane'],
            ['Double-click a bar',      'Animated zoom to that element’s time range'],
            ['Ctrl + Double-click',     'Open the emitting source location in your editor (spans / chunks with PDB attribution)'],
            ['Click a gutter chevron',  'Collapse / expand the track'],
            ['Hover',                   'Multi-line tooltip — gauges, spans, chunks, phases, ops'],
          ]} />
        </Section>

        <Section title="Gutter controls">
          <p>Three icons sit at the right edge of the ruler-row gutter:</p>
          <ul className="mt-1 list-disc space-y-1 pl-5">
            <li>
              <strong>Sliders</strong> — <em>Display options</em>: the span-colour lens, the off-CPU
              overlay toggle, and dynamic track height. These change <em>how</em> visible sections render.
            </li>
            <li>
              <strong>Funnel</strong> — <em>Section filter</em>: a searchable tri-state tree to show /
              hide Gauges, Threads, Systems and Engine Operations, with All / None shortcuts. Gauge and
              engine-op rows also carry per-track collapse state here.
            </li>
            <li>
              <strong>?</strong> — this dialog.
            </li>
          </ul>
          <p className="mt-1 text-muted-foreground">
            With inline legends on (the <kbd className="rounded border border-border bg-muted px-1 py-0.5 text-fs-xs">l</kbd>{' '}
            palette command), every track also gets its own <strong>?</strong> glyph in the gutter —
            hover it for that track's specific help.
          </p>
        </Section>

        <Section title="Tracks">
          <ul className="list-disc space-y-1 pl-5">
            <li>
              <strong>Ruler</strong> — the viewport time axis. Tick boundaries are marked so you can
              line work up against the scheduler tick it ran in.
            </li>
            <li>
              <strong>Gauges</strong> — per-tick sampled charts: <em>Memory</em>, <em>Page Cache</em>,{' '}
              <em>Transient Store</em>, <em>WAL</em>, <em>Transactions + UoW</em>. Each chevron cycles
              three states — <em>summary</em> → <em>expanded</em> → <em>double</em>. The whole region
              hides via <em>Toggle Gauge Region</em>.
            </li>
            <li>
              <strong>Thread lanes</strong> — one per worker thread. The top strip is scheduler{' '}
              <em>chunks</em> (one coloured bar per ECS system execution bound to that thread this
              tick); below it, the nested instrumentation <em>spans</em> are drawn as a flame graph,
              one row per call depth — depth 0 is the outermost op (e.g. Transaction.Commit), deeper
              rows its nested calls (BTree.Insert, PagedMMF.GetPage…).
            </li>
            <li>
              <strong>System lanes</strong> — one per ECS system: every chunk that ran the system in
              the viewport, regardless of which thread it landed on. Many small bars spread across
              time = ran in parallel; one wide bar per tick = serial-only.
            </li>
            <li>
              <strong>Engine-operation mini-rows</strong> — discrete engine events: <em>Phases</em>,{' '}
              <em>Page Cache</em> (fetch / allocate / evict / flush), <em>Disk IO</em>,{' '}
              <em>Transactions</em>, <em>WAL</em>, <em>Checkpoint</em>.
            </li>
          </ul>
        </Section>

        <Section title="Colour &amp; shape legend">
          <ul className="list-disc space-y-1 pl-5">
            <li>
              <strong>Span bars</strong> — coloured by the <em>Color spans by</em> lens (Display
              options): <em>name</em> (stable hash — default), <em>thread</em>, <em>depth</em>, or{' '}
              <em>duration</em> (log-scale heat ramp, makes outliers pop).
            </li>
            <li>
              <strong>Chunk bars</strong> — stable hash of the system index; the same system keeps
              its hue across both thread lanes and system lanes.
            </li>
            <li>
              <strong>Off-CPU overlay</strong> — translucent diagonal-hatched bars over a thread lane
              mark intervals where the OS switched the thread out (context switches); the hatch colour
              encodes the wait category. Toggle it in Display options.
            </li>
            <li>
              <strong>Coalesced spans</strong> — adjacent spans narrower than ~1 px at the same depth
              merge into a grey block labelled <em>"N spans — zoom in"</em>. Zoom in to resolve them.
            </li>
            <li>
              <strong>Pending stripes</strong> — a diagonal-stripe overlay marks time ranges whose
              chunks are still being decoded from the trace cache; it clears as data arrives.
            </li>
            <li>
              <strong>Selection</strong> — the clicked bar / event is outlined; its detail shows in
              the right-hand pane.
            </li>
          </ul>
        </Section>

        <Section title="Navigation">
          <p>
            The viewport is the green selection from the Tick Overview — drag a range there, or
            drag-to-zoom here, or double-click any bar to zoom to it. The command palette also
            carries <em>Zoom to Full Trace</em>, <em>Pan Left / Right</em>, <em>Toggle Gauge
            Region</em>, <em>Toggle Per-System Lanes</em> and <em>Toggle Legends</em>.
          </p>
        </Section>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: ReactNode }): React.JSX.Element {
  return (
    <div className="mb-4">
      <h3 className="mb-1.5 text-fs-base font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      <div className="space-y-1 leading-snug">{children}</div>
    </div>
  );
}

function KeyTable({ rows }: { rows: Array<[string, string]> }): React.JSX.Element {
  return (
    <table className="w-full">
      <tbody>
        {rows.map(([k, v]) => (
          <tr key={k} className="align-top">
            <td className="w-48 py-0.5 pr-3">
              <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 text-fs-xs">{k}</kbd>
            </td>
            <td className="py-0.5 text-muted-foreground">{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
