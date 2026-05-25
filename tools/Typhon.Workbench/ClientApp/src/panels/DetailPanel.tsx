import { useMemo, useState, type ReactNode } from 'react';
import { Binary, Boxes, ChevronDown, ChevronRight, FolderOpen, HardDrive, Layers, ListTree, Pin, PinOff, Workflow } from 'lucide-react';
import { StatusBadge } from '@/components/ui/status-badge';
import { simplifyTypeName } from '@/libs/simplifyTypeName';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import type { DbMapSelection } from '@/libs/dbmap/dbMapSelection';
import { useDbMapOverlayStore } from '@/stores/useDbMapOverlayStore';
import { useDbMap } from '@/hooks/dbmap/useDbMap';
import { useDbMapChunk, useDbMapPage } from '@/hooks/dbmap/useDbMapDetail';
import { useDbMapSegmentSummary } from '@/hooks/dbmap/useDbMapSegment';
import type { DbChunkContent, DbContentCell, DbPageDetail, StorageSegmentSummaryDto } from '@/libs/dbmap/types';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { openComponentInspector, openArchetypeInspector, openDataBrowser } from '@/shell/commands/openSchemaBrowser';
import { useArchetypesForComponent } from '@/hooks/schema/useArchetypesForComponent';
import { pickPrimaryArchetype } from '@/hooks/dataBrowser/pickArchetype';
import { componentNameFromResource } from '@/hooks/dataBrowser/resourceComponent';
import { openComponentInSchema, openDbMapForComponent, revealComponentInResourceTree, revealSystemInDag } from '@/shell/commands/openDbMap';
import { revealQueryInAnalyzer } from '@/shell/commands/profilerCommands';
import { isViewActive } from '@/shell/viewRegistry';
import type { ComponentSchema, Field } from '@/hooks/schema/types';
import ProfilerDetail from '@/panels/profiler/ProfilerDetail';
import { useTopology } from '@/hooks/data/useTopology';
import { toNodeData, resolveNoAccessReason } from '@/panels/SystemDag/dagModel';
import SystemAccessSummary from '@/panels/schemaCommon/SystemAccessSummary';
import { useEntityDetail } from '@/hooks/dataBrowser/useEntityDetail';
import EntityCardsDetail from '@/panels/DataBrowser/EntityCardsDetail';
import { useSelectionStore, type SelectionLeaf } from '@/stores/useSelectionStore';
import { resolveChain, type SelectionRef } from '@/stores/selectionChain';
import type { SelectedResource } from '@/stores/useSelectedResourceStore';
import type { ProfilerSelection } from '@/libs/profiler/model/traceModel';

/**
 * The Inspector (right rail, zone E) — the single "what's selected" surface for the whole app (IA §2.4).
 *
 * Stage 1 (#373): the Inspector now follows the **unified selection bus** ({@link useSelectionStore} `leaf`)
 * instead of arbitrating five siloed stores. The bus leaf is the most-recently selected *primary* object
 * across every object type (the silos write-through to it); projections never steal the leaf. The leaf is
 * rendered **in full** via a per-type card; its containment ancestors ({@link resolveChain}) stack above it
 * as collapsible **summaries** (IA §2.5). A **pin** freezes the chain so the user can click around.
 *
 * Per-leaf "Open in →" verbs appear only once their deep view exists (PC-6 forbids dead verbs). Stage 2:
 * component / archetype leaves carry a live "Open in … Inspector" action; system / query stay summary-only
 * until Stages 3-4.
 */
