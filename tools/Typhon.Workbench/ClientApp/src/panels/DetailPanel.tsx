import type { ReactNode } from 'react';
import { Binary, Boxes, FolderOpen, HardDrive } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { StatusBadge } from '@/components/ui/status-badge';
import { simplifyTypeName } from '@/libs/simplifyTypeName';
import { useSelectedResourceStore } from '@/stores/useSelectedResourceStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapSelectionStore, type DbMapSelection } from '@/stores/useDbMapSelectionStore';
import { useDbMapChunk, useDbMapPage } from '@/hooks/dbmap/useDbMapDetail';
import type { DbChunkContent, DbContentCell, DbPageDetail } from '@/libs/dbmap/types';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import type { ComponentSchema, Field } from '@/hooks/schema/types';
import ProfilerDetail from '@/panels/profiler/ProfilerDetail';

/**
 * The Detail panel — a single "what's selected" surface. Four independent stores feed it:
 *  - `useSchemaInspectorStore.selectedField` (schema canvas click, arrow-nav, Index row click)
 *  - `useSelectedResourceStore.selected` (resource-tree click)
 *  - `useProfilerSelectionStore.selected` (profiler panel — span / chunk / tick / marker)
 *  - `useDbMapSelectionStore.selected` (Database File Map — page click)
 *
 * Whichever was touched most recently wins — matches the IDE convention that the user's latest
 * interaction drives what the inspector shows. Graceful fallback: if the winner's data isn't
 * loaded (e.g., `schema` still fetching), fall back to the next non-empty source. Empty state
 * fires only when nothing has ever been selected from any of the four.
 */
export default function DetailPanel() {
  const selectedType = useSchemaInspectorStore((s) => s.selectedComponentType);
  const selectedFieldName = useSchemaInspectorStore((s) => s.selectedField);
  const fieldTouchedAt = useSchemaInspectorStore((s) => s.fieldTouchedAt);
  const { schema } = useComponentSchema(selectedType);
  const resource = useSelectedResourceStore((s) => s.selected);
  const resourceTouchedAt = useSelectedResourceStore((s) => s.touchedAt);
  const profilerSelected = useProfilerSelectionStore((s) => s.selected);
  const profilerTouchedAt = useProfilerSelectionStore((s) => s.touchedAt);
  const dbMapSelected = useDbMapSelectionStore((s) => s.selected);
  const dbMapTouchedAt = useDbMapSelectionStore((s) => s.touchedAt);

  // Profiler session signals — used to render the range-stats fallback when no click selection has
  // been made but the user is exploring a profiler trace. The fallback runs through the same
  // ProfilerDetail entry point with `selection={null}`; ProfilerDetail reads its data straight from
  // `useProfilerStatsStore` (populated by `useProfilerStatsWriter` in ProfilerPanel) so we don't
  // re-instantiate `useProfilerCache` here.
  const profilerMetadata = useProfilerSessionStore((s) => s.metadata);
  const sessionKind = useSessionStore((s) => s.kind);
  const isProfilerSession = sessionKind === 'attach' || sessionKind === 'trace';

  const field: Field | undefined =
    selectedFieldName && schema
      ? schema.fields.find((f) => f.name === selectedFieldName)
      : undefined;

  const fieldAvailable = !!field && !!schema;
  const resourceAvailable = !!resource;
  const profilerAvailable = profilerSelected !== null;
  const dbMapAvailable = dbMapSelected !== null;
  // The range-stats fallback is "available" whenever a profiler session is open and has metadata —
  // viewRange is always defined; an empty viewRange just renders the empty-state inside the detail.
  const profilerRangeFallbackAvailable = isProfilerSession && profilerMetadata !== null;

  // 4-way arbitration by recency. Each candidate's `at` is 0 when unavailable so they drop out
  // of the max comparison — that way a never-populated source can't accidentally win on a
  // stale `touchedAt` tombstone.
  const fieldAt    = fieldAvailable    ? fieldTouchedAt    : 0;
  const resourceAt = resourceAvailable ? resourceTouchedAt : 0;
  const profilerAt = profilerAvailable ? profilerTouchedAt : 0;
  const dbMapAt    = dbMapAvailable    ? dbMapTouchedAt    : 0;
  const winnerAt = Math.max(fieldAt, resourceAt, profilerAt, dbMapAt);

  if (winnerAt === 0) {
    // Nothing was clicked. If a profiler session is open, show the range-stats fallback over the
    // current viewport — that way the right pane carries useful info even before the user picks a
    // specific element. Outside profiler land, fall through to the empty prompt.
    if (profilerRangeFallbackAvailable) {
      return <ProfilerDetail selection={null} />;
    }
    return (
      <div className="flex h-full items-center justify-center bg-background">
        <p className="text-density-sm text-muted-foreground">
          Select a resource, component, field, or profiler element to see details
        </p>
      </div>
    );
  }

  // Dispatch to the winning source. Graceful fallback chain if the winner's data isn't loaded.
  if (dbMapAt === winnerAt && dbMapAvailable) {
    return <DbMapDetail selection={dbMapSelected!} />;
  }
  if (profilerAt === winnerAt && profilerAvailable) {
    return <ProfilerDetail selection={profilerSelected!} />;
  }
  if (fieldAt === winnerAt && fieldAvailable) {
    return <FieldDetail field={field!} schema={schema!} />;
  }
  if (resourceAt === winnerAt && resourceAvailable) {
    return <ResourceDetail />;
  }
  // Fall-back: show whichever source has data, preferring profiler > field > resource > dbMap.
  if (profilerAvailable) return <ProfilerDetail selection={profilerSelected!} />;
  if (fieldAvailable)    return <FieldDetail field={field!} schema={schema!} />;
  if (resourceAvailable) return <ResourceDetail />;
  if (dbMapAvailable)    return <DbMapDetail selection={dbMapSelected!} />;
  return null;
}

