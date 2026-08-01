import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Copy } from 'lucide-react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionCapability } from '@/stores/useSessionStore';
import { openComponentInspector, openDataBrowser } from '@/shell/commands/openSchemaBrowser';
import { revealArchetypeInFileMap } from '@/shell/commands/openDbMap';
import { useArchetypeStorage } from '@/hooks/profiles/useArchetypeStorage';
import { formatFileSize } from '@/lib/formatters';
import { StorageModePill } from '@/panels/SchemaExplorer/SchemaExplorerPanel';
import InspectorTargetSwitcher, { type SwitcherItem } from '@/panels/schemaCommon/InspectorTargetSwitcher';
import { useInspectorTarget } from '@/panels/schemaCommon/useInspectorTarget';
import { useArchetypeNames } from '@/hooks/queryConsole/useArchetypeNames';
import type { TargetCandidate } from '@/panels/schemaCommon/inspectorTarget';
import { findArchetype, resolveArchetypeComponents, indexedComponents } from './archetypeInspectorModel';

/**
 * Archetype Inspector (Stage 2, GAP-02) — the deep view for one archetype: tabs Components · Entities ·
 * Storage · Indexes-here. Driven by the bus `archetype` leaf, but PINNED to the last archetype selected:
 * clicking a component inside it sets the leaf to `component` (right-rail + later the Component Inspector)
 * without blanking this panel. Handoffs to not-yet-built views (Data Browser, File Map, Component
 * Inspector deep view) degrade to an explained note rather than a disabled stub (PC-6/PC-2).
 */

type Tab = 'components' | 'entities' | 'storage' | 'indexes';
const TABS: { id: Tab; label: string }[] = [
  { id: 'components', label: 'Components' },
  { id: 'entities', label: 'Entities' },
  { id: 'storage', label: 'Storage' },
  { id: 'indexes', label: 'Indexes' },
];

