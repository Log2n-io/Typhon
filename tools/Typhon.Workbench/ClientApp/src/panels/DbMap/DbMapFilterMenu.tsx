import { Filter } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { DbPageType, PAGE_TYPE_LABELS } from '@/libs/dbmap/types';

// The toolbar filter-to-dim menu (Module 15, A4, §4.6). A popover of page-type checkboxes; unchecking a type
// dims its cells on the map rather than hiding them. The filter is inactive when every type is checked — the
// store then holds `null`, and the renderer skips the dim layer.

/** Page types offered as filter rows — every classified type bar the never-seen `Unknown`. */
const FILTERABLE_TYPES: readonly number[] = [
  DbPageType.Free,
  DbPageType.Root,
  DbPageType.Occupancy,
  DbPageType.Component,
  DbPageType.Revision,
  DbPageType.Index,
  DbPageType.Cluster,
  DbPageType.Vsbs,
  DbPageType.StringTable,
];

export function DbMapFilterMenu() {
  const filter = useDbMapStore((s) => s.filter);
  const setFilter = useDbMapStore((s) => s.setFilter);
  const active = filter !== null;

  const isShown = (t: number): boolean => !filter || filter.pageTypes.includes(t);

  const toggle = (t: number): void => {
    const current = filter ? filter.pageTypes : [...FILTERABLE_TYPES];
    const next = current.includes(t) ? current.filter((x) => x !== t) : [...current, t];
    // Every type shown again → the filter is inactive; store null so the renderer drops the dim layer.
    setFilter(next.length === FILTERABLE_TYPES.length ? null : { pageTypes: next });
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          className={`flex items-center gap-1 rounded border px-1.5 py-0.5 text-fs-sm ${
            active ? 'border-primary bg-primary/15 text-foreground' : 'border-border bg-card text-muted-foreground'
          }`}
          title="Filter cells by page type — non-matching cells dim rather than vanish"
          data-testid="dbmap-filter"
        >
          <Filter className="h-3 w-3" /> Filter{filter ? ` (${filter.pageTypes.length})` : ''}
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-48 p-2" align="start">
        <div className="flex items-center justify-between pb-1">
          <span className="text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">
            Show page types
          </span>
          {active && (
            <button
              type="button"
              onClick={() => setFilter(null)}
              className="text-fs-xs text-primary hover:underline"
            >
              Clear
            </button>
          )}
        </div>
        <div className="flex flex-col gap-0.5">
          {FILTERABLE_TYPES.map((t) => (
            <label
              key={t}
              className="flex items-center gap-1.5 rounded px-1 py-0.5 text-fs-sm hover:bg-muted/60"
            >
              <input
                type="checkbox"
                checked={isShown(t)}
                onChange={() => toggle(t)}
                className="h-3 w-3 accent-primary"
                data-testid={`dbmap-filter-type-${t}`}
              />
              <span className="text-foreground">{PAGE_TYPE_LABELS[t]}</span>
            </label>
          ))}
        </div>
      </PopoverContent>
    </Popover>
  );
}
