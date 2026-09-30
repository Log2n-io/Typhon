import { useMemo } from 'react';
import { useRealmList } from '@/hooks/realms/useRealmList';
import { useRealmTelemetry } from '@/hooks/realms/useRealmTelemetry';
import { realmAxis, realmCoord, realmLabel, type Realm } from '@/hooks/realms/types';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { readRealmBoard } from './realmBoardReading';
import { mergeRealmRows, awakeCount, type RealmRow } from './realmRows';

/**
 * The Realms panel (#1083): which worlds this engine holds, and what each is doing.
 *
 * **One table over both sources, rather than one body per session kind.** A realm has two halves — identity and
 * geometry from the persisted catalog, run state and this tick's spatial work from the per-realm trace records — and a
 * session may hold either or both. Choosing a body by `kind === 'attach'` made them exclusive, which hid the live half
 * on the two session shapes that have both: a file with a capture attached, and a file whose holder is being watched.
 * See `useRealmTelemetry` for that table in full. Columns appear when their source does, so a plain file and a plain
 * attach each render exactly what they can answer, and a session with both renders the complete realm.
 *
 * **The live half is read from ONE tick, chosen by the global time scope.** Every field on a per-realm record is a
 * per-tick shape, so averaging would invent a state that never existed — a realm going dormant mid-window would read as
 * "Simulated 1.5". Narrowing the timeline therefore scrubs this panel through a capture, and an untouched timeline
 * tracks the head of the stream.
 */
