import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Copy } from 'lucide-react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { openComponentInspector, openDataBrowser } from '@/shell/commands/openSchemaBrowser';
import { openDbMapForComponent } from '@/shell/commands/openDbMap';
import { StorageModePill } from '@/panels/SchemaExplorer/SchemaExplorerPanel';
import InspectorTargetSwitcher, { type SwitcherItem } from '@/panels/schemaCommon/InspectorTargetSwitcher';
import { useInspectorTarget } from '@/panels/schemaCommon/useInspectorTarget';
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
      archetypes.map((a) => ({
        id: a.archetypeId,
        label: `#${a.archetypeId}`,
        meta: `${a.entityCount.toLocaleString()} ent`,
        keywords: a.componentTypes.join(' '),
      })),
    [archetypes],
  );

  const [tab, setTab] = useState<Tab>('components');

  const archetype = findArchetype(archetypes, targetId);

  if (isError) {
    return <div data-testid="archetype-inspector" className="p-3 text-[12px] text-destructive">Failed to load schema.</div>;
  }
  if (!archetype) {
    // No resolvable target: still loading, or (PC-2 Empty) the DB genuinely has no archetypes. PC-9 means we
    // never show a "pick elsewhere" dead-end while archetypes exist.
    return (
      <div data-testid="archetype-inspector" className="flex h-full items-center justify-center bg-background p-4 text-center">
        <p className="text-[12px] text-muted-foreground">
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
          currentLabel={`#${archetype.archetypeId}`}
          auto={auto}
          autoTitle="Auto-selected the archetype with the most entities — pick another above."
          items={switcherItems}
          onPick={pick}
          testId="archetype"
          noun="archetype"
        />
        <span className="text-[11px] text-muted-foreground">
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
            className={`px-3 py-1 text-[11px] ${tab === t.id ? 'border-b-2 border-primary text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {tab === 'components' && (
          <Table className="text-[12px]">
            <TableHeader>
              <TableRow>
                <TableHead className="py-1 text-[11px]">Name</TableHead>
                <TableHead className="py-1 text-right text-[11px]">Size</TableHead>
                <TableHead className="py-1 text-right text-[11px]">Indexes</TableHead>
                <TableHead className="py-1 text-right text-[11px]">Storage mode</TableHead>
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
                  <TableCell className="py-1 font-mono">{r.typeName}</TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{r.summary ? `${r.summary.storageSize}B` : '—'}</TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{r.summary?.indexCount ?? '—'}</TableCell>
                  <TableCell className="py-1 text-right text-muted-foreground">{r.summary?.storageMode ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}

        {tab === 'entities' && (
          <div className="p-4 text-[12px]">
            <p className="text-foreground">{archetype.entityCount.toLocaleString()} entities</p>
            {archetype.entityCount > 0 ? (
              <button
                type="button"
                onClick={() => openDataBrowser(archetype.archetypeId)}
                data-testid="archetype-open-data-browser"
                className="mt-2 rounded border border-border px-2 py-1 text-[11px] text-foreground hover:bg-accent"
              >
                Open in Data Browser →
              </button>
            ) : (
              <p className="mt-1 text-[11px] text-muted-foreground">No entities to browse.</p>
            )}
          </div>
        )}

        {tab === 'storage' && (
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 p-4 text-[12px]">
            <dt className="text-muted-foreground">Strategy</dt>
            <dd><StorageModePill mode={archetype.storageMode} /></dd>
            <dt className="text-muted-foreground">Entities</dt>
            <dd className="tabular-nums">{archetype.entityCount.toLocaleString()}</dd>
            <dt className="text-muted-foreground">Chunks</dt>
            <dd className="tabular-nums">{archetype.storageMode === 'cluster' ? `${archetype.chunkCount} × ${archetype.chunkCapacity}` : '—'}</dd>
            <dt className="text-muted-foreground">Occupancy</dt>
            <dd className="tabular-nums">{archetype.storageMode === 'cluster' && archetype.chunkCount > 0 ? `${archetype.occupancyPct.toFixed(1)}%` : '—'}</dd>
            {rows.length > 0 && (
              <dd className="col-span-2 mt-2">
                <button
                  type="button"
                  onClick={() => openDbMapForComponent(rows[0].typeName)}
                  data-testid="archetype-reveal-file-map"
                  title="Reveal this archetype's storage in the File Map"
                  className="rounded border border-border px-2 py-1 text-[11px] text-foreground hover:bg-accent"
                >
                  Reveal in File Map →
                </button>
              </dd>
            )}
          </dl>
        )}

        {tab === 'indexes' && (
          <div className="p-1">
            <p className="px-2 py-1 text-[11px] text-muted-foreground">
              Indexes are type-global (one B+Tree per indexed field, spanning all archetypes). These components in this
              archetype carry an index — open a component for field-level detail.
            </p>
            {indexed.length === 0 ? (
              <p className="px-2 py-2 text-[12px] text-muted-foreground">No indexed components in this archetype.</p>
            ) : (
              <Table className="text-[12px]">
                <TableBody>
                  {indexed.map((r) => (
                    <TableRow
                      key={r.fullName}
                      className="cursor-pointer hover:bg-accent"
                      data-testid="archetype-index-row"
                      data-type-name={r.typeName}
                      onClick={() => select('component', r.typeName)}
                    >
                      <TableCell className="py-1 font-mono">{r.typeName}</TableCell>
                      <TableCell className="py-1 text-right tabular-nums">
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
