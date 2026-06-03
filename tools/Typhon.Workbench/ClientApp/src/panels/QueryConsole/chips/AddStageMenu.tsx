import { useState } from 'react';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';

/**
 * "⊕ Add stage" affordance — opens a popover with the set of stages that aren't yet on the spec. WHERE / ORDER BY
 * / SKIP / TAKE are single-instance; component-list stages (WITH / WITHOUT / EXCLUDE / ENABLED / DISABLED / SELECT)
 * are also single-row each (the row holds multiple chips). Picking a stage type calls `onPick`, which the parent uses
 * to render the relevant chip row.
 */
export type StageId =
  | 'WITH'
  | 'WITHOUT'
  | 'EXCLUDE'
  | 'ENABLED'
  | 'DISABLED'
  | 'WHERE'
  | 'SPATIAL'
  | 'SELECT'
  | 'ORDER BY'
  | 'SKIP'
  | 'TAKE';

export function AddStageMenu({
  available,
  onPick,
}: {
  available: readonly StageId[];
  onPick: (stage: StageId) => void;
}) {
  const [open, setOpen] = useState(false);
  if (available.length === 0) return null;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex items-center gap-0.5 rounded border border-dashed border-border px-2 py-1 text-xs text-muted-foreground hover:bg-muted"
        >
          <Plus className="h-3 w-3" />
          Add stage
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-48 p-1">
        <ul className="text-xs">
          {available.map((s) => (
            <li key={s}>
              <button
                type="button"
                onClick={() => {
                  onPick(s);
                  setOpen(false);
                }}
                className="block w-full rounded px-2 py-1 text-left hover:bg-muted"
              >
                {s}
              </button>
            </li>
          ))}
        </ul>
      </PopoverContent>
    </Popover>
  );
}
