import { Binary, FolderOpen } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { StatusBadge } from '@/components/ui/status-badge';
import { simplifyTypeName } from '@/libs/simplifyTypeName';
import { useSelectedResourceStore } from '@/stores/useSelectedResourceStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import type { ComponentSchema, Field } from '@/hooks/schema/types';
import ProfilerDetail from '@/panels/profiler/ProfilerDetail';

/**
 * The Detail panel — a single "what's selected" surface. Three independent stores feed it:
 *  - `useSchemaInspectorStore.selectedField` (schema canvas click, arrow-nav, Index row click)
 *  - `useSelectedResourceStore.selected` (resource-tree click)
 *  - `useProfilerSelectionStore.selected` (profiler panel — span / chunk / tick / marker)
 *
 * Whichever was touched most recently wins — matches the IDE convention that the user's latest
 * interaction drives what the inspector shows. Graceful fallback: if the winner's data isn't
 * loaded (e.g., `schema` still fetching), fall back to the next non-empty source. Empty state
 * fires only when nothing has ever been selected from any of the three.
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
  // The range-stats fallback is "available" whenever a profiler session is open and has metadata —
  // viewRange is always defined; an empty viewRange just renders the empty-state inside the detail.
  const profilerRangeFallbackAvailable = isProfilerSession && profilerMetadata !== null;

  // 3-way arbitration by recency. Each candidate's `at` is 0 when unavailable so they drop out
  // of the max comparison — that way a never-populated source can't accidentally win on a
  // stale `touchedAt` tombstone.
  const fieldAt    = fieldAvailable    ? fieldTouchedAt    : 0;
  const resourceAt = resourceAvailable ? resourceTouchedAt : 0;
  const profilerAt = profilerAvailable ? profilerTouchedAt : 0;
  const winnerAt = Math.max(fieldAt, resourceAt, profilerAt);

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
  if (profilerAt === winnerAt && profilerAvailable) {
    return <ProfilerDetail selection={profilerSelected!} />;
  }
  if (fieldAt === winnerAt && fieldAvailable) {
    return <FieldDetail field={field!} schema={schema!} />;
  }
  if (resourceAt === winnerAt && resourceAvailable) {
    return <ResourceDetail />;
  }
  // Fall-back: show whichever source has data, preferring profiler > field > resource.
  if (profilerAvailable) return <ProfilerDetail selection={profilerSelected!} />;
  if (fieldAvailable)    return <FieldDetail field={field!} schema={schema!} />;
  if (resourceAvailable) return <ResourceDetail />;
  return null;
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
