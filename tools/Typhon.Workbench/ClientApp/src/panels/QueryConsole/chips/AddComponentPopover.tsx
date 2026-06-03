import { useState } from 'react';
import { Command } from 'cmdk';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { humpFilter } from '@/shell/camelHumpFilter';
import { useArchetypeComponents } from '@/hooks/queryConsole/useArchetypeComponents';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';

/**
 * "⊕ add component" popover used by WITH / WITHOUT / EXCLUDE / ENABLED / DISABLED chip rows. Fetches the
 * archetype's component list (via {@link useArchetypeComponents}) so the user picks from what's actually possible
 * — no free typing, no chance of typing a name the engine doesn't recognise.
 *
 * `excluded` hides components already on the same stage (a chip can't be added twice). The picker stays open after
 * a pick so the user can add several without re-clicking — Escape or click-outside closes.
 */
export function AddComponentPopover({
  archetypeRef,
  excluded,
  onAdd,
  label,
}: {
  archetypeRef: string | null | undefined;
  excluded: string[];
  onAdd: (typeName: string) => void;
  label: string;
}) {
  const [open, setOpen] = useState(false);
  const { components, isLoading } = useArchetypeComponents(archetypeRef);
  const { label: nameLabel } = useComponentNames();
  const skip = new Set(excluded);
  const items = components.filter((c) => !skip.has(c.typeName));

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={!archetypeRef}
          className="inline-flex items-center gap-0.5 rounded border border-dashed border-border px-1.5 py-0.5 text-xs text-muted-foreground hover:bg-muted disabled:opacity-50"
          title={archetypeRef ? `Add to ${label}` : 'Pick an archetype first'}
        >
          <Plus className="h-3 w-3" />
          add
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-0">
        <Command filter={humpFilter}>
          <Command.Input
            placeholder={`Add to ${label}…`}
            className="w-full border-b border-border bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
          />
          <Command.List className="max-h-72 overflow-auto p-1">
            {isLoading && <div className="px-3 py-2 text-xs text-muted-foreground">Loading…</div>}
            {!isLoading && items.length === 0 && (
              <Command.Empty className="px-3 py-2 text-xs text-muted-foreground">
                {archetypeRef
                  ? excluded.length > 0
                    ? 'All matching components already added.'
                    : 'No components match (or archetype FROM uses a CLR name — pick by id for schema-driven choices).'
                  : 'Pick an archetype first.'}
              </Command.Empty>
            )}
            {items.map((c) => (
              <Command.Item
                key={c.typeName}
                value={c.typeName}
                keywords={[nameLabel(c.typeName), `${c.fullName} ${c.typeName}`]}
                onSelect={() => {
                  onAdd(c.typeName);
                  // stay open so the user can chain adds
                }}
                className="flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
              >
                <span className="truncate font-mono" title={c.typeName}>
                  {nameLabel(c.typeName)}
                </span>
                <span className="shrink-0 text-xs text-muted-foreground">
                  {c.indexCount > 0 ? `${c.indexCount} idx` : '—'}
                </span>
              </Command.Item>
            ))}
          </Command.List>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
