import { useState } from 'react';
import { Command } from 'cmdk';
import { ChevronDown } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { humpFilter } from '@/shell/camelHumpFilter';

/**
 * The persistent header target-switcher for a deep inspector (PC-9, part 1). A searchable picker over every
 * valid target (archetypes / components) that re-drives the panel via `onPick` (which writes the bus). Built
 * on the vetted cmdk + Radix Popover primitives — type-ahead, roving and `Esc` come for free (PC-8), no
 * hand-rolled focus management. Reuses the palette's {@link humpFilter} so search matches word initials too.
 */

export interface SwitcherItem {
  id: string;
  /** Primary label (matched + displayed), e.g. "#2002" or "Position". */
  label: string;
  /** Secondary meta shown dimmed, e.g. "980 ent · 41%" or "8B · used in 7". */
  meta?: string;
  /** Extra keyword string for the substring fallback (FQN / composition). */
  keywords?: string;
}

interface Props {
  /** Dimmed prefix naming what the combo selects, e.g. "Archetype" / "Component" — makes it identifiable. */
  label: string;
  /** What the header currently shows — the already-resolved target's label. */
  currentLabel: string;
  /** Optional tooltip on the trigger (e.g. the component FQN, the archetype composition). */
  currentTitle?: string;
  /** True when the current target was auto-resolved → render the "(auto)" chip. */
  auto: boolean;
  /** Tooltip explaining the heuristic behind an auto pick. */
  autoTitle?: string;
  items: SwitcherItem[];
  onPick: (id: string) => void;
  /** testid prefix + accessible noun, e.g. "archetype" / "component". */
  testId: string;
  noun: string;
}

export default function InspectorTargetSwitcher({
  label,
  currentLabel,
  currentTitle,
  auto,
  autoTitle,
  items,
  onPick,
  testId,
  noun,
}: Props) {
  const [open, setOpen] = useState(false);
  return (
    <div className="flex items-center gap-1.5">
      <span className="shrink-0 text-[11px] text-muted-foreground">{label}</span>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <button
            type="button"
            data-testid={`${testId}-switcher`}
            aria-label={`Switch ${noun}`}
            title={currentTitle ?? `Switch ${noun}`}
            className="flex items-center gap-1 rounded border border-border bg-muted/40 px-1.5 py-0.5 font-mono text-[12px] text-foreground hover:bg-muted"
          >
            {currentLabel}
            <ChevronDown className="h-3 w-3 text-muted-foreground" />
          </button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-72 p-0">
          <Command filter={humpFilter}>
            <Command.Input
              data-testid={`${testId}-switcher-input`}
              placeholder={`Switch ${noun}…`}
              className="w-full border-b border-border bg-transparent px-3 py-2 text-[12px] outline-none placeholder:text-muted-foreground"
            />
            <Command.List className="max-h-72 overflow-auto p-1">
              <Command.Empty className="px-3 py-2 text-[11px] text-muted-foreground">No {noun} matches.</Command.Empty>
              {items.map((it) => (
                <Command.Item
                  key={it.id}
                  value={it.id}
                  keywords={[it.label, it.keywords ?? it.meta ?? '']}
                  onSelect={() => {
                    onPick(it.id);
                    setOpen(false);
                  }}
                  data-testid={`${testId}-switcher-item`}
                  data-id={it.id}
                  className="flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 text-[12px] aria-selected:bg-accent aria-selected:text-accent-foreground"
                >
                  <span className="truncate font-mono">{it.label}</span>
                  {it.meta && <span className="shrink-0 text-[11px] text-muted-foreground">{it.meta}</span>}
                </Command.Item>
              ))}
            </Command.List>
          </Command>
        </PopoverContent>
      </Popover>
      {auto && (
        <span
          data-testid={`${testId}-auto-chip`}
          title={autoTitle ?? 'Auto-selected — pick another above.'}
          className="rounded bg-muted px-1 text-[10px] uppercase tracking-wide text-muted-foreground"
        >
          auto
        </span>
      )}
    </div>
  );
}
