import { useState } from 'react';
import { ChevronDown, ChevronRight, ListTree, Workflow } from 'lucide-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useQueryDefinitions } from '@/panels/QueryCatalog/useQueryDefinitions';
import { useSelectionStore } from '@/stores/useSelectionStore';

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

  return (
    <div className="flex h-full w-full flex-col overflow-auto bg-background">
      <NavSection
        icon={<Workflow className="h-3.5 w-3.5" />}
        title="Systems"
        count={systems.length}
      >
        {systems.map((sys) => {
          const name = sys.name ?? `System[${sys.index}]`;
          return (
            <NavRow
              key={String(sys.index)}
              label={name}
              detail={sys.phaseName ?? undefined}
              selected={selectedSystem === name}
              onClick={() => {
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
                onClick={() => select('query', { kind: q.instanceId.kind, localId: q.instanceId.localId })}
              />
            );
          })
        )}
      </NavSection>
    </div>
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
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left text-[11px] font-medium uppercase tracking-wide text-muted-foreground hover:text-foreground"
      >
        {open ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
        {icon}
        <span>{title}</span>
        <span className="ml-auto tabular-nums">{count}</span>
      </button>
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
    <button
      type="button"
      onClick={onClick}
      aria-pressed={selected}
      className={
        'flex h-[22px] w-full items-center gap-2 px-2 text-left text-density-sm ' +
        (selected ? 'bg-accent text-accent-foreground' : 'text-foreground hover:bg-muted/60')
      }
    >
      <span className="truncate">{label}</span>
      {detail && <span className="ml-auto shrink-0 truncate text-[10px] text-muted-foreground">{detail}</span>}
    </button>
  );
}

function NavMessage({ tone, children }: { tone: 'muted' | 'error'; children: React.ReactNode }) {
  return (
    <div className="flex h-full items-center justify-center bg-background p-3">
      <p className={'text-center text-density-sm ' + (tone === 'error' ? 'text-destructive' : 'text-muted-foreground')}>
        {children}
      </p>
    </div>
  );
}
