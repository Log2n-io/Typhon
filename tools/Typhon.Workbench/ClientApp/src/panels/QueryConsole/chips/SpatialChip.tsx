import { useState } from 'react';
import { Command } from 'cmdk';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { SpatialClauseDto } from '@/api/generated/model/spatialClauseDto';
import { useArchetypeComponents } from '@/hooks/queryConsole/useArchetypeComponents';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import { humpFilter } from '@/shell/camelHumpFilter';
import { realmLabel, type Realm } from '@/hooks/realms/types';

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

/**
 * The SPATIAL clause editor.
 *
 * <b>Realms arrive as props rather than from a hook, and that is deliberate.</b> This is a leaf editor: giving it its
 * own network dependency would make every one of its tests need a QueryClient to assert that a number input updates one
 * index. The panel that owns the spec already has the session context, so it fetches and passes down — which also keeps
 * the realm list and the scope that seeds it resolved in one place instead of two.
 */
export function SpatialChip({
  value,
  archetype,
  onChange,
  realms = [],
  scopedRealm = null,
}: {
  value: SpatialClauseDto | null;
  archetype: string | null | undefined;
  onChange: (next: SpatialClauseDto | null) => void;
  /** Realms this database holds, for the IN REALM picker. Empty on a single-world database, which hides the control. */
  realms?: readonly Realm[];
  /** The context bar's realm scope, already through its link toggle — seeds a newly added clause. */
  scopedRealm?: number | null;
}) {
  const { label: nameLabel } = useComponentNames();

  if (!value) {
    return (
      <AddSpatialPopover
        archetype={archetype}
        onPick={(component) => onChange({ component, kind: 'nearby', parameters: zeros('nearby'), realm: scopedRealm })}
      />
    );
  }

  const kind = (value.kind ?? 'nearby') as SpatialKind;
  const realm = value.realm ?? null;
  const fields = KIND_FIELDS[kind] ?? KIND_FIELDS.nearby;
  const params = fields.map((_, i) => Number(value.parameters?.[i] ?? 0));

  const setKind = (k: SpatialKind) => onChange({ ...value, kind: k, parameters: zeros(k) });
  const setParam = (i: number, v: number) => onChange({ ...value, parameters: params.map((p, j) => (j === i ? v : p)) });
  const setRealm = (r: number | null) => onChange({ ...value, realm: r });

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
      {/* IN REALM — required on a database that holds several, because a spatial query naming none is now refused
          rather than silently answering realm 0 (WB-06). Offered only when there ARE realms to pick, so a single-world
          database's chip is exactly what it was. Seeded from the context bar's scope, so the scope does the work. */}
      {realms.length > 1 && (
        <span className="flex items-center gap-1" title="Which realm the predicate evaluates in">
          <span className="text-muted-foreground">IN REALM</span>
          <select
            value={realm == null ? '' : String(realm)}
            onChange={(e) => setRealm(e.target.value === '' ? null : Number(e.target.value))}
            className="bg-transparent text-inherit outline-none"
            aria-label="Spatial realm"
          >
            <option value="">(none)</option>
            {realms.map((r) => (
              <option key={r.id} value={String(r.id)}>
                {realmLabel(r)}
              </option>
            ))}
          </select>
        </span>
      )}

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
