import type { Bar } from './barBuilding';
import type { Track } from './trackBuilding';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { ArchetypeStorage } from '@/libs/schema/archetypeStorage';
import { revealArchetypeInFileMap } from '@/shell/commands/openDbMap';
import { formatFileSize } from '@/lib/formatters';

/**
 * Right-rail detail panel for the Data Flow Timeline. Three stacked sections:
 *
 * 1. <b>Selected bar</b> — when a bar is hovered or clicked, shows the (system, tick, archetype) tuple plus
 *    entity / chunk counts. Mirrors the System DAG side panel pattern: cheap, read-only, instantly readable.
 * 2. <b>Selected track</b> — when a track row is highlighted (e.g. via L4 selection elsewhere), summarizes
 *    the row identity (label + kind) and the systems that touched it across the visible tick range.
 * 3. <b>Storage today</b> — the physical lens (#619, design §4.2): where the touched archetype lives on disk in
 *    the open database, and the reveal that frames it in the File Map.
 *
 * Presentational only — every value arrives as a prop, resolved by `DataFlowPanel`. Keeping the fetches in the
 * parent is what lets this render in a bare test harness with no QueryClient.
 */
export interface DataFlowSidePanelProps {
  /** The bar under focus — hover wins over the sticky click selection. Resolved by the parent so the precedence lives in one place. */
  focusedBar: Bar | null;
  /** Tracks list — used to resolve trackId → label for the focused bar. */
  tracks: readonly Track[];
  /** Topology systems — used to surface the system's declared access on the row's component. */
  systems: readonly SystemDefinitionDto[];
  /**
   * The capture's name for the focused bar's archetype, or null when its id resolves to no archetype in this
   * capture. Never derived from the id itself — see {@link ArchetypeField}.
   */
  archetypeName: string | null;
  /** What the open database says about that archetype's storage right now. Unresolved in a bare trace session. */
  storage: ArchetypeStorage;
}

export default function DataFlowSidePanel({ focusedBar, tracks, systems, archetypeName, storage }: DataFlowSidePanelProps) {
  if (!focusedBar) {
    return (
      <div className="flex h-full flex-col gap-3 overflow-y-auto p-3 text-xs">
        <p className="text-muted-foreground">
          Hover a bar to see its system, tick, and entity details. Click to lock the selection — the System DAG
          will highlight the matching node.
        </p>
      </div>
    );
  }

  const track = tracks.find((t) => t.id === focusedBar.trackId);
  const system = systems.find((s) => s.name === focusedBar.systemName);

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-3 text-xs">
      <Section title="Bar">
        <Field label="System" value={focusedBar.systemName} />
        <Field label="Tick" value={String(focusedBar.tickNumber)} />
        <ArchetypeField name={archetypeName} archetypeId={focusedBar.archetypeId} />
        <Field label="Entities" value={focusedBar.entityCount.toLocaleString()} />
        <Field label="Chunks" value={String(focusedBar.chunkCount)} />
      </Section>

      {track && (
        <Section title="Track">
          <Field label="Label" value={track.label} />
          <Field label="Kind" value={track.kind} />
          {track.componentName && <Field label="Component" value={track.componentName} />}
          {track.familyName && <Field label="Family" value={track.familyName} />}
          {track.phaseName && <Field label="Phase" value={track.phaseName} />}
        </Section>
      )}

      {system && track?.componentName && (
        <Section title={`Access on ${track.componentName}`}>
          <AccessChips system={system} componentName={track.componentName} />
        </Section>
      )}

      {archetypeName && <StorageToday archetypeName={archetypeName} storage={storage} />}
    </div>
  );
}

/**
 * The archetype the bar's system touched.
 *
 * A touch summary carries a **per-process catalog id** (`ArchetypeRegistry.GetOrAssignCatalogId` — registration
 * order, never persisted). Rendering that number on its own, as this panel did before #619, is not merely unhelpful:
 * it invites exactly the join design §5.3 calls the landmine, since the *other* `ushort` in a trace — the low 16
 * bits of an EntityId — is the database's persisted routing id, and the two differ for most archetypes in any
 * database that gained archetypes over time.
 *
 * So the name leads. When the id resolves to nothing in this capture's archetype table, the panel says so rather
 * than falling back to a plausible label — the same absent-not-wrong rule §5.7 applies to every bridge here.
 */
