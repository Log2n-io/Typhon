import { useMemo, useState } from 'react';
import { Command } from 'cmdk';
import { ChevronDown } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { useSessionStore } from '@/stores/useSessionStore';
import { useGetApiSessionsSessionIdSchemaArchetypes } from '@/api/generated/schema/schema';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import { useArchetypeNames } from '@/hooks/queryConsole/useArchetypeNames';
import { humpFilter } from '@/shell/camelHumpFilter';

/**
 * Editable combobox for the FROM clause's archetype reference (#386 Phase 1 polish — replaces the plain `<input>`
 * that required users to know the exact name). Fetches the live archetype list from `/api/sessions/{id}/schema/archetypes`
 * and renders each as `#<id> · <component summary>` — matches what the Workbench's schema browser shows so users
 * can find what they're looking for. Free typing is preserved (the underlying field is still a text input), so users
 * can paste a name the dropdown doesn't surface (legacy CLR class names, e.g.).
 *
 * Mirrors the {@link ../schemaCommon/InspectorTargetSwitcher} pattern (cmdk + Radix Popover + humpFilter) so the
 * search behaviour is identical to the rest of the Workbench.
 */
interface Props {
  value: string;
  onChange: (next: string) => void;
}

export function FromArchetypeCombobox({ value, onChange }: Props) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState(false);
  const query = useGetApiSessionsSessionIdSchemaArchetypes(sessionId ?? '', {
    query: { enabled: !!sessionId, staleTime: 30_000 },
  });
  const { label: nameLabel } = useComponentNames();
  const { label: archName } = useArchetypeNames();

  // Build the picker items from the server's archetype list. Each item is keyed by the canonical "#<id>" form so
  // selecting an entry feeds the parser's preferred shape directly. Component summaries use the same smart label
  // as the rest of the console — `componentTypes` are CLR full names, which `nameLabel` resolves to their short form.
  const items = useMemo(() => {
    const archetypes = query.data?.data ?? [];
    return archetypes.map((a) => {
      const id = a.archetypeId ?? '?';
      const ref = `#${id}`;
      const comps = (a.componentTypes ?? []).map((t) => nameLabel(t)).slice(0, 3);
      const more = (a.componentTypes?.length ?? 0) > 3 ? ` +${a.componentTypes!.length - 3} more` : '';
      const arch = archName(ref);
      // Primary label is the smart archetype name (falls back to the bare ref when no name is known); the id and
      // component summary form the secondary line, so the user reads "PlayerArch" first, "#823 · …" second.
      return {
        id: ref,
        archLabel: arch === ref ? ref : arch,
        meta: `${ref} · ${comps.join(', ')}${more} · ${a.entityCount ?? 0} ent`,
        keywords: `${ref} ${id} ${(a.componentTypes ?? []).join(' ')}`,
      };
    });
  }, [query.data, nameLabel, archName]);

  return (
    <div className="flex flex-1 items-center gap-1">
      {/* Editable text input — accepts free typing for legacy CLR-name references and pasted shareable URLs. */}
      <input
        className="flex-1 rounded border border-border bg-background px-2 py-1 font-mono"
        // Idle: show the smart archetype name when the value resolves to a known archetype. Focused: reveal the raw
        // stored ref (`#823`) so what the user edits is exactly what the parser receives.
        value={editing ? value : archName(value) || value}
        onFocus={() => setEditing(true)}
        onBlur={() => setEditing(false)}
        onChange={(e) => onChange(e.target.value)}
        placeholder="#2001 or ArchetypeName"
      />
      {/* Popover with cmdk-driven picker. The chevron is its only trigger so the input remains a normal text field. */}
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <button
            type="button"
            className="rounded border border-border bg-muted/40 px-1.5 py-1 text-xs text-muted-foreground hover:bg-muted"
            title="Pick from registered archetypes"
            aria-label="Open archetype picker"
          >
            <ChevronDown className="h-3 w-3" />
          </button>
        </PopoverTrigger>
        <PopoverContent align="end" className="w-80 p-0">
          <Command filter={humpFilter}>
            <Command.Input
              placeholder="Search archetypes (id, components)…"
              className="w-full border-b border-border bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
            />
            <Command.List className="max-h-72 overflow-auto p-1">
              {query.isLoading && <div className="px-3 py-2 text-xs text-muted-foreground">Loading…</div>}
              {!query.isLoading && items.length === 0 && (
                <Command.Empty className="px-3 py-2 text-xs text-muted-foreground">
                  No archetypes registered.
                </Command.Empty>
              )}
              {items.map((it) => (
                <Command.Item
                  key={it.id}
                  value={it.id}
                  keywords={[it.archLabel, it.keywords]}
                  onSelect={() => {
                    onChange(it.id);
                    setOpen(false);
                    setEditing(false);
                  }}
                  className="flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
                >
                  <span className="shrink-0 font-mono">{it.archLabel}</span>
                  <span className="truncate text-xs text-muted-foreground">{it.meta}</span>
                </Command.Item>
              ))}
            </Command.List>
          </Command>
        </PopoverContent>
      </Popover>
    </div>
  );
}
