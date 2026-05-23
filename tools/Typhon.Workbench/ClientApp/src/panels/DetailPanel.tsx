import { useMemo, useState, type ReactNode } from 'react';
import { Binary, Boxes, ChevronDown, ChevronRight, FolderOpen, HardDrive, Layers, ListTree, Pin, PinOff, Workflow } from 'lucide-react';
import { StatusBadge } from '@/components/ui/status-badge';
import { simplifyTypeName } from '@/libs/simplifyTypeName';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapSelectionStore, type DbMapSelection } from '@/stores/useDbMapSelectionStore';
import { useDbMapOverlayStore } from '@/stores/useDbMapOverlayStore';
import { useDbMapChunk, useDbMapPage } from '@/hooks/dbmap/useDbMapDetail';
import { useDbMapSegmentSummary } from '@/hooks/dbmap/useDbMapSegment';
import type { DbChunkContent, DbContentCell, DbPageDetail, StorageSegmentSummaryDto } from '@/libs/dbmap/types';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import type { ComponentSchema, Field } from '@/hooks/schema/types';
import ProfilerDetail from '@/panels/profiler/ProfilerDetail';
import { useEntityDetail } from '@/hooks/dataBrowser/useEntityDetail';
import EntityCardsDetail from '@/panels/DataBrowser/EntityCardsDetail';
import { useSelectionStore, type SelectionLeaf } from '@/stores/useSelectionStore';
import { resolveChain, type SelectionRef } from '@/stores/selectionChain';
import type { SelectedResource } from '@/stores/useSelectedResourceStore';
import type { ProfilerSelection } from '@/stores/useProfilerSelectionStore';

/**
 * The Inspector (right rail, zone E) — the single "what's selected" surface for the whole app (IA §2.4).
 *
 * Stage 1 (#373): the Inspector now follows the **unified selection bus** ({@link useSelectionStore} `leaf`)
 * instead of arbitrating five siloed stores. The bus leaf is the most-recently selected *primary* object
 * across every object type (the silos write-through to it); projections never steal the leaf. The leaf is
 * rendered **in full** via a per-type card; its containment ancestors ({@link resolveChain}) stack above it
 * as collapsible **summaries** (IA §2.5). A **pin** freezes the chain so the user can click around.
 *
 * The handoff footer (real Open-in/Reveal-in verbs) is intentionally absent in Stage 1 — the deep panels it
 * would target are gated off (Stage 0), and PC-6 forbids dead verbs. It returns with those panels in Stages 2-4.
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
        <p className="text-center text-density-sm text-muted-foreground">
          Select anything — a resource, component, entity, system, or profiler element — to inspect it.
        </p>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col bg-background">
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-3 py-1.5">
        <span className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Inspector</span>
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
      <div className="flex min-h-0 flex-1 flex-col">
        {ancestors.length > 0 && (
          <div className="shrink-0 border-b border-border">
            {ancestors.map((node) => (
              <AncestorSection key={`${node.type}:${String(node.ref)}`} node={node} />
            ))}
          </div>
        )}
        <div className="min-h-0 flex-1 overflow-auto">
          <LeafCard leaf={activeLeaf} />
        </div>
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
      return <ObjectSummaryCard icon={<Boxes className="h-4 w-4 text-muted-foreground" />} kind="Component" title={String(leaf.ref)} />;
    case 'archetype':
      return <ObjectSummaryCard icon={<Layers className="h-4 w-4 text-muted-foreground" />} kind="Archetype" title={`#${String(leaf.ref)}`} />;
    case 'system':
      return <ObjectSummaryCard icon={<Workflow className="h-4 w-4 text-muted-foreground" />} kind="System" title={String(leaf.ref)} />;
    case 'query':
      return <ObjectSummaryCard icon={<ListTree className="h-4 w-4 text-muted-foreground" />} kind="Query" title={queryLabel(leaf.ref)} />;
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

/**
 * A summary card for an object whose rich/deep surface is a (currently-gated) workspace panel — Component,
 * Archetype, System, Query. It states what's selected and that the deep view returns later (PC-6: an
 * explained gated state, never a dead "Open in →" button).
 */
function ObjectSummaryCard({ icon, kind, title }: { icon: ReactNode; kind: string; title: string }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          {icon}
          <h3 className="truncate text-[13px] font-semibold text-foreground" title={title}>{title}</h3>
          <span className="ml-auto text-[11px] text-muted-foreground">{kind}</span>
        </div>
        <p className="text-[11px] text-muted-foreground">
          The {kind} deep view returns in a later stage of the Workbench redesign.
        </p>
      </div>
    </div>
  );
}

