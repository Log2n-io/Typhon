import { useEffect, useState } from 'react';
import { Command } from 'cmdk';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { OrderByDto } from '@/api/generated/model/orderByDto';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import { useComponentSchema } from '@/hooks/queryConsole/useComponentSchema';
import { humpFilter } from '@/shell/camelHumpFilter';

/**
 * ORDER BY chip — when set, renders as a pill (component.field + asc/desc toggle + remove). When unset, renders
 * an "+ add" button that opens a popover with the field picker constrained to indexed fields on the same
 * component as the WHERE clause (engine constraint at EcsQuery.cs:481-484).
 *
 * When no WHERE has been set, the picker shows "pick a WHERE component first" — the engine requires an OrderBy
 * field that matches an existing WhereField call, so we can't surface a meaningful list without that anchor.
 */
export function OrderByChip({
  value,
  whereComponent,
  onChange,
}: {
  value: OrderByDto | null;
  whereComponent: string | null;
  onChange: (next: OrderByDto | null) => void;
}) {
  const { label: nameLabel } = useComponentNames();
  if (value) {
    return (
      <span className="inline-flex items-center gap-1 rounded border border-border bg-muted/50 px-1.5 py-0.5 font-mono text-xs">
        <span className="text-muted-foreground" title={value.component ?? undefined}>
          {nameLabel(value.component)}.
        </span>
        <span>{value.field}</span>
        <button
          type="button"
          onClick={() => onChange({ ...value, descending: !value.descending })}
          title="Toggle ASC ⇄ DESC"
          className="rounded bg-background px-1 text-muted-foreground hover:text-foreground"
        >
          {value.descending ? 'DESC' : 'ASC'}
        </button>
        <button
          type="button"
          onClick={() => onChange(null)}
          className="text-muted-foreground hover:text-foreground"
          aria-label="Remove ORDER BY"
          title="Remove"
        >
          ×
        </button>
      </span>
    );
  }
  return <AddOrderByPopover whereComponent={whereComponent} onChange={onChange} />;
}

function AddOrderByPopover({
  whereComponent,
  onChange,
}: {
  whereComponent: string | null;
  onChange: (next: OrderByDto) => void;
}) {
  const [open, setOpen] = useState(false);
  const { label: nameLabel } = useComponentNames();
  const { schema } = useComponentSchema(whereComponent);
  const fields = (schema?.fields ?? []).filter((f) => f.isIndexed);
  const whereLabel = whereComponent ? nameLabel(whereComponent) : '';

  // Reset the popover on close so re-opening starts fresh.
  useEffect(() => {
    if (!open) {
      /* no local state to reset for this picker yet */
    }
  }, [open]);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={!whereComponent}
          className="inline-flex items-center gap-0.5 rounded border border-dashed border-border px-1.5 py-0.5 text-xs text-muted-foreground hover:bg-muted disabled:opacity-50"
          title={
            whereComponent ? 'Add ORDER BY' : 'Add a WHERE predicate first — ORDER BY requires a matching component'
          }
        >
          <Plus className="h-3 w-3" />
          ORDER BY
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-0">
        <Command filter={humpFilter}>
          <Command.Input
            placeholder={`Pick a ${whereLabel} field (indexed only)…`}
            className="w-full border-b border-border bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
          />
          <Command.List className="max-h-60 overflow-auto p-1">
            {fields.length === 0 && (
              <Command.Empty className="px-3 py-2 text-xs text-muted-foreground">
                No indexed fields on {whereLabel}.
              </Command.Empty>
            )}
            {fields.map((f) => (
              <Command.Item
                key={f.name ?? ''}
                value={f.name ?? ''}
                onSelect={() => {
                  onChange({ component: whereComponent ?? '', field: f.name ?? '', descending: false });
                  setOpen(false);
                }}
                className="flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 font-mono text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
              >
                <span>{f.name}</span>
                <span className="shrink-0 text-xs text-muted-foreground">{f.typeName}</span>
              </Command.Item>
            ))}
          </Command.List>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
