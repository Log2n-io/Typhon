import { Search } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { useQueryCatalogStore } from './useQueryCatalogStore';

/**
 * Header bar for the Query Catalog panel. Holds the free-text search input + system / archetype
 * filter dropdowns. Reads/writes filter state to the panel's Zustand store; data fetching is
 * orchestrated by the parent panel.
 *
 * System/archetype dropdowns are populated from the union of values observed in the catalog —
 * passed in via props rather than computed here so the parent owns the data dependency.
 *
 * Issue #338 (P5 of #342).
 */
interface ToolbarProps {
  totalCount: number;
  filteredCount: number;
  archetypeOptions: { id: number; name: string }[];
  systemOptions: { id: number; name: string }[];
}

export function QueryCatalogToolbar({ totalCount, filteredCount, archetypeOptions, systemOptions }: ToolbarProps) {
  const search = useQueryCatalogStore((s) => s.search);
  const setSearch = useQueryCatalogStore((s) => s.setSearch);
  const systemFilter = useQueryCatalogStore((s) => s.systemFilter);
  const setSystemFilter = useQueryCatalogStore((s) => s.setSystemFilter);
  const archetypeFilter = useQueryCatalogStore((s) => s.archetypeFilter);
  const setArchetypeFilter = useQueryCatalogStore((s) => s.setArchetypeFilter);

  return (
    <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
      <div className="relative flex-1 max-w-xs">
        <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input
          type="text"
          placeholder="Search filters / owners / archetype…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="h-7 pl-7 text-[12px]"
          data-testid="query-catalog-search"
        />
      </div>

      <select
        value={archetypeFilter ?? ''}
        onChange={(e) => setArchetypeFilter(e.target.value === '' ? null : Number(e.target.value))}
        className="h-7 rounded border border-border bg-background px-2 text-[12px]"
        data-testid="query-catalog-archetype-filter"
      >
        <option value="">All archetypes</option>
        {archetypeOptions.map((a) => (
          <option key={a.id} value={a.id}>{a.name}</option>
        ))}
      </select>

      <select
        value={systemFilter ?? ''}
        onChange={(e) => setSystemFilter(e.target.value === '' ? null : Number(e.target.value))}
        className="h-7 rounded border border-border bg-background px-2 text-[12px]"
        data-testid="query-catalog-system-filter"
      >
        <option value="">All systems</option>
        {systemOptions.map((s) => (
          <option key={s.id} value={s.id}>{s.name}</option>
        ))}
      </select>

      <span className="ml-auto text-[11px] text-muted-foreground tabular-nums">
        {filteredCount === totalCount ? `${totalCount}` : `${filteredCount} / ${totalCount}`}
      </span>
    </div>
  );
}
