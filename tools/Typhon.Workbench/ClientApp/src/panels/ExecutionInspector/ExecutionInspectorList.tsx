import { useEffect, useRef } from 'react';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { toNumber } from '../QueryCatalog/numeric';
import { formatNs } from './phaseRows';
import { useExecutionInspectorStore } from './useExecutionInspectorStore';

interface Props {
  executions: QueryExecutionDto[];
  systemNames: Map<number, string>;
}

/**
 * Sidebar list of executions for the focused definition. Each row shows (tick, system, wall-time)
 * and is clickable to set the {@link useExecutionInspectorStore.selected} pointer. The currently
 * selected row is highlighted; clicking it again is a no-op.
 */
export function ExecutionInspectorList({ executions, systemNames }: Props) {
  const selected = useExecutionInspectorStore((s) => s.selected);
  const setSelected = useExecutionInspectorStore((s) => s.setSelected);
  const selectedRef = useRef<HTMLButtonElement | null>(null);

  // Scroll the highlighted row into the visible portion of the sidebar whenever the selection changes —
  // critical for external hand-offs (e.g. the profiler "Inspect query execution" button jumping to tick
  // 273 in a 700-row list). 'nearest' avoids forcing the row to the top when it's already on screen.
  useEffect(() => {
    if (selectedRef.current) {
      selectedRef.current.scrollIntoView({ block: 'nearest', behavior: 'auto' });
    }
  }, [selected]);

  if (executions.length === 0) {
    return (
      <div className="flex h-full items-center justify-center bg-muted/10 text-fs-sm text-muted-foreground">
        No executions recorded.
      </div>
    );
  }

  return (
    <div className="h-full w-[220px] shrink-0 overflow-y-auto border-r border-border bg-card text-fs-base">
      <div className="border-b border-border bg-muted/30 px-2 py-1 text-fs-xs uppercase tracking-wider text-muted-foreground">
        Executions ({executions.length})
      </div>
      <ul className="divide-y divide-border/50">
        {executions.map((e) => {
          const tickIndex = toNumber(e.tickIndex);
          const systemId = toNumber(e.systemId);
          const startTs = toNumber(e.startTs);
          const endTs = toNumber(e.endTs);
          const isSelected = selected?.tickIndex === tickIndex && selected.systemId === systemId;
          const systemLabel = systemId < 0
            ? '<unattributed>'
            : (systemNames.get(systemId) ?? `System[${systemId}]`);
          return (
            <li key={`${tickIndex}-${systemId}`}>
              <button
                ref={isSelected ? selectedRef : undefined}
                type="button"
                onClick={() => setSelected({ tickIndex, systemId })}
                className={`flex w-full flex-col gap-0.5 px-2 py-1.5 text-left hover:bg-accent ${isSelected ? 'bg-accent/60' : ''}`}
                data-testid="execution-list-row"
                data-tick-index={tickIndex}
                data-system-id={systemId}
                aria-selected={isSelected}
              >
                <span className="font-mono text-fs-sm text-foreground">tick {tickIndex.toLocaleString()}</span>
                <span className="truncate text-fs-sm text-muted-foreground">{systemLabel}</span>
                <span className="font-mono text-fs-xs text-muted-foreground">{formatNs(Math.max(0, endTs - startTs))}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
