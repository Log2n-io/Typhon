import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { AlertTriangle, Copy, Info } from 'lucide-react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { StatusBadge } from '@/components/ui/status-badge';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import InspectorTargetSwitcher, { type SwitcherItem } from '@/panels/schemaCommon/InspectorTargetSwitcher';
import { useInspectorTarget } from '@/panels/schemaCommon/useInspectorTarget';
import type { TargetCandidate } from '@/panels/schemaCommon/inspectorTarget';
import { usePanelHotkeys } from '@/hooks/usePanelHotkeys';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useArchetypeNames } from '@/hooks/queryConsole/useArchetypeNames';
import { buildComponentNameMap, leafSegment } from '@/libs/componentNames';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import { useIndexesForComponent } from '@/hooks/schema/useIndexesForComponent';
import { useArchetypesForComponent } from '@/hooks/schema/useArchetypesForComponent';
import { useSystemRelationships } from '@/hooks/schema/useSystemRelationships';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { openArchetypeInspector, openDataBrowser } from '@/shell/commands/openSchemaBrowser';
import { openDbMapForComponent } from '@/shell/commands/openDbMap';
import { pickPrimaryArchetype } from '@/hooks/dataBrowser/pickArchetype';
import AccessChips from '@/panels/SchemaInspector/AccessChips';
import SchemaRelationshipsGraph from '@/panels/SchemaInspector/SchemaRelationshipsGraph';
import SchemaLayoutCanvas from './SchemaLayoutCanvas';

/**
 * Component Inspector (Stage 2, GAP-02) — the flagship deep view for one component TYPE (type-global facts).
 * A clean rebuild of the former four `Schema*` panels into one tabbed surface (the old panels carried
 * disabled "Open in" stubs — PC-6 — so they are not reused). Driven by the bus `component` leaf, pinned so
 * drilling inward doesn't blank it. Tabs are added as they land (no placeholder/stub tabs): this increment
 * ships Indexes + Used-in; Layout (byte grid), Storage-mode (needs the StorageMode API delta, GAP-25) and
 * Relationships follow.
 */

type Tab = 'layout' | 'indexes' | 'storagemode' | 'usedin' | 'relationships';
const TABS: { id: Tab; label: string }[] = [
  { id: 'layout', label: 'Layout' },
  { id: 'indexes', label: 'Indexes' },
  { id: 'storagemode', label: 'Storage mode' },
  { id: 'usedin', label: 'Used in' },
  { id: 'relationships', label: 'Relationships' },
];

// Plain-language notes mirroring the engine's StorageMode enum (src/Typhon.Schema.Definition/StorageMode.cs).
const STORAGE_MODE_NOTES: Record<string, string> = {
  Versioned: 'Full MVCC snapshot isolation — per-transaction WAL, crash recovery to the exact pre-crash state.',
  SingleVersion: 'In-place writes (last-writer-wins) — tick-fence WAL durability, crash recovery to the last tick boundary.',
  Transient: 'Heap memory only — no persistence, no WAL; all data is lost on crash. The developer owns concurrency.',
};