function ArchetypeField({ name, archetypeId }: { name: string | null; archetypeId: number }) {
  if (!name) {
    return (
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-fs-sm text-muted-foreground">Archetype</span>
        <span className="text-fs-sm text-muted-foreground" data-testid="dataflow-archetype-unresolved">
          #{archetypeId} · not in this capture
        </span>
      </div>
    );
  }
  return (
    <div className="flex items-baseline justify-between gap-2">
      <span className="text-fs-sm text-muted-foreground">Archetype</span>
      <span className="font-mono text-fs-sm text-foreground" title={`${name} (catalog #${archetypeId})`} data-testid="dataflow-archetype-name">
        {name}
        <span className="ml-1 font-sans text-muted-foreground">#{archetypeId}</span>
      </span>
    </div>
  );
}

/**
 * Where the touched archetype lives on disk **now** — the physical half of design §4.2.
 *
 * ⚠️ The caveat is not decoration; §4.2 names it as a requirement. Everything above this section happened at a
 * recorded instant; everything in it is true of the file as it stands. Checkpointing, compaction and cluster
 * migration all move pages between the two, so an archetype being fragmented today cannot explain a spike in a
 * run that predates the fragmenting. Chronically fragmented *and* chronically slow is a real signal — but it is
 * correlational, and the panel says so rather than letting the adjacency imply cause.
 *
 * Rendered only when the archetype resolves in the open database; a bare trace session, or an archetype this
 * database has never heard of, shows nothing at all rather than a zeroed row.
 */
function StorageToday({ archetypeName, storage }: { archetypeName: string; storage: ArchetypeStorage }) {
  if (!storage.resolved) {
    return null;
  }
  return (
    <Section title="Storage today">
      <Field label="Segments" value={storage.owned.map((s) => s.kind).join(' · ')} />
      <Field
        label="Pages"
        value={`${storage.totalPages.toLocaleString()}${storage.totalBytes > 0 ? ` · ${formatFileSize(storage.totalBytes)}` : ''}`}
      />
      <Field label="Chunk fill" value={`${storage.chunkFillPct.toFixed(1)}%`} />
      {storage.reclaimableBytes > 0 && <Field label="Reclaimable" value={formatFileSize(storage.reclaimableBytes)} />}
      {storage.entityCount !== null && <Field label="Entities now" value={storage.entityCount.toLocaleString()} />}
      {storage.shared.length > 0 && (
        <p className="mt-1 text-fs-xs text-muted-foreground" data-testid="dataflow-storage-shared">
          Plus {storage.shared.length} component {storage.shared.length === 1 ? 'table' : 'tables'} shared with other
          archetypes — not counted above.
        </p>
      )}
      <p className="mt-1 text-fs-xs text-muted-foreground" data-testid="dataflow-storage-caveat">
        This is the layout <em>now</em>, not at the recorded tick — checkpointing, compaction and cluster migration
        move pages. It cannot explain a spike in this run, only describe the file as it stands.
      </p>
      <button
        type="button"
        onClick={() => revealArchetypeInFileMap(archetypeName)}
        data-testid="dataflow-reveal-file-map"
        title="Frame this archetype's own segments in the File Map"
        className="mt-1 self-start rounded border border-border px-2 py-0.5 text-fs-xs text-foreground hover:bg-accent"
      >
        Reveal in File Map →
      </button>
    </Section>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</div>
      <div className="flex flex-col gap-0.5 rounded-md border border-border bg-card p-2">{children}</div>
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <span className="text-fs-sm text-muted-foreground">{label}</span>
      <span className="font-mono text-fs-sm text-foreground" title={value}>{value}</span>
    </div>
  );
}

/**
 * Render the access chips for a system + component, mirroring the System DAG side panel. Each kind displayed only
 * if present.
 */
function AccessChips({ system, componentName }: { system: SystemDefinitionDto; componentName: string }) {
  const access: { label: string; matches: boolean }[] = [
    { label: 'Writes',         matches: includes(system.writes, componentName) },
    { label: 'Side-writes',    matches: includes(system.sideWrites, componentName) },
    { label: 'ReadsFresh',     matches: includes(system.readsFresh, componentName) },
    { label: 'ReadsSnapshot',  matches: includes(system.readsSnapshot, componentName) },
    { label: 'Reads',          matches: includes(system.reads, componentName) },
    { label: 'AdditionalReads', matches: includes(system.additionalReads, componentName) },
  ];
  const matched = access.filter((a) => a.matches);
  if (matched.length === 0) {
    return <span className="text-fs-sm text-muted-foreground">— No declared access on this component —</span>;
  }
  return (
    <div className="flex flex-wrap gap-1">
      {matched.map((a) => (
        <span key={a.label} className="rounded bg-muted px-1.5 py-0.5 text-fs-xs font-medium text-foreground">
          {a.label}
        </span>
      ))}
    </div>
  );
}

function includes(arr: readonly string[] | null | undefined, target: string): boolean {
  if (!arr) return false;
  for (const v of arr) if (v === target) return true;
  return false;
}
