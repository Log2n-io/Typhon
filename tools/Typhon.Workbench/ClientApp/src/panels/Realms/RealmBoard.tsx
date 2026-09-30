import { useMemo } from 'react';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { readRealmBoard, type RealmBoardRow } from './realmBoardReading';

/**
 * The live realm board (#1083 rung 6) — which realms are awake right now, and how many are not.
 *
 * **Dormant realms are counted, not listed, and that is the engine's shape rather than a UI choice.** A dormant realm
 * emits no telemetry at all: that is the whole claim of RLM-B, measured at 0.20 µs per *active* realm in PRV-04. So the
 * board can show the runnable ones in full and must state the rest as a number — listing 1 185 rows of nothing would
 * contradict the very property being demonstrated.
 *
 * **Shape, not work.** Migrations, repair units and budget spent are owned per archetype in the engine — one set of
 * counters serves every realm the archetype lives in — so a per-realm column for them would be the sum across realms
 * under one realm's name. They stay on the Spatial Maintenance panel, which is archetype-wide by construction.
 */
export default function RealmBoard() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);
  const { windowedTicks } = useLiveGaugeData(sessionKind === 'attach' ? sessionId : null);
  const board = useMemo(() => readRealmBoard(windowedTicks), [windowedTicks]);

  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  const selectedId = leaf?.type === 'realm' ? (leaf.ref as number) : null;

  if (board.tickNumber == null) {
    return (
      <div className="p-4 text-sm text-muted-foreground">
        No realm telemetry in this window. Per-realm records need the spatial trace subtree enabled on the engine, and a
        realm reports only while it is runnable.
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col overflow-hidden text-sm">
      <div className="flex items-baseline justify-between gap-2 border-b px-3 py-2">
        <span className="font-medium">
          {board.rows.length} runnable {board.rows.length === 1 ? 'realm' : 'realms'}
        </span>
        <span className="text-fs-2xs tabular-nums text-muted-foreground">
          {/* The count is the claim: a realm nobody is in emits nothing and costs nothing. */}
          {board.dormantRealms > 0 && <>{board.dormantRealms.toLocaleString()} dormant · </>}
          {board.presentRealms.toLocaleString()} registered · tick {board.tickNumber.toLocaleString()}
        </span>
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full border-collapse tabular-nums">
          <thead className="sticky top-0 bg-background text-fs-2xs uppercase text-muted-foreground">
            <tr>
              <Th className="text-left">Realm</Th>
              <Th className="text-left">State</Th>
              <Th title="1 = every tick; N = once every N ticks">Divisor</Th>
              <Th title="This realm's own cell edge">Cell</Th>
              <Th>Cells</Th>
              <Th title="Archetypes that reported a row for this realm on this tick">Arch</Th>
              <Th>Clusters</Th>
              <Th title="Clusters whose extent escaped their cell">Escaped</Th>
              <Th title="Cells promoted to the cell-tree, and cells blocked from it">Promoted / blocked</Th>
            </tr>
          </thead>
          <tbody>
            {board.rows.map((row) => (
              <Row key={row.realmId} row={row} selected={row.realmId === selectedId} onSelect={() => select('realm', row.realmId)} />
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Th({ children, className, title }: { children: React.ReactNode; className?: string; title?: string }) {
  return (
    <th className={'px-2 py-1 font-medium ' + (className ?? 'text-right')} title={title}>
      {children}
    </th>
  );
}

function Row({ row, selected, onSelect }: { row: RealmBoardRow; selected: boolean; onSelect: () => void }) {
  return (
    <tr
      onClick={onSelect}
      aria-current={selected ? 'true' : undefined}
      className={'cursor-default border-b border-border/40 hover:bg-accent/50 ' + (selected ? 'bg-accent' : '')}
    >
      <td className="px-2 py-1 text-left">{row.realmId === 0 ? '0 — primary' : row.realmId}</td>
      <td className="px-2 py-1 text-left">
        <span className={row.runState === 0 ? 'text-muted-foreground' : ''}>{row.runStateName}</span>
      </td>
      <td className="px-2 py-1 text-right">{row.divisor === 1 ? '—' : `÷${row.divisor}`}</td>
      <td className="px-2 py-1 text-right">{fmt(row.cellSize)}</td>
      <td className="px-2 py-1 text-right">
        {row.cellCount.toLocaleString()}
        {row.gridDepth > 1 && <span className="text-muted-foreground"> 3D</span>}
      </td>
      <td className="px-2 py-1 text-right">{row.archetypes}</td>
      <td className="px-2 py-1 text-right">{row.clusters.toLocaleString()}</td>
      <td className={'px-2 py-1 text-right ' + (row.escapedClusters > 0 ? 'text-amber-600 dark:text-amber-400' : '')}>
        {row.escapedClusters.toLocaleString()}
      </td>
      <td className="px-2 py-1 text-right">
        {row.promotedCells.toLocaleString()} / {row.blockedCells.toLocaleString()}
      </td>
    </tr>
  );
}

function fmt(n: number): string {
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}