export default function ComponentInspectorPanel(props: IDockviewPanelProps) {
  const { list: components, isLoading: cLoading, isError } = useComponentList();
  const select = useSelectionStore((s) => s.select);

  // Self-addressing target (PC-9): the bus `component` leaf when there is one, else an auto-pick over the
  // loaded component types — so this deep view is never an empty dead-end. Drilling into a field sets the
  // `field` leaf, which doesn't match our type, so the panel stays put.
  const candidates = useMemo<TargetCandidate[]>(
    () => components.map((c) => ({ id: c.typeName, entityCount: c.entityCount })),
    [components],
  );
  const { targetId, auto, pick } = useInspectorTarget({ type: 'component', candidates, loading: cLoading });

  // Friendly display labels — the same labeller the File Map uses: shorten each registered typeName to its
  // shortest dot-suffix that is still unique across the whole set (`Typhon.Test.EvoCc.Bag` → `Bag`, colliding
  // leaves keep one more segment). Built over the full component list so the labels match the map elsewhere; the
  // raw typeName stays the identity (`id`) and is kept searchable via `keywords`. See {@link buildComponentNameMap}.
  const componentLabel = useMemo(() => {
    const map = buildComponentNameMap(components.map((c) => c.typeName));
    return (name: string): string => map.get(name) ?? leafSegment(name);
  }, [components]);
  const switcherItems = useMemo<SwitcherItem[]>(
    () =>
      components.map((c) => ({
        id: c.typeName,
        label: componentLabel(c.typeName),
        meta: `${c.storageSize}B · used in ${c.archetypeCount ?? '—'}`,
        keywords: `${c.typeName} ${c.fullName}`,
      })),
    [components, componentLabel],
  );

  const [tab, setTab] = useState<Tab>('layout');

  // Panel-scoped `[` / `]` cycle through the tab strip (AC2.3 / PC-8) — active only while this is the focused
  // dock panel, so it works after a click, F6, or the `G C` chord without stealing the keys app-wide.
  usePanelHotkeys(props.api, {
    '[': () => setTab((cur) => TABS[(TABS.findIndex((t) => t.id === cur) - 1 + TABS.length) % TABS.length].id),
    ']': () => setTab((cur) => TABS[(TABS.findIndex((t) => t.id === cur) + 1) % TABS.length].id),
  });

  const summary = targetId ? components.find((c) => c.typeName === targetId || c.fullName === targetId) ?? null : null;

  if (isError) {
    return <div data-testid="component-inspector" className="p-3 text-fs-base text-destructive">Failed to load schema.</div>;
  }
  if (!targetId) {
    // No resolvable target: still loading, or (PC-2 Empty) the DB genuinely has no components. PC-9 means we
    // never show a "pick elsewhere" dead-end while components exist.
    return (
      <div data-testid="component-inspector" className="flex h-full items-center justify-center bg-background p-4 text-center">
        <p className="text-fs-base text-muted-foreground">
          {cLoading || candidates.length > 0 ? 'Loading…' : 'This database has no components.'}
        </p>
      </div>
    );
  }
  const typeName = targetId;

  return (
    <div data-testid="component-inspector" className="flex h-full w-full flex-col overflow-hidden bg-background">
      {/* Header */}
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-3 py-1.5">
        <InspectorTargetSwitcher
          label="Component"
          currentLabel={componentLabel(summary?.typeName ?? typeName)}
          currentTitle={summary?.fullName ?? typeName}
          auto={auto}
          autoTitle="Auto-selected the component with the most entities — pick another above."
          items={switcherItems}
          onPick={pick}
          testId="component"
          noun="component"
        />
        {summary && (
          <span className="text-fs-sm text-muted-foreground">
            {summary.storageSize}B · {summary.fieldCount} fields · used in {summary.archetypeCount ?? '—'}
          </span>
        )}
        <div className="ml-auto flex shrink-0 items-center gap-2">
          <button
            type="button"
            onClick={() => openDbMapForComponent(typeName)}
            data-testid="component-reveal-file-map"
            title="Reveal this component's storage segment in the File Map"
            className="rounded border border-border px-2 py-0.5 text-fs-sm text-foreground hover:bg-accent"
          >
            Reveal in File Map →
          </button>
          <OpenInDataBrowserAction typeName={typeName} />
          <button
            type="button"
            onClick={() => void navigator.clipboard?.writeText(summary?.fullName ?? typeName)}
            title="Copy full type name"
            aria-label="Copy full type name"
            className="flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            <Copy className="h-3 w-3" />
          </button>
        </div>
      </div>

      {/* Tabs — shadcn/Radix (keyboard arrow-nav + a11y roles), styled as the dense underline strip. Radix
          keeps only the active tab's body mounted, so each tab still fetches only when opened. */}
      <Tabs value={tab} onValueChange={(v) => setTab(v as Tab)} className="flex min-h-0 flex-1 flex-col">
        <TabsList className="flex h-auto w-full shrink-0 justify-start gap-0 rounded-none border-b border-border bg-transparent p-0 px-1">
          {TABS.map((t) => (
            <TabsTrigger
              key={t.id}
              value={t.id}
              // Keyboard focus is shown as a translucent focus-color background fill (not a ring/box): the shadcn
              // ring + global outline are suppressed; `:focus-visible` paints `bg-ring/40`. It is `!important`
              // because Radix automatic mode makes the focused tab the *active* tab too, so without it the
              // `data-[state=active]:bg-transparent` rule (emitted later, equal specificity) would win and the
              // fill would never show. Selection stays the solid primary underline, so focus ≠ selection (DS-4).
              className="rounded-none border-b-2 border-transparent px-3 py-1 text-fs-sm font-normal text-muted-foreground hover:text-foreground focus-visible:bg-ring/20! focus-visible:text-foreground focus-visible:outline-none focus-visible:ring-0 focus-visible:ring-offset-0 data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:text-foreground data-[state=active]:shadow-none"
            >
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="layout" className="mt-0 min-h-0 flex-1 overflow-auto">
          <LayoutTab typeName={typeName} />
        </TabsContent>
        <TabsContent value="indexes" className="mt-0 min-h-0 flex-1 overflow-auto">
          <IndexesTab typeName={typeName} />
        </TabsContent>
        <TabsContent value="storagemode" className="mt-0 min-h-0 flex-1 overflow-auto">
          <StorageModeTab typeName={typeName} />
        </TabsContent>
        <TabsContent value="usedin" className="mt-0 min-h-0 flex-1 overflow-auto">
          <UsedInTab typeName={typeName} onReveal={(id) => { select('archetype', id); openArchetypeInspector(); }} />
        </TabsContent>
        <TabsContent value="relationships" className="mt-0 min-h-0 flex-1 overflow-auto">
          <RelationshipsTab typeName={typeName} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

/**
 * Header verb: type-first "Open in → Data Browser" (AC2.7/GAP-03). A component is M:N with archetypes, so we
 * auto-pick the max-entity archetype ({@link pickPrimaryArchetype}) and scope the Data Browser to it; the
 * Browser's own archetype picker is the change control. Renders nothing when no archetype has entities — no
 * dead verb (PC-6) — and stays hidden until the archetype list loads.
 */
function OpenInDataBrowserAction({ typeName }: { typeName: string }) {
  const { archetypes } = useArchetypesForComponent(typeName);
  const primary = pickPrimaryArchetype(archetypes);
  if (!primary) {
    return null;
  }
  return (
    <button
      type="button"
      onClick={() => openDataBrowser(primary.archetypeId)}
      data-testid="component-open-data-browser"
      title={`Browse entities — auto-picks #${primary.archetypeId} (most entities for this component); change the archetype in the Data Browser`}
      className="rounded border border-border px-2 py-0.5 text-fs-sm text-foreground hover:bg-accent"
    >
      Open in Data Browser →
    </button>
  );
}

function LayoutTab({ typeName }: { typeName: string }) {
  const { schema, isLoading, isError } = useComponentSchema(typeName);
  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  // Highlight the field iff the bus field-leaf belongs to this component.
  const selectedField =
    leaf?.type === 'field' && (leaf.ref as { component?: string } | null)?.component === typeName
      ? (leaf.ref as { field: string }).field
      : null;
  return (
    <SchemaLayoutCanvas
      schema={schema ?? null}
      isLoading={isLoading}
      isError={isError}
      selectedField={selectedField}
      onSelectField={(name) => (name ? select('field', { component: typeName, field: name }) : select('component', typeName))}
    />
  );
}

function StorageModeTab({ typeName }: { typeName: string }) {
  const { schema, isLoading, isError } = useComponentSchema(typeName);
  const mode = schema?.storageMode;
  return (
    <div className="p-4 text-fs-base" data-testid="component-storagemode">
      {isError && <p className="text-destructive">Failed to load.</p>}
      {isLoading && !mode && <p className="text-muted-foreground">Loading…</p>}
      {mode && (
        <>
          <p className="text-fs-lg font-semibold text-foreground" data-testid="storagemode-value">{mode}</p>
          <p className="mt-2 text-fs-sm text-muted-foreground">{STORAGE_MODE_NOTES[mode] ?? 'Component MVCC storage mode.'}</p>
        </>
      )}
    </div>
  );
}

function IndexesTab({ typeName }: { typeName: string }) {
  const { indexes, isLoading, isError } = useIndexesForComponent(typeName);
  return (
    <div className="p-1">
      <p className="px-2 py-1 text-fs-sm text-muted-foreground">
        Type-global: one B+Tree per indexed field, spanning every archetype that uses this component.
      </p>
      {isError && <p className="px-2 py-2 text-fs-base text-destructive">Failed to load indexes.</p>}
      {isLoading && <p className="px-2 py-2 text-fs-base text-muted-foreground">Loading…</p>}
      {!isLoading && indexes.length === 0 && <p className="px-2 py-2 text-fs-base text-muted-foreground">No indexed fields.</p>}
      {indexes.length > 0 && (
        <Table className="text-fs-base">
          <TableHeader>
            <TableRow>
              <TableHead className="text-fs-sm">Field</TableHead>
              <TableHead className="text-right text-fs-sm">Offset / Size</TableHead>
              <TableHead className="text-fs-sm">Index type</TableHead>
              <TableHead className="text-fs-sm">Multi</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {indexes.map((i) => (
              <TableRow key={i.fieldName} data-testid="component-index-row" data-field={i.fieldName}>
                <TableCell className="font-mono">{i.fieldName}</TableCell>
                <TableCell className="text-right tabular-nums">{i.fieldOffset} / {i.fieldSize}B</TableCell>
                <TableCell>{i.indexType}</TableCell>
                <TableCell>{i.allowsMultiple ? 'yes' : 'no'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function RelationshipsTab({ typeName }: { typeName: string }) {
  const kind = useSessionStore((s) => s.kind);
  // Relationships (who reads / triggers on a component) are a runtime concept — they come from a trace's
  // recorded topology or a live engine. A static database file (Open) has no systems, so we don't fetch
  // (which would 409 the profiler/topology endpoints) and show a kind-appropriate note instead.
  if (kind === 'open') {
    return (
      <div className="p-4 text-fs-base" data-testid="component-relationships">
        <p className="text-foreground">System relationships</p>
        <p className="mt-1 text-fs-sm text-muted-foreground">
          Which systems read or trigger on this component comes from a running engine — open a <strong>trace</strong> or{' '}
          <strong>attach</strong> to a live engine to see it. A database file on its own has no systems.
        </p>
      </div>
    );
  }
  return <ProfilerRelationships typeName={typeName} />;
}

function ProfilerRelationships({ typeName }: { typeName: string }) {
  const { response, isLoading, isError } = useSystemRelationships(typeName);
  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      <AccessChips componentTypeName={typeName} />
      {!response.runtimeHosted && (
        <div
          data-testid="rel-runtime-banner"
          className="flex items-start gap-2 border-b border-amber-700/40 bg-amber-500/10 px-3 py-2 text-fs-sm text-amber-700 dark:text-amber-300"
        >
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <div>
            <div className="font-semibold">Runtime not hosted</div>
            <div className="opacity-90">System read/trigger relationships report once runtime hosting lands; the access declarations above come from the trace / Init payload regardless.</div>
          </div>
        </div>
      )}
      {response.runtimeHosted && response.systems.length === 0 && (
        <div className="flex items-start gap-2 border-b border-border bg-muted/20 px-3 py-2 text-fs-sm text-muted-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0" />
          <div>No system reads this component via its input view, and none declares it as a change-filter trigger.</div>
        </div>
      )}
      <div className="min-h-0 flex-1" data-testid="component-relationships">
        {isError && <p className="p-3 text-fs-base text-destructive">Failed to load relationships.</p>}
        {isLoading && <p className="p-3 text-fs-base text-muted-foreground">Loading…</p>}
        {!isLoading && !isError && <SchemaRelationshipsGraph componentTypeName={typeName} systems={response.systems} />}
      </div>
    </div>
  );
}

function UsedInTab({ typeName, onReveal }: { typeName: string; onReveal: (archetypeId: string) => void }) {
  const { archetypes, isLoading, isError } = useArchetypesForComponent(typeName);
  const { label: archName } = useArchetypeNames();
  return (
    <div className="p-1">
      <p className="px-2 py-1 text-fs-sm text-muted-foreground">Archetypes that use this component (M:N). Click a row to reveal it in the Archetype Inspector.</p>
      {isError && <p className="px-2 py-2 text-fs-base text-destructive">Failed to load archetypes.</p>}
      {isLoading && <p className="px-2 py-2 text-fs-base text-muted-foreground">Loading…</p>}
      {!isLoading && archetypes.length === 0 && <p className="px-2 py-2 text-fs-base text-muted-foreground">Not used in any archetype.</p>}
      {archetypes.length > 0 && (
        <Table className="text-fs-base">
          <TableHeader>
            <TableRow>
              <TableHead className="text-fs-sm">Archetype</TableHead>
              <TableHead className="text-fs-sm">Storage</TableHead>
              <TableHead className="text-right text-fs-sm">Entities</TableHead>
              <TableHead className="text-right text-fs-sm">Occupancy</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {archetypes.map((a) => (
              <TableRow
                key={a.archetypeId}
                className="cursor-pointer hover:bg-accent"
                data-testid="component-usedin-row"
                data-archetype-id={a.archetypeId}
                onClick={() => onReveal(a.archetypeId)}
              >
                <TableCell className="font-mono" title={`#${a.archetypeId}`}>{archName(`#${a.archetypeId}`)}</TableCell>
                <TableCell>
                  <StatusBadge tone={a.storageMode === 'cluster' ? 'success' : 'warn'}>{a.storageMode}</StatusBadge>
                </TableCell>
                <TableCell className="text-right tabular-nums">{a.entityCount.toLocaleString()}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {a.storageMode === 'cluster' && a.chunkCount > 0 ? `${a.occupancyPct.toFixed(1)}%` : '—'}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
