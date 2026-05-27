import { useState } from 'react';
import { Item as RovingItem, Root as RovingRoot } from '@radix-ui/react-roving-focus';
import { ChevronDown, ChevronRight, GitBranch, Info, ListTree, Network, Workflow } from 'lucide-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useQueryDefinitions } from '@/panels/QueryAnalyzer/useQueryDefinitions';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { revealQueryInAnalyzer } from '@/shell/commands/profilerCommands';
import { revealSystemInCriticalPath, revealSystemInDag, revealSystemInDataFlow, revealSystemInInspector } from '@/shell/commands/openDbMap';

/**
 * Systems & Queries Navigator (zone C, Trace/Attach) — the left-rail "what exists" list for a profiler
 * session, the trace/attach analogue of the Resource Tree (IA §1 zone C, §6). It reuses the existing
 * profiler-metadata (systems) and query-catalog hooks; a row click writes the **unified selection bus**
 * leaf so the right-rail Inspector re-targets (Stage 1 load-a-file slice). Deep profiler/query views
 * return in Stages 3-4; this navigator is shell, always present in a profiler session.
 */
export default function SystemsQueriesNavigatorPanel() {
  const sessionId = useSessionStore((s) => s.sessionId);
  // Trigger + hydrate the metadata fetch (the navigator is the owner now that the Profiler panel is gated).
  const metaQuery = useProfilerMetadata(sessionId);
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const buildError = useProfilerSessionStore((s) => s.buildError);
  const { definitions, isError: queriesError } = useQueryDefinitions();
  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  const setSystem = useSelectionStore((s) => s.setSystem);

  const systems = metadata?.systems ?? [];

  if (buildError) {
    return <NavMessage tone="error">{buildError}</NavMessage>;
  }
  // 202 build-in-progress: metadata not yet hydrated and no terminal error.
  if (!metadata && metaQuery.isLoading) {
    return <NavMessage tone="muted">Building trace index…</NavMessage>;
  }
  if (systems.length === 0 && definitions.length === 0) {
    return <NavMessage tone="muted">No systems or queries in this trace.</NavMessage>;
  }

  const selectedSystem = leaf?.type === 'system' ? (leaf.ref as string) : null;
  const selectedQueryLocalId =
    leaf?.type === 'query' && leaf.ref !== null && typeof leaf.ref === 'object'
      ? String((leaf.ref as { localId?: unknown }).localId)
      : null;

  // PC-8 roving: one tab stop for the whole navigator, ArrowUp/Down move the keyboard cursor between the
  // section headers + rows (Radix RovingFocusGroup — vetted, not hand-rolled). Esc backs focus out of the list.
  const onEsc = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      (document.activeElement as HTMLElement | null)?.blur();
    }
  };
  return (
    <RovingRoot asChild orientation="vertical" loop>
      <div className="flex h-full w-full flex-col overflow-hidden bg-background" onKeyDown={onEsc}>
      {/* Pane header — carries the active-panel cue (DS-4): `.dv-active-group .wb-pane-header` tints it when
          this navigator is the focused pane, the same affordance the Resources/Inspector panes render. */}
      <div className="wb-pane-header flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
        <span className="text-fs-xs font-medium uppercase tracking-wide text-muted-foreground">Navigator</span>
      </div>
      <div className="min-h-0 flex-1 overflow-auto">
      <NavSection
        icon={<Workflow className="h-3.5 w-3.5" />}
        title="Systems"
        count={systems.length}
      >
        {systems.map((sys) => {
          const name = sys.name ?? `System[${sys.index}]`;
          return (
            <SystemNavRow
              key={String(sys.index)}
              name={name}
              phaseName={sys.phaseName ?? undefined}
              selected={selectedSystem === name}
              onSelect={() => {
                setSystem(name); // projection — highlights the system wherever it's shown
                select('system', name); // primary — the Inspector leaf
              }}
            />
          );
        })}
      </NavSection>

      <NavSection
        icon={<ListTree className="h-3.5 w-3.5" />}
        title="Queries"
        count={definitions.length}
      >
        {queriesError ? (
          <NavMessage tone="muted">Query catalog unavailable.</NavMessage>
        ) : (
          definitions.map((q) => {
            const localId = String(q.instanceId.localId);
            return (
              <NavRow
                key={`${q.instanceId.kind}:${localId}`}
                label={`Query #${localId}`}
                detail={`${Number(q.aggregate.executionCount).toLocaleString()} exec`}
                selected={selectedQueryLocalId === localId}
                // First-class navigator entry — open/focus the Query Analyzer and select this query
                // (reveal writes the bus leaf too, so the row's `selected` highlight still tracks).
                onClick={() => revealQueryInAnalyzer(Number(q.instanceId.kind), Number(q.instanceId.localId))}
              />
            );
          })
        )}
      </NavSection>
      </div>
      </div>
    </RovingRoot>
  );
}

