import { useState } from 'react';
import { Command } from 'cmdk';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { SpatialClauseDto } from '@/api/generated/model/spatialClauseDto';
import { useArchetypeComponents } from '@/hooks/queryConsole/useArchetypeComponents';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import { humpFilter } from '@/shell/camelHumpFilter';

/**
 * SPATIAL chip (#386). A single spatial constraint: pick a `[SpatialIndex]` component (picker filtered via the
 * schema's `hasSpatialIndex` flag), choose a kind, and fill the kind's numeric parameters. The engine attaches one
 * spatial predicate per query, so chip mode allows a single SPATIAL stage (the compiler also enforces this with
 * `spatial_single_clause_only`).
 *
 * Parameters follow the §5.1 grammar / {@link SpatialClauseDto} layout: NEARBY `[cx,cy,cz,radius]`, AABB
 * `[minX,minY,minZ,maxX,maxY,maxZ]`, RAY `[ox,oy,oz,dx,dy,dz,maxDist]`. The Z components are part of the 3D-shaped
 * grammar; the server drops them for 2D components.
 */
export type SpatialKind = 'nearby' | 'aabb' | 'ray';

const KIND_FIELDS: Record<SpatialKind, readonly string[]> = {
  nearby: ['cx', 'cy', 'cz', 'radius'],
  aabb: ['minX', 'minY', 'minZ', 'maxX', 'maxY', 'maxZ'],
  ray: ['ox', 'oy', 'oz', 'dx', 'dy', 'dz', 'maxDist'],
};

const KIND_LABEL: Record<SpatialKind, string> = { nearby: 'NEARBY', aabb: 'AABB', ray: 'RAY' };

const ALL_KINDS: readonly SpatialKind[] = ['nearby', 'aabb', 'ray'];

function zeros(kind: SpatialKind): number[] {
  return KIND_FIELDS[kind].map(() => 0);
}

export function SpatialChip({
  value,
  archetype,
  onChange,
}: {
  value: SpatialClauseDto | null;
  archetype: string | null | undefined;
  onChange: (next: SpatialClauseDto | null) => void;
}) {
  const { label: nameLabel } = useComponentNames();

  if (!value) {
    return (
      <AddSpatialPopover
        archetype={archetype}
        onPick={(component) => onChange({ component, kind: 'nearby', parameters: zeros('nearby') })}
      />
    );
  }

  const kind = (value.kind ?? 'nearby') as SpatialKind;
  const fields = KIND_FIELDS[kind] ?? KIND_FIELDS.nearby;
  const params = fields.map((_, i) => Number(value.parameters?.[i] ?? 0));

  const setKind = (k: SpatialKind) => onChange({ ...value, kind: k, parameters: zeros(k) });
  const setParam = (i: number, v: number) => onChange({ ...value, parameters: params.map((p, j) => (j === i ? v : p)) });

  return (
    <span className="inline-flex flex-wrap items-center gap-1 rounded border border-border bg-muted/50 px-1.5 py-0.5 font-mono text-xs">
      <span className="text-muted-foreground" title={value.component ?? undefined}>
        {nameLabel(value.component)}
      </span>
      <select
        value={kind}
        onChange={(e) => setKind(e.target.value as SpatialKind)}
        className="rounded border border-border bg-background px-1 py-0.5 text-foreground"
        aria-label="Spatial kind"
      >
        {ALL_KINDS.map((k) => (
          <option key={k} value={k}>
            {KIND_LABEL[k]}
          </option>
        ))}
      </select>
      {fields.map((f, i) => (
        <label key={f} className="flex items-center gap-0.5">
          <span className="text-muted-foreground">{f}</span>
          <input
            type="number"
            value={params[i]}
            onChange={(e) => setParam(i, Number(e.target.value) || 0)}
            className="w-16 rounded border border-border bg-background px-1 py-0.5"
            aria-label={`${KIND_LABEL[kind]} ${f}`}
          />
        </label>
      ))}
      <button
        type="button"
        onClick={() => onChange(null)}
        className="text-muted-foreground hover:text-foreground"
        aria-label="Remove SPATIAL"
        title="Remove"
      >
        ×
      </button>
    </span>
  );
}

function AddSpatialPopover({
  archetype,
  onPick,
}: {
  archetype: string | null | undefined;
  onPick: (component: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const { components, isLoading } = useArchetypeComponents(archetype);
  const { label: nameLabel } = useComponentNames();
  const spatial = components.filter((c) => c.hasSpatialIndex);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={!archetype}
          className="inline-flex items-center gap-0.5 rounded border border-dashed border-border px-1.5 py-0.5 text-xs text-muted-foreground hover:bg-muted disabled:opacity-50"
          title={archetype ? 'Add a SPATIAL constraint' : 'Pick an archetype first'}
        >
          <Plus className="h-3 w-3" />
          SPATIAL
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-0">
        <Command filter={humpFilter}>
          <Command.Input
            placeholder="Pick a [SpatialIndex] component…"
            className="w-full border-b border-border bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
          />
          <Command.List className="max-h-60 overflow-auto p-1">
            {isLoading && <div className="px-3 py-2 text-xs text-muted-foreground">Loading…</div>}
            {!isLoading && spatial.length === 0 && (
              <Command.Empty className="px-3 py-2 text-xs text-muted-foreground">
                {archetype ? 'No [SpatialIndex] component on this archetype.' : 'Pick an archetype first.'}
              </Command.Empty>
            )}
            {spatial.map((c) => (
              <Command.Item
                key={c.typeName}
                value={c.typeName}
                keywords={[nameLabel(c.typeName), `${c.fullName} ${c.typeName}`]}
                onSelect={() => {
                  onPick(c.typeName);
                  setOpen(false);
                }}
                className="flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
              >
                <span className="truncate font-mono" title={c.typeName}>
                  {nameLabel(c.typeName)}
                </span>
                <span className="shrink-0 text-xs text-muted-foreground">spatial</span>
              </Command.Item>
            ))}
          </Command.List>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