/** A collapsible containment-ancestor summary line in the Inspector context stack (expanded by default). */
function AncestorSection({ node }: { node: SelectionRef }): React.JSX.Element {
  const [open, setOpen] = useState(true);
  return (
    <div className="px-3 py-1">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-1.5 text-left text-[11px] text-muted-foreground hover:text-foreground"
      >
        {open ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
        <span className="text-[10px] uppercase tracking-wide">{node.type}</span>
        {open && <span className="truncate font-mono text-foreground">{String(node.ref)}</span>}
      </button>
    </div>
  );
}

/** Card-body loading placeholder (PC-2 loading state). */
function CardLoading({ label }: { label: string }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3">
        <p className="text-[11px] text-muted-foreground">Loading {label}…</p>
      </div>
    </div>
  );
}

// Database File Map selection (Module 15, §6.5) — a page, a chunk, or a content cell, fully decoded by the
// server-side detail tier (A2). The hooks below are called unconditionally; the irrelevant ones stay disabled.
function DbMapDetail({ selection }: { selection: DbMapSelection }) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const databaseName = useDbMapSelectionStore((s) => s.databaseName);
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
    return <DbMapSegmentDetail databaseName={databaseName} segmentId={selection.segmentId} summary={summary ?? null} />;
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
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          {icon}
          <h3 className="text-[13px] font-semibold text-foreground">{title}</h3>
          {badge && <StatusBadge tone="neutral">{badge}</StatusBadge>}
          <span className="ml-auto truncate font-mono text-[11px] text-muted-foreground">{databaseName}</span>
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
  const select = useDbMapSelectionStore((s) => s.select);
  return (
    <DbMapDetailCard
      icon={<HardDrive className="h-4 w-4 text-muted-foreground" />}
      title={`Page ${pageIndex}`}
      badge={page?.pageType}
      databaseName={databaseName}
    >
      {!page ? (
        <p className="text-[11px] text-muted-foreground">Decoding page…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
            <Row label="Byte offset" value={`0x${page.byteOffset.toString(16).toUpperCase()}`} />
            <dt className="text-muted-foreground">Owning segment</dt>
            <dd className="font-mono tabular-nums text-foreground">
              {page.ownerSegmentId >= 0 ? (
                <button
                  type="button"
                  onClick={() => select(databaseName, { kind: 'segment', segmentId: page.ownerSegmentId })}
                  className="text-sky-400 underline-offset-2 hover:underline"
                  title="Show segment harvest summary"
                >
                  #{page.ownerSegmentId} · {page.ownerSegmentKind}
                </button>
              ) : (
                'none'
              )}
            </dd>
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
  summary,
}: {
  databaseName: string;
  segmentId: number;
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
        <p className="text-[11px] text-muted-foreground">Harvesting segment summary…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
              <p className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">Cluster fill</p>
              <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
                <Row label="Entities" value={Number(summary.entityCount).toLocaleString()} />
                <Row label="Active clusters" value={summary.activeClusterCount.toLocaleString()} />
                <Row label="Slots / cluster" value={String(summary.clusterSize)} />
                {slotOccPct != null && <Row label="Slot occupancy" value={`${slotOccPct}%`} />}
              </dl>
            </div>
          )}

          {map && (
            <div className="mt-3 border-t border-border pt-2">
              <p className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">Entity-map (linear hash)</p>
              <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
    </DbMapDetailCard>
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
      <p className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">Bucket fill</p>
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
        <p className="text-[11px] text-muted-foreground">Decoding chunk…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
            <p className="mt-3 border-t border-border pt-2 text-[11px] text-muted-foreground">
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
    `rounded px-1.5 py-0.5 text-[10px] font-mono ${
      active ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground hover:bg-muted/70'
    }`;

  return (
    <div className="mt-3 border-t border-border pt-2">
      <p className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">Component overlay</p>
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
        <p className="mt-1.5 flex items-center gap-2 text-[10px] text-muted-foreground">
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
        <p className="text-[11px] text-muted-foreground">Decoding…</p>
      ) : (
        <>
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
      <p className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">{title}</p>
      <div className="max-h-64 overflow-auto">
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-0.5 text-[11px]">
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
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          <Binary className="h-4 w-4 text-muted-foreground" />
          <h3 className="text-[13px] font-semibold text-foreground">{field.name}</h3>
          {field.isIndexed && (
            <StatusBadge tone="success">
              indexed{field.indexAllowsMultiple ? ' (multi)' : ''}
            </StatusBadge>
          )}
          {crossesBoundary && <StatusBadge tone="warn">crosses cache line</StatusBadge>}
          <span className="ml-auto font-mono text-[11px] text-muted-foreground">
            {schema.typeName}
          </span>
        </div>

        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          <FolderOpen className="h-4 w-4 text-muted-foreground" />
          <h3 className="text-[13px] font-semibold text-foreground">{selected.name}</h3>
          <span className="ml-auto text-[11px] text-muted-foreground">{selected.kind}</span>
        </div>

        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
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
      </div>
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
