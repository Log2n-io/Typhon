import { useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import type { LossEstimate } from './integrityModel';

/**
 * The complete enumeration of what a repair destroys.
 *
 * **This list is the consent.** A dialog reading *"47 entities will be affected — OK?"* is not consent; it
 * is a number standing in for the thing the operator is supposed to be agreeing to. So every entry the
 * server sent is rendered — never a summary, never a "first 100 shown", never a count.
 *
 * It is virtualized rather than truncated precisely to keep that literal. A manifest can run to thousands
 * of rows, and the alternative to windowing is a cap — which would turn "here is everything you are about
 * to lose" into "here is some of it", quietly, exactly where quiet is most expensive. The row count is
 * printed in the header so a reader can verify nothing was dropped between the server and the screen.
 */
export default function LossManifest({ entries }: { entries: LossEstimate[] }) {
  const parentRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: entries.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 40,
    overscan: 16,
  });

  if (entries.length === 0) {
    return (
      <p className="px-3 py-2 text-fs-base text-muted-foreground" data-testid="integrity-loss-empty">
        This repair destroys nothing. Every step regenerates a derived structure from data that survives.
      </p>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-baseline gap-2 border-b border-border px-3 py-1.5">
        <span className="text-fs-base font-medium text-destructive">What this repair destroys</span>
        <span className="text-fs-sm tabular-nums text-muted-foreground" data-testid="integrity-loss-count">
          {entries.length.toLocaleString()} {entries.length === 1 ? 'entry' : 'entries'}
        </span>
        <span className="text-fs-sm text-muted-foreground">— all of them listed, none summarised</span>
      </div>

      <div ref={parentRef} className="min-h-0 flex-1 overflow-auto" data-testid="integrity-loss-manifest">
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
          {virtualizer.getVirtualItems().map((row) => {
            const e = entries[row.index];
            return (
              <div
                key={row.key}
                data-testid="integrity-loss-row"
                className="absolute left-0 top-0 w-full border-b border-border/50 px-3 py-1"
                style={{ height: row.size, transform: `translateY(${row.start}px)` }}
              >
                <div className="flex items-baseline gap-2 text-fs-base">
                  <span className="tabular-nums text-destructive">{e.count}</span>
                  <span className="text-foreground">{e.kind}</span>
                  {e.component && <span className="font-mono text-fs-sm text-muted-foreground">{e.component}</span>}
                  {e.archetype && <span className="font-mono text-fs-sm text-muted-foreground">{e.archetype}</span>}
                </div>
                {e.explanation && <p className="truncate text-fs-sm text-muted-foreground" title={e.explanation}>{e.explanation}</p>}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
