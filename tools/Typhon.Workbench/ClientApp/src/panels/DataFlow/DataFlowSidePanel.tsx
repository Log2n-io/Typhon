import type { Bar } from './barBuilding';
import type { Track } from './trackBuilding';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';

/**
 * Right-rail detail panel for the Data Flow Timeline. Two stacked sections:
 *
 * 1. <b>Selected bar</b> — when a bar is hovered or clicked, shows the (system, tick, archetype) tuple plus
 *    entity / chunk counts. Mirrors the System DAG side panel pattern: cheap, read-only, instantly readable.
 * 2. <b>Selected track</b> — when a track row is highlighted (e.g. via L4 selection elsewhere), summarizes
 *    the row identity (label + kind) and the systems that touched it across the visible tick range.
 *
 * Phase B v1 focuses on the bar-detail flow; track summarization is a placeholder until the cross-panel
 * `dataTrack` selection slot lands in Phase D.
 */
export interface DataFlowSidePanelProps {
  /** Hovered bar (preferred display) — shows live tooltip-style info. */
  hoveredBar: Bar | null;
  /** Selected (clicked) bar — sticky display when there's no hover. */
  selectedBar: Bar | null;
  /** Tracks list — used to resolve trackId → label for the selected/hovered bar. */
  tracks: readonly Track[];
  /** Topology systems — used to surface the system's declared access on the row's component. */
  systems: readonly SystemDefinitionDto[];
}

export default function DataFlowSidePanel({ hoveredBar, selectedBar, tracks, systems }: DataFlowSidePanelProps) {
  const focused = hoveredBar ?? selectedBar;

  if (!focused) {
    return (
      <div className="flex h-full flex-col gap-3 overflow-y-auto p-3 text-xs">
        <p className="text-muted-foreground">
          Hover a bar to see its system, tick, and entity details. Click to lock the selection — the System DAG
          will highlight the matching node.
        </p>
      </div>
    );
  }

  const track = tracks.find((t) => t.id === focused.trackId);
  const system = systems.find((s) => s.name === focused.systemName);

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-3 text-xs">
      <Section title="Bar">
        <Field label="System" value={focused.systemName} />
        <Field label="Tick" value={String(focused.tickNumber)} />
        <Field label="Archetype id" value={String(focused.archetypeId)} />
        <Field label="Entities" value={focused.entityCount.toLocaleString()} />
        <Field label="Chunks" value={String(focused.chunkCount)} />
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
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{title}</div>
      <div className="flex flex-col gap-0.5 rounded-md border border-border bg-card p-2">{children}</div>
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <span className="text-[11px] text-muted-foreground">{label}</span>
      <span className="font-mono text-[11px] text-foreground" title={value}>{value}</span>
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
    return <span className="text-[11px] text-muted-foreground">— No declared access on this component —</span>;
  }
  return (
    <div className="flex flex-wrap gap-1">
      {matched.map((a) => (
        <span key={a.label} className="rounded bg-muted px-1.5 py-0.5 text-[10px] font-medium text-foreground">
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