export default function ArchetypeInspectorPanel(_props: IDockviewPanelProps) {
  const { list: archetypes, isLoading: aLoading, isError } = useArchetypeList();
  const { list: components } = useComponentList();
  const select = useSelectionStore((s) => s.select);
  const { label: archName } = useArchetypeNames();

  // Self-addressing target (PC-9): the bus `archetype` leaf when there is one, else an auto-pick over the
  // loaded archetypes — so this deep view is never an empty dead-end. Drilling into a component sets the
  // `component` leaf, which doesn't match our type, so the panel stays put.
  const candidates = useMemo<TargetCandidate[]>(
    () => archetypes.map((a) => ({ id: a.archetypeId, entityCount: a.entityCount })),
    [archetypes],
  );
  const { targetId, auto, pick } = useInspectorTarget({ type: 'archetype', candidates, loading: aLoading });
  const switcherItems = useMemo<SwitcherItem[]>(
    () =>
      archetypes.map((a) => {
        const short = archName(a.archetypeId);
        return {
          id: a.archetypeId,
          label: short === a.archetypeId ? `#${a.archetypeId}` : short,
          meta: `#${a.archetypeId} · ${a.entityCount.toLocaleString()} ent`,
          keywords: `#${a.archetypeId} ${a.archetypeId} ${a.componentTypes.join(' ')}`,
        };
      }),
    [archetypes, archName],
  );

  const [tab, setTab] = useState<Tab>('components');

  const archetype = findArchetype(archetypes, targetId);

  if (isError) {
    return (
      <div data-testid="archetype-inspector" className="p-3 text-fs-base text-destructive">
        Failed to load schema.
      </div>
    );
  }
  if (!archetype) {
    // No resolvable target: still loading, or (PC-2 Empty) the DB genuinely has no archetypes. PC-9 means we
    // never show a "pick elsewhere" dead-end while archetypes exist.
    return (
      <div
        data-testid="archetype-inspector"
        className="flex h-full items-center justify-center bg-background p-4 text-center"
      >
        <p className="text-fs-base text-muted-foreground">
          {aLoading || candidates.length > 0 ? 'Loading…' : 'This database has no archetypes.'}
        </p>
      </div>
    );
  }

  const rows = resolveArchetypeComponents(archetype, components);
  const indexed = indexedComponents(rows);

  return (
    <div data-testid="archetype-inspector" className="flex h-full w-full flex-col overflow-hidden bg-background">
      {/* Header */}
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-3 py-1.5">
        <InspectorTargetSwitcher
          label="Archetype"
          currentLabel={
            archName(archetype.archetypeId) === archetype.archetypeId
              ? `#${archetype.archetypeId}`
              : `${archName(archetype.archetypeId)} (#${archetype.archetypeId})`
          }
          auto={auto}
          autoTitle="Auto-selected the archetype with the most entities — pick another above."
          items={switcherItems}
          onPick={pick}
          testId="archetype"
          noun="archetype"
        />
        <span className="text-fs-sm text-muted-foreground">
          {archetype.componentTypes.length} components · {archetype.entityCount.toLocaleString()} entities
        </span>
        <StorageModePill mode={archetype.storageMode} />
        <button
          type="button"
          onClick={() => void navigator.clipboard?.writeText(archetype.archetypeId)}
          title="Copy archetype id"
          aria-label="Copy archetype id"
          className="ml-auto flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-muted hover:text-foreground"
        >
          <Copy className="h-3 w-3" />
        </button>
      </div>

      {/* Tabs */}
      <div role="tablist" className="flex shrink-0 border-b border-border px-1">
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            aria-selected={tab === t.id}
            onClick={() => setTab(t.id)}
            className={`px-3 py-1 text-fs-sm ${tab === t.id ? 'border-b-2 border-primary text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {tab === 'components' && (
          <Table className="text-fs-base">
            <TableHeader>
              <TableRow>
                <TableHead className="text-fs-sm">Name</TableHead>
                <TableHead className="text-right text-fs-sm">Size</TableHead>
                <TableHead className="text-right text-fs-sm">Indexes</TableHead>
                <TableHead className="text-right text-fs-sm">Storage mode</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((r) => (
                <TableRow
                  key={r.fullName}
                  className="cursor-pointer hover:bg-accent"
                  data-testid="archetype-component-row"
                  data-type-name={r.typeName}
                  title={r.fullName}
                  onClick={() => select('component', r.typeName)}
                  onDoubleClick={() => {
                    select('component', r.typeName);
                    openComponentInspector();
                  }}
                >
                  <TableCell className="font-mono">{r.typeName}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {r.summary ? `${r.summary.storageSize}B` : '—'}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{r.summary?.indexCount ?? '—'}</TableCell>
                  <TableCell className="text-right text-muted-foreground">{r.summary?.storageMode ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}

        {tab === 'entities' && (
          <div className="p-4 text-fs-base">
            <p className="text-foreground">{archetype.entityCount.toLocaleString()} entities</p>
            {archetype.entityCount > 0 ? (
              <button
                type="button"
                onClick={() => openDataBrowser(archetype.archetypeId)}
                data-testid="archetype-open-data-browser"
                className="mt-2 rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
              >
                Open in Data Browser →
              </button>
            ) : (
              <p className="mt-1 text-fs-sm text-muted-foreground">No entities to browse.</p>
            )}
          </div>
        )}

        {tab === 'storage' && (
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 p-4 text-fs-base">
            <dt className="text-muted-foreground">Strategy</dt>
            <dd>
              <StorageModePill mode={archetype.storageMode} />
            </dd>
            <dt className="text-muted-foreground">Entities</dt>
            <dd className="tabular-nums">{archetype.entityCount.toLocaleString()}</dd>
            <dt className="text-muted-foreground">Chunks</dt>
            <dd className="tabular-nums">
              {archetype.storageMode === 'cluster' ? `${archetype.chunkCount} × ${archetype.chunkCapacity}` : '—'}
            </dd>
            <dt className="text-muted-foreground">Occupancy</dt>
            <dd className="tabular-nums">
              {archetype.storageMode === 'cluster' && archetype.chunkCount > 0
                ? `${archetype.occupancyPct.toFixed(1)}%`
                : '—'}
            </dd>
            <ArchetypeStorageBreakdown archetypeName={archetype.name} />
          </dl>
        )}

        {tab === 'indexes' && (
          <div className="p-1">
            <p className="px-2 py-1 text-fs-sm text-muted-foreground">
              Indexes are type-global (one B+Tree per indexed field, spanning all archetypes). These components in this
              archetype carry an index — open a component for field-level detail.
            </p>
            {indexed.length === 0 ? (
              <p className="px-2 py-2 text-fs-base text-muted-foreground">No indexed components in this archetype.</p>
            ) : (
              <Table className="text-fs-base">
                <TableBody>
                  {indexed.map((r) => (
                    <TableRow
                      key={r.fullName}
                      className="cursor-pointer hover:bg-accent"
                      data-testid="archetype-index-row"
                      data-type-name={r.typeName}
                      onClick={() => select('component', r.typeName)}
                    >
                      <TableCell className="font-mono">{r.typeName}</TableCell>
                      <TableCell className="text-right tabular-nums">
                        {r.summary?.indexCount} {r.summary?.indexCount === 1 ? 'index' : 'indexes'}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * Where this archetype actually lives on disk (#619 §4.2), and the reveal that frames it.
 *
 * The split is the point. **Owned** segments — cluster rows, entity map, cluster index — are this archetype's
 * alone, and they are what the totals and the reveal cover. **Shared** component tables are type-global: one table
 * per component type, used by every archetype carrying it. Listing them without summing them is the only honest
 * rendering, and it is what makes a legacy (non-cluster) archetype legible — its rows are in those shared tables,
 * so a small owned footprint is the correct answer rather than a suspicious one.
 *
 * Before #619 this section's reveal called `openDbMapForComponent(rows[0].typeName)` — the archetype's *first
 * component*, which for a cluster archetype has no component-table segment at all, so the button did nothing.
 */
function ArchetypeStorageBreakdown({ archetypeName }: { archetypeName: string }): React.JSX.Element {
  const hasDatabase = useSessionCapability('database');
  // The staleness caveat belongs here only when a capture is attached — that is the one situation where these
  // present-tense numbers sit near recorded ones and could be read as explaining them. With no capture there is no
  // trace-time to confuse them with, and an unprompted apology for data that is simply current is noise.
  const hasCapture = useSessionCapability('profiler');
  const storage = useArchetypeStorage(hasDatabase ? archetypeName : null);

  // §5.7 / IA §7: a handoff that cannot resolve is absent, not a disabled button that lies about being available.
  if (!hasDatabase || !storage.resolved) {
    return <></>;
  }

  return (
    <>
      <dt className="text-muted-foreground">Segments</dt>
      <dd data-testid="archetype-owned-segments">
        {storage.owned.map((s) => s.kind).join(' · ')}
        <span className="ml-1 text-muted-foreground">
          ({storage.totalPages.toLocaleString()} {storage.totalPages === 1 ? 'page' : 'pages'}
          {storage.totalBytes > 0 && ` · ${formatFileSize(storage.totalBytes)}`})
        </span>
      </dd>
      {storage.shared.length > 0 && (
        <>
          <dt className="text-muted-foreground">Shared tables</dt>
          <dd data-testid="archetype-shared-tables" className="text-muted-foreground">
            {storage.shared.length} component {storage.shared.length === 1 ? 'table' : 'tables'}, shared with every
            archetype carrying those components — not counted above.
          </dd>
        </>
      )}
      {hasCapture && (
        <dd className="col-span-2 mt-1 text-fs-sm text-muted-foreground" data-testid="archetype-storage-caveat">
          This is the layout <em>now</em>, not at the attached capture's ticks — checkpointing, compaction and cluster
          migration move pages.
        </dd>
      )}
      <dd className="col-span-2 mt-2">
        <button
          type="button"
          onClick={() => revealArchetypeInFileMap(archetypeName)}
          data-testid="archetype-reveal-file-map"
          title="Frame this archetype's own segments in the File Map"
          className="rounded border border-border px-2 py-1 text-fs-sm text-foreground hover:bg-accent"
        >
          Reveal in File Map →
        </button>
      </dd>
    </>
  );
}
