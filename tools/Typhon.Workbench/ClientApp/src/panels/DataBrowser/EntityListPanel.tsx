import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from 'lucide-react';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentList } from '@/hooks/schema/useComponentList';
import type { ArchetypeInfo } from '@/hooks/schema/types';
import { useEntityPage } from '@/hooks/dataBrowser/useEntityPage';
import { useComponentSchemas } from '@/hooks/dataBrowser/useComponentSchemas';
import { archetypeIdFromRawEntityId } from '@/hooks/dataBrowser/entityId';
import { defaultPreviewFields, serializePreview, type PreviewField } from '@/hooks/dataBrowser/previewFields';
import { formatValue } from '@/hooks/dataBrowser/formatValue';
import { useDataBrowserStore, DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS } from '@/stores/useDataBrowserStore';
import EntityListContextMenu from './EntityListContextMenu';
import EntityColumnPicker from './EntityColumnPicker';

const ROW_HEIGHT = 22;

/**
 * Data Browser — Entity List (Module 06, v1). Pick an archetype, page through its entities with configurable preview columns
 * (page-size + prev/next footer; page size is an explicit count or "Auto" = fit-to-height). Row click selects the entity for
 * the Component Cards panel. Read-only.
 */
export default function EntityListPanel(_props: IDockviewPanelProps) {
  const archetypeId = useDataBrowserStore((s) => s.archetypeId);
  const selectedEntityId = useDataBrowserStore((s) => s.selectedEntityId);
  const pageSize = useDataBrowserStore((s) => s.pageSize);
  const autoPageSize = useDataBrowserStore((s) => s.autoPageSize);
  const pageIndex = useDataBrowserStore((s) => s.pageIndex);
  const previewFields = useDataBrowserStore((s) => s.previewFields);
  const setArchetype = useDataBrowserStore((s) => s.setArchetype);
  const selectEntity = useDataBrowserStore((s) => s.selectEntity);
  const setPageSize = useDataBrowserStore((s) => s.setPageSize);
  const setAutoPageSize = useDataBrowserStore((s) => s.setAutoPageSize);
  const setPageIndex = useDataBrowserStore((s) => s.setPageIndex);
  const setPreviewFields = useDataBrowserStore((s) => s.setPreviewFields);

  const { list: archetypes } = useArchetypeList();
  const { list: components } = useComponentList();
  const currentArchetype = archetypes.find((a) => a.archetypeId === archetypeId);
  // ArchetypeInfo.componentTypes are CLR full names; the schema endpoint + decode key off the registered component name
  // (def.Name). Map CLR full name → registered name via the component summaries so lookups resolve when they differ.
  const nameByFullName = useMemo(() => new Map(components.map((c) => [c.fullName, c.typeName])), [components]);
  const componentNames = (currentArchetype?.componentTypes ?? []).map((ct) => nameByFullName.get(ct) ?? ct);
  const schemas = useComponentSchemas(componentNames);
  const effectivePreview = previewFields ?? defaultPreviewFields(componentNames, schemas);
  const previewSpec = serializePreview(effectivePreview);
  const fieldName = (pf: PreviewField) =>
    schemas.get(pf.typeName)?.fields.find((f) => f.fieldId === pf.fieldId)?.name ?? `#${pf.fieldId}`;

  // "Go to id": jump to (and select) any entity by its raw id. The id encodes its archetype (low 12 bits), so we switch the
  // picker automatically. Existence is reflected by the Component Cards panel ("failed to load" for a dead id).
  const [goInput, setGoInput] = useState('');
  const [goError, setGoError] = useState(false);
  const submitGoto = () => {
    const archId = archetypeIdFromRawEntityId(goInput);
    if (archId === null) {
      setGoError(true);
      return;
    }
    setGoError(false);
    if (archId !== archetypeId) {
      setArchetype(archId);
    }
    selectEntity(goInput.trim());
  };

  // Auto page size: how many rows fit in the scroll viewport, minus the sticky header row. Measured via ResizeObserver.
  const listRef = useRef<HTMLDivElement>(null);
  const [autoSize, setAutoSize] = useState(DEFAULT_PAGE_SIZE);
  useLayoutEffect(() => {
    const el = listRef.current;
    if (!el) return;
    const measure = () => setAutoSize(Math.max(1, Math.floor(el.clientHeight / ROW_HEIGHT) - 1));
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const effectivePageSize = autoPageSize ? autoSize : pageSize;
  const offset = pageIndex * effectivePageSize;
  const { rows, total, isLoading, isError, isFetching } = useEntityPage(archetypeId, offset, effectivePageSize, previewSpec);

  const pageCount = Math.max(1, Math.ceil(total / effectivePageSize));
  const rangeStart = total === 0 ? 0 : offset + 1;
  const rangeEnd = offset + rows.length;
  const canPrev = pageIndex > 0;
  const canNext = rangeEnd < total;

  useEffect(() => {
    if (pageIndex > pageCount - 1) {
      setPageIndex(pageCount - 1);
    }
  }, [pageCount, pageIndex, setPageIndex]);

  const onPageSizeChange = (value: string) => {
    if (value === 'auto') {
      setAutoPageSize(true);
    } else {
      setPageSize(Number(value));
    }
  };

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-[12px] font-semibold text-foreground">Data Browser</h3>
        <select
          className="ml-1 w-60 max-w-[15rem] truncate rounded border border-border bg-background px-1.5 py-0.5 text-[11px] text-foreground"
          value={archetypeId ?? ''}
          onChange={(e) => setArchetype(e.target.value || null)}
          data-testid="archetype-picker"
        >
          <option value="">Select an archetype…</option>
          {archetypes.map((a) => (
            <option key={a.archetypeId} value={a.archetypeId} title={archetypeLabel(a, false)}>
              {archetypeLabel(a, true)}
            </option>
          ))}
        </select>
        <form
          className="flex items-center"
          onSubmit={(e) => {
            e.preventDefault();
            submitGoto();
          }}
        >
          <input
            value={goInput}
            onChange={(e) => {
              setGoInput(e.target.value);
              setGoError(false);
            }}
            placeholder="Go to id…"
            inputMode="numeric"
            spellCheck={false}
            className={`w-28 rounded border bg-background px-1.5 py-0.5 text-[11px] text-foreground placeholder:text-muted-foreground/60 ${
              goError ? 'border-destructive' : 'border-border'
            }`}
            title={goError ? 'Enter a numeric entity id' : 'Jump to an entity by id (Enter) — switches archetype automatically'}
            data-testid="goto-id"
          />
        </form>
        {archetypeId && (
          <span className="ml-auto text-[11px] tabular-nums text-muted-foreground" data-testid="entity-count">
            {total.toLocaleString()} entities
          </span>
        )}
      </div>

      <div ref={listRef} className="flex-1 overflow-auto">
        {!archetypeId && (
          <div className="flex h-full items-center justify-center">
            <p className="text-[12px] text-muted-foreground">Select an archetype to browse its entities.</p>
          </div>
        )}
        {archetypeId && isError && <p className="p-3 text-[12px] text-destructive">Failed to load entities.</p>}
        {archetypeId && isLoading && <p className="p-3 text-[12px] text-muted-foreground">Loading entities…</p>}
        {archetypeId && !isLoading && !isError && total === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">This archetype has no entities.</p>
        )}
        {archetypeId && rows.length > 0 && (
          <table className="w-full border-collapse text-[12px]">
            <thead className="sticky top-0 z-10 bg-background">
              <tr className="border-b border-border text-[10px] uppercase tracking-wide text-muted-foreground">
                <th className="px-3 py-0.5 text-left font-medium">Entity ID</th>
                {effectivePreview.map((pf) => (
                  <th key={`${pf.typeName}:${pf.fieldId}`} className="px-2 py-0.5 text-left font-medium" title={`${pf.typeName}.${fieldName(pf)}`}>
                    {fieldName(pf)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const selected = row.entityId === selectedEntityId;
                return (
                  <EntityListContextMenu key={row.entityId} entityId={row.entityId}>
                    <tr
                      className={`cursor-pointer border-b border-border/40 ${
                        selected ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50'
                      }`}
                      style={{ height: ROW_HEIGHT }}
                      onClick={() => selectEntity(row.entityId)}
                      data-testid="entity-row"
                      data-entity-id={row.entityId}
                    >
                      <td className="whitespace-nowrap px-3 font-mono tabular-nums">{row.entityId}</td>
                      {effectivePreview.map((pf, i) => (
                        <td key={`${pf.typeName}:${pf.fieldId}`} className="whitespace-nowrap px-2 font-mono tabular-nums">
                          {row.preview[i] ? formatValue(row.preview[i]) : ''}
                        </td>
                      ))}
                    </tr>
                  </EntityListContextMenu>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {archetypeId && total > 0 && (
        <div
          className="flex items-center gap-2 border-t border-border px-3 py-1 text-[11px] text-muted-foreground"
          data-testid="entity-pager"
        >
          <label className="flex items-center gap-1">
            Rows
            <select
              className="rounded border border-border bg-background px-1 py-0.5 text-[11px] text-foreground"
              value={autoPageSize ? 'auto' : pageSize}
              onChange={(e) => onPageSizeChange(e.target.value)}
              data-testid="page-size"
            >
              <option value="auto">Auto</option>
              {PAGE_SIZE_OPTIONS.map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
          {autoPageSize && <span className="tabular-nums text-muted-foreground/70">({effectivePageSize}/page)</span>}
          <EntityColumnPicker componentNames={componentNames} schemas={schemas} effective={effectivePreview} onChange={setPreviewFields} />

          <span className="ml-auto tabular-nums" data-testid="page-range">
            {rangeStart.toLocaleString()}–{rangeEnd.toLocaleString()} of {total.toLocaleString()}
          </span>

          <div className="flex items-center gap-0.5">
            <PagerButton label="First page" disabled={!canPrev} onClick={() => setPageIndex(0)}>
              <ChevronsLeft className="h-3.5 w-3.5" />
            </PagerButton>
            <PagerButton label="Previous page" disabled={!canPrev} onClick={() => setPageIndex(pageIndex - 1)}>
              <ChevronLeft className="h-3.5 w-3.5" />
            </PagerButton>
            <span className="px-1 tabular-nums" data-testid="page-indicator">
              {(pageIndex + 1).toLocaleString()} / {pageCount.toLocaleString()}
            </span>
            <PagerButton label="Next page" disabled={!canNext} onClick={() => setPageIndex(pageIndex + 1)}>
              <ChevronRight className="h-3.5 w-3.5" />
            </PagerButton>
            <PagerButton label="Last page" disabled={!canNext} onClick={() => setPageIndex(pageCount - 1)}>
              <ChevronsRight className="h-3.5 w-3.5" />
            </PagerButton>
          </div>

          {isFetching && <span className="text-[10px] text-muted-foreground/70">…</span>}
        </div>
      )}
    </div>
  );
}

function PagerButton({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className="flex h-5 w-5 items-center justify-center rounded text-foreground hover:bg-accent disabled:cursor-default disabled:opacity-30 disabled:hover:bg-transparent"
    >
      {children}
    </button>
  );
}

// Component type names are fully-qualified (e.g. "Typhon.Test.ECS.Position") — show the last segment in the compact picker.
function shortName(typeName: string): string {
  const dot = typeName.lastIndexOf('.');
  return dot >= 0 ? typeName.slice(dot + 1) : typeName;
}

// `#id · count · Comp+Comp+…` — the component list is ellipsis-truncated for the closed select so a many-component archetype
// doesn't overflow the picker. `truncate=false` (used for the option's title tooltip) keeps the full list.
function archetypeLabel(a: ArchetypeInfo, truncate: boolean): string {
  const comps = a.componentTypes.map(shortName).join('+');
  const shown = truncate && comps.length > 22 ? `${comps.slice(0, 21)}…` : comps;
  return `#${a.archetypeId} · ${a.entityCount} · ${shown}`;
}