export default function RealmsPanel() {
  const { list, catalog, hasRealms, hasCatalog, isLoading, error } = useRealmList();
  const { ticks, hasTelemetry } = useRealmTelemetry();
  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  const selectedId = leaf?.type === 'realm' ? (leaf.ref as number) : null;

  const board = useMemo(() => (hasTelemetry ? readRealmBoard(ticks) : null), [hasTelemetry, ticks]);
  const set = useMemo(() => mergeRealmRows(list, board), [list, board]);

  if (!hasRealms) {
    return <div className="p-4 text-sm text-muted-foreground">This database holds one world, so it has no realms to list.</div>;
  }
  if (error) {
    return <div className="p-4 text-sm text-destructive">Could not read the realm catalog: {String(error)}</div>;
  }
  if (isLoading && set.rows.length === 0) {
    return <div className="p-4 text-sm text-muted-foreground">Reading the realm catalog…</div>;
  }
  if (set.rows.length === 0) {
    return (
      <div className="p-4 text-sm text-muted-foreground">
        No realm records in this window. Per-realm telemetry needs the spatial trace subtree enabled on the engine, and
        a realm reports only while it is runnable.
      </div>
    );
  }

  const awake = awakeCount(set);

  return (
    <div className="flex h-full flex-col overflow-hidden text-sm">
      <div className="flex items-baseline justify-between gap-2 border-b px-3 py-2">
        <span className="font-medium">
          {set.rows.length} {set.rows.length === 1 ? 'realm' : 'realms'}
          {board && <span className="ml-2 font-normal text-muted-foreground">{awake} awake</span>}
        </span>
        <span className="text-fs-2xs tabular-nums text-muted-foreground">
          {/* The dormant count is the claim a realm engine makes: a realm nobody is in emits nothing and costs nothing.
              It is only printed when telemetry is the ONLY source — with a catalog the list already shows them all. */}
          {set.dormantRealms != null && set.dormantRealms > 0 && <>{set.dormantRealms.toLocaleString()} dormant · </>}
          {set.presentRealms != null && <>{set.presentRealms.toLocaleString()} registered · </>}
          {hasCatalog && catalog ? `ids 0…${(catalog.maxRealms ?? 1) - 1}` : ''}
          {/* The tick only means something when a live half was read. On a plain file the banner below already says
              why there is none, so repeating it here would be the same fact twice in two wordings. */}
          {set.tickNumber != null && `${hasCatalog ? ' · ' : ''}tick ${set.tickNumber.toLocaleString()}`}
        </span>
      </div>

      {hasCatalog && catalog && !catalog.liveState && !hasTelemetry && (
        // Once, at the top. A per-row "—" with a tooltip would ask the reader to discover the same fact per realm.
        <p className="border-b bg-muted/40 px-3 py-2 text-xs text-muted-foreground">{catalog.liveStateReason}</p>
      )}

      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full border-collapse">
          <thead className="sticky top-0 bg-background text-fs-2xs uppercase text-muted-foreground">
            <tr>
              <Th className="text-left">Realm</Th>
              {hasTelemetry && <Th className="text-left">State</Th>}
              {hasTelemetry && <Th title="1 = every tick; N = once every N ticks">Divisor</Th>}
              {hasCatalog && <Th title="World bounds along X, as min … max">X</Th>}
              {hasCatalog && <Th title="World bounds along Y, as min … max">Y</Th>}
              {hasCatalog && <Th title="World bounds along Z. A flat realm is exactly one cell deep, never zero">Z</Th>}
              <Th title="This realm's own spatial cell edge">Cell</Th>
              <Th>Cells</Th>
              {hasTelemetry && <Th title="Archetypes that reported this realm on this tick">Arch</Th>}
              {hasTelemetry && <Th>Clusters</Th>}
              {hasTelemetry && <Th title="Clusters whose extent escaped their cell">Escaped</Th>}
              {hasCatalog && <Th className="text-left" title="Bootstrap grid record, persisted catalog, or the live realm table">Source</Th>}
            </tr>
          </thead>
          <tbody>
            {set.rows.map((row) => (
              <Row
                key={row.id}
                row={row}
                showLive={hasTelemetry}
                showCatalog={hasCatalog}
                selected={row.id === selectedId}
                onSelect={() => select('realm', row.id)}
              />
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

function Row({
  row,
  showLive,
  showCatalog,
  selected,
  onSelect,
}: {
  row: RealmRow;
  showLive: boolean;
  showCatalog: boolean;
  selected: boolean;
  onSelect: () => void;
}) {
  const g = row.catalog?.grid;
  // Cell size and cell count come from either side; the catalog's is authoritative when present, since a registration
  // whose grid contradicts the catalog is refused at open (RLM-01) and the two therefore agree by construction.
  const cellSize = g?.cellSize ?? row.live?.cellSize ?? null;
  const cellCount = row.live?.cellCount ?? (g ? cellsOf(row.catalog!) : null);

  return (
    <tr
      onClick={onSelect}
      aria-current={selected ? 'true' : undefined}
      className={'cursor-default border-b border-border/40 tabular-nums hover:bg-accent/60 ' + (selected ? 'bg-accent' : '')}
    >
      <td className="max-w-56 truncate px-2 py-1 text-left" title={row.catalog ? realmLabel(row.catalog) : undefined}>
        {row.catalog ? realmLabel(row.catalog) : row.id === 0 ? 'Realm 0 — primary' : `Realm ${row.id}`}
      </td>

      {showLive && (
        <td className="px-2 py-1 text-left">
          {row.live ? (
            <span className={row.live.runState === 0 ? 'text-muted-foreground' : ''}>{row.live.runStateName}</span>
          ) : (
            // Registered, and silent. A dormant realm emits no telemetry at all — that is what makes it free — so the
            // absence of a row IS the state, not a gap in it.
            <span className="text-muted-foreground" title="Registered, but reported nothing on this tick — a dormant realm emits no telemetry.">
              dormant
            </span>
          )}
        </td>
      )}
      {showLive && <td className="px-2 py-1 text-right">{row.live ? (row.live.divisor === 1 ? '—' : `÷${row.live.divisor}`) : ''}</td>}

      {showCatalog && <td className="whitespace-nowrap px-2 py-1 text-right">{g ? realmAxis(g.minX, g.maxX) : ''}</td>}
      {showCatalog && <td className="whitespace-nowrap px-2 py-1 text-right">{g ? realmAxis(g.minY, g.maxY) : ''}</td>}
      {showCatalog && (
        <td className="whitespace-nowrap px-2 py-1 text-right">
          {g ? realmAxis(g.minZ, g.maxZ) : ''}
          {g?.deep && <span className="ml-1 text-muted-foreground">3D</span>}
        </td>
      )}

      <td className="px-2 py-1 text-right">{cellSize == null ? '' : realmCoord(cellSize)}</td>
      <td className="px-2 py-1 text-right">{cellCount == null ? '' : cellCount.toLocaleString()}</td>

      {showLive && <td className="px-2 py-1 text-right">{row.live?.archetypes ?? ''}</td>}
      {showLive && <td className="px-2 py-1 text-right">{row.live?.clusters.toLocaleString() ?? ''}</td>}
      {showLive && (
        <td className={'px-2 py-1 text-right ' + (row.live && row.live.escapedClusters > 0 ? 'text-amber-600 dark:text-amber-400' : '')}>
          {row.live?.escapedClusters.toLocaleString() ?? ''}
        </td>
      )}

      {showCatalog && <td className="px-2 py-1 text-left text-muted-foreground">{row.catalog?.source ?? 'live'}</td>}
    </tr>
  );
}

/** Cells per axis, the way the engine's own grid constructor computes them: ceil(extent / cellSize) per axis. */
function cellsOf(realm: Realm): number | null {
  const { grid } = realm;
  if (grid.cellSize <= 0) {
    return null;
  }
  const per = (min: number, max: number) => Math.max(1, Math.ceil((max - min) / grid.cellSize));
  return per(grid.minX, grid.maxX) * per(grid.minY, grid.maxY) * (grid.deep ? per(grid.minZ, grid.maxZ) : 1);
}