export default function DetailPanel() {
  const leaf = useSelectionStore((s) => s.leaf);
  const profilerMetadata = useProfilerSessionStore((s) => s.metadata);
  const sessionKind = useSessionStore((s) => s.kind);
  const isProfilerSession = sessionKind === 'attach' || sessionKind === 'trace';

  // Pin freezes the rail on the current object so clicking elsewhere doesn't re-target it (ephemeral).
  const [pinned, setPinned] = useState<SelectionLeaf | null>(null);
  const activeLeaf = pinned ?? leaf;

  // Ancestors derive from the leaf's own ref (+ scalar-context fallback); recompute when the leaf changes.
  const ancestors = useMemo(() => resolveChain(activeLeaf, useSelectionStore.getState()), [activeLeaf]);

  if (activeLeaf === null) {
    // Before any pick, a profiler session still shows range-stats over the current viewport.
    if (isProfilerSession && profilerMetadata !== null) {
      return <ProfilerDetail selection={null} />;
    }
    return (
      <div className="flex h-full items-center justify-center bg-background p-3">
        <p className="text-center text-fs-lg text-muted-foreground">
          Select anything — a resource, component, entity, system, or profiler element — to inspect it.
        </p>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col bg-background">
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-3 py-1.5">
        <span className="text-fs-xs font-medium uppercase tracking-wide text-muted-foreground">Inspector</span>
        <button
          type="button"
          onClick={() => setPinned(pinned ? null : leaf)}
          aria-pressed={pinned !== null}
          title={pinned ? 'Unpin — follow selection' : 'Pin this object'}
          className="ml-auto rounded p-1 text-muted-foreground hover:bg-muted/60 hover:text-foreground"
        >
          {pinned ? <Pin className="h-3.5 w-3.5" /> : <PinOff className="h-3.5 w-3.5" />}
        </button>
      </div>
      {/* Finest-grained first: the leaf (focused, most-specific object) on top, then its containment ancestors
          below in finest→coarsest order (chunk → page → segment). `resolveChain` returns root→parent, so reverse
          it. Leaf + ancestors share ONE scroll list. With a chain, the leaf is wrapped in an auto-height div so
          its `h-full` collapses to content height — no whitespace gap before the first ancestor, one scrollbar.
          With no chain, the lone leaf fills the rail as before (profiler / entity cards expect that). */}
      <div className="min-h-0 flex-1 overflow-auto">
        {ancestors.length === 0 ? (
          <div className="h-full">
            <LeafCard leaf={activeLeaf} />
          </div>
        ) : (
          <>
            <div>
              <LeafCard leaf={activeLeaf} />
            </div>
            <div className="border-t border-border">
              {[...ancestors].reverse().map((node) => (
                <AncestorSection key={`${node.type}:${String(node.ref)}`} node={node} activeLeaf={activeLeaf} />
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * Maps the bus leaf to its **full** card — reusing the existing detail bodies. Each card that fetches data
 * is its own component so its hooks stay unconditional. Object types with a (currently-gated) deep panel
 * render a summary placeholder until that panel returns.
 */
function LeafCard({ leaf }: { leaf: SelectionLeaf }): React.JSX.Element {
  switch (leaf.type) {
    case 'resource':
      return <ResourceDetail resource={leaf.ref as SelectedResource} />;
    case 'field':
      return <FieldLeafCard ref0={leaf.ref as { component: string | null; field: string }} />;
    case 'entity':
      return <EntityLeafCard ref0={leaf.ref as { archetypeId: string | null; entityId: string }} />;
    case 'page':
    case 'chunk':
    case 'cell':
    case 'segment':
      return <DbMapDetail selection={leaf.ref as DbMapSelection} />;
    case 'span':
    case 'tick':
      return <ProfilerDetail selection={leaf.ref as ProfilerSelection} />;
    case 'component':
      return <ComponentLeafCard typeName={String(leaf.ref)} />;
    case 'archetype':
      return <ArchetypeLeafCard archetypeId={String(leaf.ref)} />;
    case 'system':
      return <SystemLeafCard name={String(leaf.ref)} />;
    case 'query':
      return <QueryLeafCard ref0={leaf.ref} />;
    default:
      return <ObjectSummaryCard icon={<Binary className="h-4 w-4 text-muted-foreground" />} kind={leaf.type} title={String(leaf.ref)} />;
  }
}

/** Field leaf → fetch its component schema, find the field, render the full FieldDetail. */
function FieldLeafCard({ ref0 }: { ref0: { component: string | null; field: string } }): React.JSX.Element {
  const { schema } = useComponentSchema(ref0.component);
  const field = schema?.fields.find((f) => f.name === ref0.field);
  if (!schema || !field) {
    return <CardLoading label="field" />;
  }
  return <FieldDetail field={field} schema={schema} />;
}

/** Entity leaf → fetch the entity detail, render the component-card stack. */
function EntityLeafCard({ ref0 }: { ref0: { archetypeId: string | null; entityId: string } }): React.JSX.Element {
  const { detail } = useEntityDetail(ref0.archetypeId, ref0.entityId);
  if (!detail) {
    return <CardLoading label="entity" />;
  }
  return <EntityCardsDetail detail={detail} />;
}

/** Best-effort label for a query leaf ref (id/string today; richer when the Query Analyzer returns). */
function queryLabel(ref: unknown): string {
  if (typeof ref === 'string' || typeof ref === 'number') {
    return String(ref);
  }
  if (ref !== null && typeof ref === 'object') {
    const r = ref as Record<string, unknown>;
    if ('localId' in r) {
      return `${String(r.kind ?? 'Query')}#${String(r.localId)}`;
    }
  }
  return 'Query';
}

/** One "Open in →" verb in a leaf summary card. Only verbs whose destination view exists are added (PC-6). */
interface LeafAction {
  label: string;
  onClick: () => void;
  testId?: string;
}

/** Shared shell for a leaf summary card: icon + title + kind, a body, and its real "Open in →" verbs. */
function LeafSummaryCard({
  icon,
  kind,
  title,
  fullTitle,
  actions,
  children,
}: {
  icon: ReactNode;
  kind: string;
  title: string;
  fullTitle?: string;
  actions: LeafAction[];
  children: ReactNode;
}): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          {icon}
          <h3 className="truncate text-fs-lg font-semibold text-foreground" title={fullTitle ?? title}>{title}</h3>
          <span className="ml-auto text-fs-sm text-muted-foreground">{kind}</span>
        </div>
        {children}
        <div className="mt-3 flex flex-col gap-1">
          {actions.map((a) => (
            <button
              key={a.label}
              type="button"
              onClick={a.onClick}
              data-testid={a.testId}
              className="w-full rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
            >
              {a.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

/** Component leaf → a summary + "Open in Component Inspector" and the type-first "Open in Data Browser" (GAP-03). */
function ComponentLeafCard({ typeName }: { typeName: string }): React.JSX.Element {
  const { list } = useComponentList();
  const c = list.find((x) => x.typeName === typeName || x.fullName === typeName);
  // M:N type-first (AC2.7): auto-pick the max-entity archetype; add the verb only when there's data to browse.
  const { archetypes } = useArchetypesForComponent(c?.typeName ?? typeName);
  const primary = pickPrimaryArchetype(archetypes);
  const actions: LeafAction[] = [{ label: 'Open in Component Inspector', onClick: openComponentInspector }];
  if (primary) {
    actions.push({ label: 'Open in Data Browser', onClick: () => openDataBrowser(primary.archetypeId), testId: 'leaf-open-data-browser' });
  }
  actions.push({ label: 'Reveal in File Map', onClick: () => openDbMapForComponent(c?.typeName ?? typeName), testId: 'leaf-reveal-file-map' });
  return (
    <LeafSummaryCard
      icon={<Boxes className="h-4 w-4 text-muted-foreground" />}
      kind="Component"
      title={c?.typeName ?? typeName}
      fullTitle={c?.fullName ?? typeName}
      actions={actions}
    >
      {c ? (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-fs-sm">
          <dt className="text-muted-foreground">Size</dt>
          <dd className="tabular-nums">{c.storageSize}B</dd>
          <dt className="text-muted-foreground">Fields</dt>
          <dd className="tabular-nums">{c.fieldCount}</dd>
          <dt className="text-muted-foreground">Indexes</dt>
          <dd className="tabular-nums">{c.indexCount}</dd>
          <dt className="text-muted-foreground">Used in</dt>
          <dd className="tabular-nums">{c.archetypeCount ?? '—'} archetypes</dd>
        </dl>
      ) : (
        <p className="text-fs-sm text-muted-foreground">Loading…</p>
      )}
    </LeafSummaryCard>
  );
}

/** Archetype leaf → a summary + "Open in Archetype Inspector" and "Open in Data Browser" (when it has entities). */
function ArchetypeLeafCard({ archetypeId }: { archetypeId: string }): React.JSX.Element {
  const { list } = useArchetypeList();
  const a = list.find((x) => x.archetypeId === archetypeId);
  const actions: LeafAction[] = [{ label: 'Open in Archetype Inspector', onClick: openArchetypeInspector }];
  if (a && a.entityCount > 0) {
    actions.push({ label: 'Open in Data Browser', onClick: () => openDataBrowser(archetypeId), testId: 'leaf-open-data-browser' });
  }
  return (
    <LeafSummaryCard
      icon={<Layers className="h-4 w-4 text-muted-foreground" />}
      kind="Archetype"
      title={`#${archetypeId}`}
      actions={actions}
    >
      {a ? (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-fs-sm">
          <dt className="text-muted-foreground">Components</dt>
          <dd className="tabular-nums">{a.componentTypes.length}</dd>
          <dt className="text-muted-foreground">Entities</dt>
          <dd className="tabular-nums">{a.entityCount.toLocaleString()}</dd>
          <dt className="text-muted-foreground">Storage</dt>
          <dd>{a.storageMode}</dd>
        </dl>
      ) : (
        <p className="text-fs-sm text-muted-foreground">Loading…</p>
      )}
    </LeafSummaryCard>
  );
}

/**
 * System card (inspector.md §4.2) — a summary: phase + RFC-07 declared access. Resolves the system from the
 * (cached) topology and reuses {@link toNodeData} so the access arrays are byte-identical to the System DAG's,
 * and {@link SystemAccessSummary} so there is one declared-access rendering (PC-3). No "Reveal in System DAG"
 * verb yet — that view is gated until Phase 3D (PC-6: no dead affordance).
 */
function SystemDetailBody({ name }: { name: string }): React.JSX.Element {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology } = useTopology(sessionId);
  const system = topology?.systems?.find((s) => s.name === name) ?? null;
  if (!system) {
    return <AncestorBody>{topology ? 'Not in this trace topology.' : 'Loading…'}</AncestorBody>;
  }
  const node = toNodeData(system);
  // topology is non-null here (system was just resolved from it); the guard keeps TS happy without `!`.
  const noAccessReason = topology ? resolveNoAccessReason(topology, node.dagId) : undefined;
  return (
    <div className="space-y-2">
      <div className="text-fs-sm">
        <span className="text-muted-foreground">Phase: </span>
        <span className="font-mono text-foreground">{node.phaseName || '(unphased)'}</span>
      </div>
      <SystemAccessSummary {...node} noAccessReason={noAccessReason} />
    </div>
  );
}

/**
 * Full leaf card for a `system` selection — header + {@link SystemDetailBody} (phase + declared access) + the
 * "Reveal in System DAG" handoff. The DAG view is active as of Phase 3D, so this is a live verb (PC-6: no dead
 * affordance); it highlights the system on the bus and centres its node (engine systems auto-reveal).
 */
function SystemLeafCard({ name }: { name: string }): React.JSX.Element {
  return (
    <LeafSummaryCard
      icon={<Workflow className="h-4 w-4 text-muted-foreground" />}
      kind="System"
      title={name}
      actions={[{ label: 'Reveal in System DAG', onClick: () => revealSystemInDag(name), testId: 'leaf-reveal-system-dag' }]}
    >
      <SystemDetailBody name={name} />
    </LeafSummaryCard>
  );
}

/**
 * Query leaf → a summary card with the "Open in Query Analyzer" verb (the deep view now exists, so this
 * is no longer the dead "returns later" placeholder — #376 Phase 4, PC-6). The verb is gated on the view
 * being active so it can't go dead if the Analyzer is ever gated off.
 */
function QueryLeafCard({ ref0 }: { ref0: unknown }): React.JSX.Element {
  const r = ref0 !== null && typeof ref0 === 'object' ? (ref0 as { kind?: unknown; localId?: unknown }) : {};
  const kind = Number(r.kind);
  const localId = Number(r.localId);
  const canOpen = isViewActive('QueryAnalyzer') && Number.isFinite(kind) && Number.isFinite(localId);
  return (
    <LeafSummaryCard
      icon={<ListTree className="h-4 w-4 text-muted-foreground" />}
      kind="Query"
      title={queryLabel(ref0)}
      actions={canOpen
        ? [{ label: 'Open in Query Analyzer', onClick: () => revealQueryInAnalyzer(kind, localId), testId: 'leaf-open-query-analyzer' }]
        : []}
    >
      <p className="text-fs-sm text-muted-foreground">Its catalog entry, plan, and per-execution phase breakdown.</p>
    </LeafSummaryCard>
  );
}

/**
 * The "Reveal in System DAG" verb for the System *ancestor* section (a chunk/span's owning system). The leaf card
 * renders the same verb through {@link LeafSummaryCard}'s action row; this mirrors it for the ancestor so the
 * jump-to-DAG handoff works from a chunk/span selection too. Same styling + handler as the leaf action; a distinct
 * test id because the two never co-render (leaf XOR ancestor).
 */
function RevealInSystemDagButton({ name }: { name: string }): React.JSX.Element {
  return (
    <button
      type="button"
      onClick={() => revealSystemInDag(name)}
      data-testid="ancestor-reveal-system-dag"
      className="mt-2 w-full rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
    >
      Reveal in System DAG
    </button>
  );
}

/**
 * A summary card for an object whose deep surface is still a gated workspace panel — Query (Stage 4). States
 * what's selected + that the deep view returns later (PC-6: an explained state, no dead verb).
 */
function ObjectSummaryCard({ icon, kind, title }: { icon: ReactNode; kind: string; title: string }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          {icon}
          <h3 className="truncate text-fs-lg font-semibold text-foreground" title={title}>{title}</h3>
          <span className="ml-auto text-fs-sm text-muted-foreground">{kind}</span>
        </div>
        <p className="text-fs-sm text-muted-foreground">
          The {kind} deep view returns in a later stage of the Workbench redesign.
        </p>
      </div>
    </div>
  );
}

// Ancestor types whose rail card IS the full detail (no dedicated deep panel — inspector.md §4.2), so they
// render the SAME card as when selected directly. Types WITH a deep panel (component/archetype) render a
// compact summary instead, deferring depth to that panel. `chunk` is here so a cell leaf's chunk parent shows
// its full card (it has no deep panel either) rather than an empty summary section.
const FULL_CARD_ANCESTORS = new Set<string>(['page', 'segment', 'chunk']);

/**
 * A collapsible containment-ancestor section in the Inspector context stack (IA §2.5 / inspector.md §3, §4.2),
 * expanded by default. A page/segment has **no deep panel**, so the rail IS its full detail — it renders the
 * **same card as the leaf** (identical UI whether it's the focus or a parent in the chain). Deep-panel types
 * (component/archetype) render a compact summary line. The header carries the ref when collapsed; the full
 * card's own title carries it when expanded.
 */
function AncestorSection({ node, activeLeaf }: { node: SelectionRef; activeLeaf: SelectionLeaf }): React.JSX.Element {
  const [open, setOpen] = useState(true);
  const full = FULL_CARD_ANCESTORS.has(node.type);
  return (
    <div data-testid={`inspector-ancestor-${node.type}`}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-1.5 px-3 py-1 text-left text-fs-sm text-muted-foreground hover:text-foreground"
      >
        {open ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
        <span className="text-fs-xs uppercase tracking-wide">{node.type}</span>
        {(!full || !open) && <span className="truncate font-mono text-foreground">{String(node.ref)}</span>}
      </button>
      {open &&
        (full ? (
          <AncestorFullCard node={node} activeLeaf={activeLeaf} />
        ) : (
          <div className="px-3 pb-1 pl-[18px]">
            <AncestorSummary node={node} />
          </div>
        ))}
    </div>
  );
}

/** Renders the SAME full card the leaf would for a no-deep-panel ancestor (page/segment/chunk), via a synthesized leaf. */
function AncestorFullCard({ node, activeLeaf }: { node: SelectionRef; activeLeaf: SelectionLeaf }): React.JSX.Element | null {
  const leaf = ancestorToLeaf(node, activeLeaf);
  return leaf ? <LeafCard leaf={leaf} /> : null;
}

/**
 * Synthesize the bus leaf for a full-card ancestor so it reuses {@link LeafCard} verbatim — the "same UI"
 * guarantee. `page`/`segment` need only their own id; a `chunk` card additionally needs its segment + page,
 * which the node ref (just the chunk id) lacks — so source them from the active leaf. A chunk ancestor only
 * arises under a `cell` leaf (see {@link resolveChain}), and that ref carries every storage coordinate.
 */
function ancestorToLeaf(node: SelectionRef, activeLeaf: SelectionLeaf): SelectionLeaf | null {
  switch (node.type) {
    case 'page':
      return { type: 'page', ref: { kind: 'page', pageIndex: Number(node.ref) }, touchedAt: 0 };
    case 'segment':
      return { type: 'segment', ref: { kind: 'segment', segmentId: Number(node.ref) }, touchedAt: 0 };
    case 'chunk': {
      const r = activeLeaf.ref as { pageIndex?: number; segmentId?: number; chunkId?: number } | null;
      if (r && r.pageIndex != null && r.segmentId != null && r.chunkId != null) {
        return { type: 'chunk', ref: { kind: 'chunk', pageIndex: r.pageIndex, segmentId: r.segmentId, chunkId: r.chunkId }, touchedAt: 0 };
      }
      return null;
    }
    default:
      return null;
  }
}

/** Compact summary body for a deep-panel ancestor (component/archetype) — the rail defers to the deep view (§4.2). */
function AncestorSummary({ node }: { node: SelectionRef }): React.JSX.Element | null {
  switch (node.type) {
    case 'component':
      return <ComponentAncestorSummary typeName={String(node.ref)} />;
    case 'archetype':
      return <ArchetypeAncestorSummary archetypeId={String(node.ref)} />;
    case 'system':
      // System ⊃ Span/Chunk ancestor: the same phase + declared-access body the leaf shows (3C), PLUS the live
      // "Reveal in System DAG" verb — so the jump-to-DAG handoff is reachable straight from a chunk/span selection,
      // not only when the System is the primary leaf. The button is a sibling of the (topology-gated) body so it
      // shows even while topology is still loading (reveal works by name regardless).
      return (
        <>
          <SystemDetailBody name={String(node.ref)} />
          <RevealInSystemDagButton name={String(node.ref)} />
        </>
      );
    default:
      return null;
  }
}

function AncestorBody({ children }: { children: ReactNode }): React.JSX.Element {
  return <p className="text-fs-sm text-muted-foreground" data-testid="inspector-ancestor-body">{children}</p>;
}

function ComponentAncestorSummary({ typeName }: { typeName: string }): React.JSX.Element {
  const { list } = useComponentList();
  const c = list.find((x) => x.typeName === typeName);
  if (!c) {
    return <AncestorBody>{typeName}</AncestorBody>;
  }
  return <AncestorBody>{`${c.storageSize} B · ${c.fieldCount} fields · ${c.indexCount} idx`}</AncestorBody>;
}

function ArchetypeAncestorSummary({ archetypeId }: { archetypeId: string }): React.JSX.Element {
  const { list } = useArchetypeList();
  const a = list.find((x) => x.archetypeId === archetypeId);
  if (!a) {
    return <AncestorBody>{`#${archetypeId}`}</AncestorBody>;
  }
  return <AncestorBody>{`${a.componentTypes.length} comp · ${a.entityCount.toLocaleString()} ent · ${a.occupancyPct.toFixed(0)}%`}</AncestorBody>;
}

/** Card-body loading placeholder (PC-2 loading state). */
function CardLoading({ label }: { label: string }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3">
        <p className="text-fs-sm text-muted-foreground">Loading {label}…</p>
      </div>
    </div>
  );
}

// Database File Map selection (Module 15, §6.5) — a page, a chunk, or a content cell, fully decoded by the
// server-side detail tier (A2). The hooks below are called unconditionally; the irrelevant ones stay disabled.
function DbMapDetail({ selection }: { selection: DbMapSelection }) {
  const sessionId = useSessionStore((s) => s.sessionId);
  // The database name belongs to the file-map data, not the selection — read it from the (cached) map query
  // rather than carrying it on the bus leaf. Empty until the map data is available; the card just omits it.
  const { data: dbMap } = useDbMap(sessionId);
  const databaseName = dbMap?.databaseName ?? '';
  const pageIndex = selection.kind === 'page' ? selection.pageIndex : null;
  const isChunkLike = selection.kind === 'chunk' || selection.kind === 'cell';
  const segId = isChunkLike ? selection.segmentId : null;
  const chunkId = isChunkLike ? selection.chunkId : null;
  const summarySegId = selection.kind === 'segment' ? selection.segmentId : null;
  const { data: page } = useDbMapPage(sessionId, pageIndex);
  const { data: chunk } = useDbMapChunk(sessionId, segId, chunkId);
  const { data: summary } = useDbMapSegmentSummary(sessionId, summarySegId);

  if (selection.kind === 'page') {
    return <DbMapPageDetail databaseName={databaseName} pageIndex={selection.pageIndex} page={page ?? null} />;
  }
  if (selection.kind === 'segment') {
    return <DbMapSegmentDetail databaseName={databaseName} segmentId={selection.segmentId} typeName={selection.typeName} summary={summary ?? null} />;
  }
  if (selection.kind === 'chunk') {
    return <DbMapChunkDetail databaseName={databaseName} pageIndex={selection.pageIndex} chunk={chunk ?? null} />;
  }
  const cell = chunk?.cells.find((c) => c.offset === selection.cellOffset) ?? null;
  return <DbMapCellDetail databaseName={databaseName} chunk={chunk ?? null} cellOffset={selection.cellOffset} cell={cell} />;
}

function DbMapDetailCard({
  icon,
  title,
  badge,
  databaseName,
  children,
}: {
  icon: ReactNode;
  title: string;
  badge?: string;
  databaseName: string;
  children: ReactNode;
}) {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          {icon}
          <h3 className="text-fs-lg font-semibold text-foreground">{title}</h3>
          {badge && <StatusBadge tone="neutral">{badge}</StatusBadge>}
          <span className="ml-auto truncate font-mono text-fs-sm text-muted-foreground">{databaseName}</span>
        </div>
        {children}
      </div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="font-mono tabular-nums text-foreground">{value}</dd>
    </>
  );
}

function DbMapPageDetail({
  databaseName,
  pageIndex,
  page,
}: {
  databaseName: string;
  pageIndex: number;
  page: DbPageDetail | null;
}) {
  return (
    <DbMapDetailCard
      icon={<HardDrive className="h-4 w-4 text-muted-foreground" />}
      title={`Page ${pageIndex}`}
      badge={page?.pageType}
      databaseName={databaseName}
    >
      {!page ? (
        <p className="text-fs-sm text-muted-foreground">Decoding page…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
            <Row label="Byte offset" value={`0x${page.byteOffset.toString(16).toUpperCase()}`} />
            <Row label="Change revision" value={page.changeRevision.toLocaleString()} />
            <Row label="Format revision" value={String(page.formatRevision)} />
            <Row label="Modification counter" value={String(page.modificationCounter)} />
            <Row
              label="CRC"
              value={`${page.crcStatus} (0x${page.liveChecksum.toString(16).toUpperCase()})`}
            />
            <Row label="Residency" value={`${page.residency} · DC ${page.dirtyCounter}`} />
            {page.chunkTotal > 0 && (
              <Row
                label="Chunks"
                value={`${page.chunkUsed} / ${page.chunkTotal} (${Math.round(page.fillRatio * 100)}% full)`}
              />
            )}
          </dl>
          {page.directoryEntries.length > 0 && (
            <CellList title={`Page directory · ${page.directoryEntries.length} entries`} cells={page.directoryEntries} />
          )}
        </>
      )}
    </DbMapDetailCard>
  );
}

function DbMapSegmentDetail({
  databaseName,
  segmentId,
  typeName,
  summary,
}: {
  databaseName: string;
  segmentId: number;
  typeName?: string;
  summary: StorageSegmentSummaryDto | null;
}) {
  const chunkBased = (summary?.chunkCapacity ?? 0) > 0;
  const chunkFillPct =
    summary && chunkBased ? Math.round((summary.allocatedChunkCount / summary.chunkCapacity) * 100) : null;
  const isCluster = (summary?.clusterSize ?? 0) > 0;
  const slotDenom = summary ? summary.activeClusterCount * summary.clusterSize : 0;
  const slotOccPct = summary && isCluster && slotDenom > 0 ? Math.round((Number(summary.entityCount) / slotDenom) * 100) : null;
  const map = summary?.entityMap ?? null;

  return (
    <DbMapDetailCard
      icon={<Layers className="h-4 w-4 text-muted-foreground" />}
      title={`Segment #${segmentId}`}
      badge={summary?.kind}
      databaseName={databaseName}
    >
      {!summary ? (
        <p className="text-fs-sm text-muted-foreground">Harvesting segment summary…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
            <Row label="Root page" value={`#${summary.rootPageIndex}`} />
            <Row label="Pages" value={summary.pageCount.toLocaleString()} />
            {summary.stride > 0 && <Row label="Stride" value={`${summary.stride} B`} />}
            {chunkBased && (
              <>
                <Row
                  label="Chunks"
                  value={`${summary.allocatedChunkCount.toLocaleString()} / ${summary.chunkCapacity.toLocaleString()} (${chunkFillPct}% full)`}
                />
                <Row label="Free chunks" value={summary.freeChunkCount.toLocaleString()} />
              </>
            )}
          </dl>

          {isCluster && (
            <div className="mt-3 border-t border-border pt-2">
              <p className="mb-1 text-fs-xs uppercase tracking-wide text-muted-foreground">Cluster fill</p>
              <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
                <Row label="Entities" value={Number(summary.entityCount).toLocaleString()} />
                <Row label="Active clusters" value={summary.activeClusterCount.toLocaleString()} />
                <Row label="Slots / cluster" value={String(summary.clusterSize)} />
                {slotOccPct != null && <Row label="Slot occupancy" value={`${slotOccPct}%`} />}
              </dl>
            </div>
          )}

          {map && (
            <div className="mt-3 border-t border-border pt-2">
              <p className="mb-1 text-fs-xs uppercase tracking-wide text-muted-foreground">Entity-map (linear hash)</p>
              <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
                <Row label="Entries" value={Number(map.entryCount).toLocaleString()} />
                <Row label="Buckets" value={map.bucketCount.toLocaleString()} />
                <Row label="Load factor" value={map.loadFactor.toFixed(2)} />
                <Row label="Overflow buckets" value={map.overflowBucketCount.toLocaleString()} />
                <Row label="Max chain" value={String(map.maxChainLength)} />
              </dl>
              <BucketFillBar map={map} />
            </div>
          )}
        </>
      )}
      {typeName && <SegmentActions typeName={typeName} />}
    </DbMapDetailCard>
  );
}

/**
 * Segment → handoff verbs (AC2.14). A component-table segment pivots to the same destinations a component does
 * — Open in Component Inspector (Schema), Open in Data Browser (type-first archetype pick), Reveal in File Map,
 * Reveal in Resource Tree. Mirrors {@link ResourceActions}; renders only for a component segment (a `typeName`),
 * so a non-component segment never shows a dead verb (PC-6). This is what makes a Storage Health row (which
 * carries the type name on the bus segment leaf) reach all four views.
 */
function SegmentActions({ typeName }: { typeName: string }): React.JSX.Element {
  const { archetypes } = useArchetypesForComponent(typeName);
  const primary = pickPrimaryArchetype(archetypes);
  const cls = 'w-full rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent';
  return (
    <div className="mt-3 flex flex-col gap-1 border-t border-border pt-2">
      <button type="button" onClick={() => openComponentInSchema(typeName)} data-testid="segment-open-schema" className={cls}>
        Open in Component Inspector
      </button>
      {primary && (
        <button type="button" onClick={() => openDataBrowser(primary.archetypeId)} data-testid="segment-open-data-browser" className={cls}>
          Open in Data Browser
        </button>
      )}
      <button type="button" onClick={() => openDbMapForComponent(typeName)} data-testid="segment-reveal-file-map" className={cls}>
        Reveal in File Map
      </button>
      <button type="button" onClick={() => revealComponentInResourceTree(typeName)} data-testid="segment-reveal-resource" className={cls}>
        Reveal in Resource Tree
      </button>
    </div>
  );
}

// Five-band primary-bucket fill histogram (empty / ¼ / ½ / ¾ / full) — a horizontal stacked bar makes hash skew
// visible at a glance (a heavy "full" band next to many "empty" buckets ⇒ uneven distribution).
function BucketFillBar({ map }: { map: NonNullable<StorageSegmentSummaryDto['entityMap']> }) {
  const bands: { label: string; count: number; cls: string }[] = [
    { label: 'empty', count: map.fillEmpty, cls: 'bg-muted' },
    { label: '1–25%', count: map.fillQuarter, cls: 'bg-sky-900' },
    { label: '26–50%', count: map.fillHalf, cls: 'bg-sky-700' },
    { label: '51–75%', count: map.fillThreeQuarter, cls: 'bg-sky-500' },
    { label: '76–100%', count: map.fillFull, cls: 'bg-sky-400' },
  ];
  const total = bands.reduce((s, b) => s + b.count, 0);
  if (total === 0) return null;
  return (
    <div className="mt-2">
      <p className="mb-1 text-fs-xs uppercase tracking-wide text-muted-foreground">Bucket fill</p>
      <div className="flex h-2 w-full overflow-hidden rounded-sm">
        {bands.map((b) =>
          b.count === 0 ? null : (
            <div
              key={b.label}
              className={b.cls}
              style={{ width: `${(b.count / total) * 100}%` }}
              title={`${b.label}: ${b.count.toLocaleString()} buckets`}
            />
          ),
        )}
      </div>
    </div>
  );
}

function DbMapChunkDetail({
  databaseName,
  pageIndex,
  chunk,
}: {
  databaseName: string;
  pageIndex: number;
  chunk: DbChunkContent | null;
}) {
  return (
    <DbMapDetailCard
      icon={<Boxes className="h-4 w-4 text-muted-foreground" />}
      title={chunk ? `Chunk ${chunk.chunkId}` : 'Chunk'}
      badge={chunk?.decoder}
      databaseName={databaseName}
    >
      {!chunk ? (
        <p className="text-fs-sm text-muted-foreground">Decoding chunk…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
            <Row label="Page" value={String(pageIndex)} />
            <Row label="Segment" value={`#${chunk.segmentId}`} />
            {chunk.componentType && <Row label="Component" value={chunk.componentType} />}
            <Row label="Occupied" value={chunk.occupied ? 'yes' : 'no'} />
            <Row label="Byte offset" value={`0x${chunk.byteOffset.toString(16).toUpperCase()}`} />
            <Row label="Size" value={`${chunk.size} B`} />
          </dl>
          {chunk.clusterComponents.length > 0 && <ClusterOverlayPicker chunk={chunk} />}
          {chunk.cells.length > 0 ? (
            <CellList title={`Decoded content · ${chunk.cells.length} cells`} cells={chunk.cells} />
          ) : (
            <p className="mt-3 border-t border-border pt-2 text-fs-sm text-muted-foreground">
              No typed decoder — undecoded content.
            </p>
          )}
        </>
      )}
    </DbMapDetailCard>
  );
}

// Per-component enabled-state overlay picker (A6 §10.1) — shown for cluster chunks. Selecting a component recolours
// every L4 entity slot of this cluster segment on the map by whether that component is enabled for each entity
// (green) vs occupied-but-disabled (dim red); "Occupancy" restores the plain lit/dark colouring. The mask is already
// in the decode, so switching is a pure client recolour — no refetch.
function ClusterOverlayPicker({ chunk }: { chunk: DbChunkContent }) {
  const segmentId = useDbMapOverlayStore((s) => s.segmentId);
  const componentSlot = useDbMapOverlayStore((s) => s.componentSlot);
  const setOverlay = useDbMapOverlayStore((s) => s.setOverlay);
  const clearOverlay = useDbMapOverlayStore((s) => s.clear);
  const occupancyActive = !(segmentId === chunk.segmentId && componentSlot != null);

  const chipCls = (active: boolean) =>
    `rounded px-1.5 py-0.5 text-fs-xs font-mono ${
      active ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground hover:bg-muted/70'
    }`;

  return (
    <div className="mt-3 border-t border-border pt-2">
      <p className="mb-1 text-fs-xs uppercase tracking-wide text-muted-foreground">Component overlay</p>
      <div className="flex flex-wrap gap-1">
        <button type="button" className={chipCls(occupancyActive)} onClick={clearOverlay} title="Colour slots by occupancy only">
          Occupancy
        </button>
        {chunk.clusterComponents.map((name, i) => {
          const active = segmentId === chunk.segmentId && componentSlot === i;
          const short = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : name;
          return (
            <button
              key={name}
              type="button"
              className={chipCls(active)}
              title={`Overlay: ${name} (enabled = green, disabled = dim)`}
              onClick={() => setOverlay(chunk.segmentId, i, name)}
            >
              {short}
            </button>
          );
        })}
      </div>
      {!occupancyActive && (
        <p className="mt-1.5 flex items-center gap-2 text-fs-xs text-muted-foreground">
          <span className="inline-block h-2 w-2 rounded-sm" style={{ background: 'rgb(34,197,94)' }} /> enabled
          <span className="inline-block h-2 w-2 rounded-sm" style={{ background: 'rgb(120,53,53)' }} /> disabled
        </p>
      )}
    </div>
  );
}

function DbMapCellDetail({
  databaseName,
  chunk,
  cellOffset,
  cell,
}: {
  databaseName: string;
  chunk: DbChunkContent | null;
  cellOffset: number;
  cell: DbContentCell | null;
}) {
  return (
    <DbMapDetailCard
      icon={<Binary className="h-4 w-4 text-muted-foreground" />}
      title={cell ? cell.label : `Cell @${cellOffset}`}
      badge={cell?.kind}
      databaseName={databaseName}
    >
      {!cell ? (
        <p className="text-fs-sm text-muted-foreground">Decoding…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
            <dt className="text-muted-foreground">Value</dt>
            <dd className="break-all font-mono text-foreground">{cell.value}</dd>
            <Row label="Kind" value={cell.kind} />
            <Row label="Offset" value={`${cell.offset} (in chunk)`} />
            <Row label="Size" value={`${cell.size} B`} />
            {chunk?.componentType && <Row label="Component" value={chunk.componentType} />}
          </dl>
          {/* The overlay matters most at L4 (where clicking selects a slot/cell) — surface the picker here too so the
              component can be switched while looking at the entity grid. */}
          {chunk && chunk.clusterComponents.length > 0 && <ClusterOverlayPicker chunk={chunk} />}
        </>
      )}
    </DbMapDetailCard>
  );
}

function CellList({ title, cells }: { title: string; cells: DbContentCell[] }) {
  return (
    <div className="mt-3 border-t border-border pt-2">
      <p className="mb-1 text-fs-xs uppercase tracking-wide text-muted-foreground">{title}</p>
      <div className="max-h-64 overflow-auto">
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-0.5 text-fs-sm">
          {cells.slice(0, 256).map((c, i) => (
            <div key={`${c.kind}-${c.offset}-${i}`} className="contents">
              <dt className="truncate text-muted-foreground" title={c.label}>
                {c.label}
              </dt>
              <dd className="truncate font-mono text-foreground" title={c.value}>
                {c.value}
              </dd>
            </div>
          ))}
        </dl>
      </div>
    </div>
  );
}

function FieldDetail({ field, schema }: { field: Field; schema: ComponentSchema }) {
  const distanceToBoundary = 64 - (field.offset % 64);
  const crossesBoundary = field.size > distanceToBoundary;
  const nextFieldOffset = computeNextFieldOffset(field, schema);
  const paddingAfter = nextFieldOffset != null ? nextFieldOffset - (field.offset + field.size) : null;

  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          <Binary className="h-4 w-4 text-muted-foreground" />
          <h3 className="text-fs-lg font-semibold text-foreground">{field.name}</h3>
          {field.isIndexed && (
            <StatusBadge tone="success">
              indexed{field.indexAllowsMultiple ? ' (multi)' : ''}
            </StatusBadge>
          )}
          {crossesBoundary && <StatusBadge tone="warn">crosses cache line</StatusBadge>}
          <span className="ml-auto font-mono text-fs-sm text-muted-foreground">
            {schema.typeName}
          </span>
        </div>

        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
          <dt className="text-muted-foreground">Type</dt>
          <dd className="font-mono text-foreground">{field.typeName}</dd>

          <dt className="text-muted-foreground">.NET type</dt>
          <dd className="truncate font-mono text-foreground" title={field.typeFullName}>
            {simplifyTypeName(field.typeFullName)}
          </dd>

          <dt className="text-muted-foreground">Offset</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {field.offset} (0x{field.offset.toString(16).toUpperCase()})
          </dd>

          <dt className="text-muted-foreground">Size</dt>
          <dd className="font-mono tabular-nums text-foreground">{field.size} B</dd>

          <dt className="text-muted-foreground">Field Id</dt>
          <dd className="font-mono tabular-nums text-foreground">{field.fieldId}</dd>

          <dt className="text-muted-foreground">Cache line</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {Math.floor(field.offset / 64)}
            {crossesBoundary && ` → ${Math.floor((field.offset + field.size - 1) / 64)}`}
          </dd>

          <dt className="text-muted-foreground">To next line</dt>
          <dd className="font-mono tabular-nums text-foreground">{distanceToBoundary} B</dd>

          {paddingAfter != null && paddingAfter > 0 && (
            <>
              <dt className="text-muted-foreground">Padding after</dt>
              <dd className="font-mono tabular-nums text-foreground">{paddingAfter} B</dd>
            </>
          )}
        </dl>
      </div>
    </div>
  );
}

function ResourceDetail({ resource }: { resource: SelectedResource }) {
  const selected = resource;
  const { raw } = selected;
  const childrenCount = raw.children?.length ?? 0;
  const entityCount = raw.entityCount != null ? Number(raw.entityCount) : null;

  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          <FolderOpen className="h-4 w-4 text-muted-foreground" />
          <h3 className="text-fs-lg font-semibold text-foreground">{selected.name}</h3>
          <span className="ml-auto text-fs-sm text-muted-foreground">{selected.kind}</span>
        </div>

        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm">
          <dt className="text-muted-foreground">Id</dt>
          <dd className="truncate text-foreground">{raw.id ?? selected.resourceId}</dd>

          <dt className="text-muted-foreground">Path</dt>
          <dd className="truncate text-foreground">{selected.path.join(' / ')}</dd>

          <dt className="text-muted-foreground">Kind</dt>
          <dd className="text-foreground">{selected.kind}</dd>

          {entityCount != null && (
            <>
              <dt className="text-muted-foreground">Entities</dt>
              <dd className="text-foreground">{entityCount.toLocaleString()}</dd>
            </>
          )}

          <dt className="text-muted-foreground">Children</dt>
          <dd className="text-foreground">{childrenCount}</dd>
        </dl>
        <ResourceActions resource={selected} />
      </div>
    </div>
  );
}

/**
 * Resource → handoff verbs (GAP-03 / GAP-04). Only a ComponentTable resource whose recovered component
 * resolves against the live list gets verbs — a naming-convention change makes them vanish (PC-6), never a
 * broken handoff. The resource name carries the *registered* name (`typeName`; the CLR `fullName` can differ —
 * e.g. `Fixtures` vs `Fixture`), so match typeName first. "Open in Data Browser" needs a populated archetype;
 * "Reveal in File Map" needs only the component (its segment exists regardless of entity count).
 */
function ResourceActions({ resource }: { resource: SelectedResource }): React.JSX.Element | null {
  const { list } = useComponentList();
  const recovered = componentNameFromResource(resource.kind, resource.name);
  const component = recovered ? list.find((c) => c.typeName === recovered || c.fullName === recovered) : undefined;
  const { archetypes } = useArchetypesForComponent(component?.typeName ?? null);
  const primary = pickPrimaryArchetype(archetypes);
  if (!component) {
    return null;
  }
  return (
    <div className="mt-3 flex flex-col gap-1">
      {primary && (
        <button
          type="button"
          onClick={() => openDataBrowser(primary.archetypeId)}
          data-testid="resource-open-data-browser"
          className="w-full rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
        >
          Open in Data Browser
        </button>
      )}
      <button
        type="button"
        onClick={() => openDbMapForComponent(component.typeName)}
        data-testid="resource-reveal-file-map"
        className="w-full rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
      >
        Reveal in File Map
      </button>
    </div>
  );
}

// Adjacent-field offset by byte position, not array order — the design doc sorts fields by offset
// for the Layout view, so the ordering is already byte-ascending; we still lookup by offset>current
// to stay robust if that ever changes.
function computeNextFieldOffset(field: Field, schema: ComponentSchema): number | null {
  let best: number | null = null;
  for (const f of schema.fields) {
    if (f.offset <= field.offset) continue;
    if (best == null || f.offset < best) best = f.offset;
  }
  return best;
}