// Database File Map selection (Module 15, §6.5) — a page, a chunk, or a content cell, fully decoded by the
// server-side detail tier (A2). The hooks below are called unconditionally; the irrelevant ones stay disabled.
function DbMapDetail({ selection }: { selection: DbMapSelection }) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const databaseName = useDbMapSelectionStore((s) => s.databaseName);
  const pageIndex = selection.kind === 'page' ? selection.pageIndex : null;
  const segId = selection.kind !== 'page' ? selection.segmentId : null;
  const chunkId = selection.kind !== 'page' ? selection.chunkId : null;
  const { data: page } = useDbMapPage(sessionId, pageIndex);
  const { data: chunk } = useDbMapChunk(sessionId, segId, chunkId);

  if (selection.kind === 'page') {
    return <DbMapPageDetail databaseName={databaseName} pageIndex={selection.pageIndex} page={page ?? null} />;
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
            <Row
              label="Owning segment"
              value={page.ownerSegmentId >= 0 ? `#${page.ownerSegmentId} · ${page.ownerSegmentKind}` : 'none'}
            />
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
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
          <dt className="text-muted-foreground">Value</dt>
          <dd className="break-all font-mono text-foreground">{cell.value}</dd>
          <Row label="Kind" value={cell.kind} />
          <Row label="Offset" value={`${cell.offset} (in chunk)`} />
          <Row label="Size" value={`${cell.size} B`} />
          {chunk?.componentType && <Row label="Component" value={chunk.componentType} />}
        </dl>
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
          {cells.slice(0, 256).map((c) => (
            <div key={`${c.kind}-${c.offset}`} className="contents">
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

function ResourceDetail() {
  const selected = useSelectedResourceStore((s) => s.selected);
  if (!selected) return null;

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

        <div className="mt-3 flex flex-wrap gap-2 border-t border-border pt-2">
          <Button disabled size="sm" variant="outline" title="Coming in a later phase">
            Open in Query
          </Button>
          <Button disabled size="sm" variant="outline" title="Coming in a later phase">
            Open in Entities
          </Button>
          <Button disabled size="sm" variant="outline" title="Coming in a later phase">
            Open in Schema
          </Button>
        </div>
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