function NavSection({
  icon,
  title,
  count,
  children,
}: {
  icon: React.ReactNode;
  title: string;
  count: number;
  children: React.ReactNode;
}) {
  const [open, setOpen] = useState(true);
  return (
    <div className="border-b border-border">
      <RovingItem asChild>
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left text-fs-sm font-medium uppercase tracking-wide text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
        >
          {open ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
          {icon}
          <span>{title}</span>
          <span className="ml-auto tabular-nums">{count}</span>
        </button>
      </RovingItem>
      {open && <div className="pb-1">{children}</div>}
    </div>
  );
}

function NavRow({
  label,
  detail,
  selected,
  onClick,
}: {
  label: string;
  detail?: string;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <RovingItem asChild>
      <button
        type="button"
        onClick={onClick}
        aria-pressed={selected}
        className={
          'flex h-[22px] w-full items-center gap-2 px-2 text-left text-fs-lg ' +
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ' +
          // focus (the ring) is rendered distinctly from selection (the accent fill) — DS-4 focus≠selection.
          (selected ? 'bg-accent text-accent-foreground' : 'text-foreground hover:bg-muted/60')
        }
      >
        <span className="truncate">{label}</span>
        {detail && <span className="ml-auto shrink-0 truncate text-fs-xs text-muted-foreground">{detail}</span>}
      </button>
    </RovingItem>
  );
}

/**
 * System row with hover-revealed verb buttons (`[DAG] [CP] [Flow] [Inspect]`). Mirrors the existing
 * "Reveal in File Map" pattern from `ComponentInspectorPanel` — body click writes the bus leaf
 * (ambient cluster highlight, power-user path); explicit verbs open + focus the corresponding panel
 * and surface the system there, so the navigator's outcomes are visible regardless of which cluster
 * panels the user currently has open.
 *
 * Rationale (#379 follow-up, 2026-05-26): the Systems row half of the navigator was reactive-only
 * (click radiated to invisible cluster panels), which made the panel look inert. The Query row half
 * already opened the Analyzer on click — verbs here restore symmetry without changing the
 * power-user body-click contract.
 */
function SystemNavRow({
  name,
  phaseName,
  selected,
  onSelect,
}: {
  name: string;
  phaseName?: string;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    // The container holds the `group` hover state + the row's selected styling, but is NOT the
    // roving target — Radix needs to manage tabindex on the body BUTTON so it stays the keyboard
    // entry point (and so the upstream "every button has explicit tabindex" invariant holds). Verb
    // buttons sit as absolutely-positioned siblings overlaying the row's right edge (where the
    // phase chip lives), so they don't compete with the body button for flex space and never push
    // the phase label out in narrow panels. The phase chip flips to `invisible` (keeps layout, drops
    // the ink) so the verbs paint into a clean rectangle and the underlying name doesn't reflow.
    <div
      className={
        'group relative flex h-[22px] w-full items-center gap-2 px-2 text-fs-lg ' +
        (selected ? 'bg-accent text-accent-foreground' : 'text-foreground hover:bg-muted/60')
      }
      data-testid={`systems-nav-row-${name}`}
    >
      {/* Body click — bus-write only (the existing power-user path). The RovingItem wraps THIS
          button so Radix's tabindex / arrow-key plumbing lands on the keyboard-relevant target. */}
      <RovingItem asChild>
        <button
          type="button"
          onClick={onSelect}
          aria-pressed={selected}
          className="flex min-w-0 flex-1 items-center gap-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
        >
          <span className="truncate">{name}</span>
          {phaseName && (
            <span className="ml-auto shrink-0 truncate text-fs-xs text-muted-foreground group-hover:invisible group-focus-within:invisible">{phaseName}</span>
          )}
        </button>
      </RovingItem>
      {/* Hover-revealed verb buttons — each opens/focuses the corresponding panel with the system
          pre-selected. Absolutely positioned over the row's right edge (where the phase chip lives)
          so the body button's flex layout never reflows when they appear — narrow panels keep the
          name fully visible underneath the verbs instead of seeing the phase label and name
          truncate. Hidden until row hover (or until the row body has keyboard focus, via
          `group-focus-within`) so the row stays uncluttered in the normal scrolling case. */}
      <div className="pointer-events-none absolute inset-y-0 right-2 hidden items-center gap-0.5 group-hover:flex group-focus-within:flex">
        <VerbButton
          label="Reveal in System DAG"
          testId={`systems-nav-reveal-dag-${name}`}
          onClick={() => revealSystemInDag(name)}
          icon={<Network className="h-3 w-3" />}
        />
        <VerbButton
          label="Reveal in Critical Path"
          testId={`systems-nav-reveal-cp-${name}`}
          onClick={() => revealSystemInCriticalPath(name)}
          icon={<GitBranch className="h-3 w-3" />}
        />
        <VerbButton
          label="Reveal in Data Flow"
          testId={`systems-nav-reveal-flow-${name}`}
          onClick={() => revealSystemInDataFlow(name)}
          icon={<Workflow className="h-3 w-3" />}
        />
        <VerbButton
          label="Reveal in Inspector"
          testId={`systems-nav-reveal-inspector-${name}`}
          onClick={() => revealSystemInInspector(name)}
          icon={<Info className="h-3 w-3" />}
        />
      </div>
    </div>
  );
}

function VerbButton({
  label,
  testId,
  onClick,
  icon,
}: {
  label: string;
  testId: string;
  onClick: () => void;
  icon: React.ReactNode;
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      data-testid={testId}
      // Verb buttons are mouse-only / explicit-focus only — Tab traversal goes through the row body
      // (the RovingItem). `tabIndex={-1}` keeps them focusable programmatically but out of the Tab
      // chain, which also satisfies the "every row is a roving item" test's "every button has
      // explicit tabindex" invariant.
      tabIndex={-1}
      onClick={(e) => {
        // The row body wraps a `<button>` too — without stopping propagation, clicking a verb would
        // ALSO bubble to the body's `onSelect`, doubling the bus writes (harmless but noisy). Stop
        // here so each click does exactly the navigation the user asked for.
        e.stopPropagation();
        onClick();
      }}
      // `pointer-events-auto` overrides the absolutely-positioned container's `pointer-events-none`
      // so the verb itself is clickable while the gaps between verbs pass clicks through to the
      // body button underneath (drag-selecting / clicking the row anywhere except on a verb works
      // even while the verbs are visible).
      className="pointer-events-auto flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
    >
      {icon}
    </button>
  );
}

function NavMessage({ tone, children }: { tone: 'muted' | 'error'; children: React.ReactNode }) {
  return (
    <div className="flex h-full items-center justify-center bg-background p-3">
      <p className={'text-center text-fs-lg ' + (tone === 'error' ? 'text-destructive' : 'text-muted-foreground')}>
        {children}
      </p>
    </div>
  );
}
