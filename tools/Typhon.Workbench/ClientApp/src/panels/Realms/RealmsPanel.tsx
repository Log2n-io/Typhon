import { useMemo } from 'react';
import { useRealmList } from '@/hooks/realms/useRealmList';
import { useRealmTelemetry } from '@/hooks/realms/useRealmTelemetry';
import { realmAxis, realmCoord, realmLabel, type Realm } from '@/hooks/realms/types';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { readRealmBoard } from './realmBoardReading';
import { readRealmRates } from './realmRatesReading';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import { useTelemetrySession } from '@/hooks/profiler/useTelemetrySession';
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
  // A SECOND clock, and a BOUNDED one. The board samples one tick because a realm's shape is a fact about an instant;
  // rates reset every tick, so a single sample is not a rate and only a window is one (SO-01).
  //
  // `windowedTicks`, NOT `ticks`. `useRealmTelemetry` hands back every tick in scope, which while following the head is
  // the whole session — so the divisor would grow without bound and a realm's migrations per second would FALL for as
  // long as the session ran, comparable to nothing, least of all to the same realm ten minutes earlier. This is the
  // same 60-second window every other rate on the surface is quoted over.
  const { sessionId } = useTelemetrySession();
  const { windowedTicks } = useLiveGaugeData(sessionId);
  const rates = useMemo(() => (hasTelemetry ? readRealmRates(windowedTicks) : null), [hasTelemetry, windowedTicks]);
  const set = useMemo(() => mergeRealmRows(list, board, rates), [list, board, rates]);
  // The rate columns appear only when some realm actually reported work: a column of dashes over 1 237 realms is a
  // column asserting it could have answered, and the banner already says why it cannot.
  const showRates = set.rateWindowMs != null;
  // How many columns the one-tick half occupies, so the spanning header above them stays right when a column is added.
  const shapeColumnCount = 1 + (hasTelemetry ? 2 : 0) + (hasCatalog ? 3 : 0) + 2 + (hasTelemetry ? 3 : 0);
  const rateWindowLabel = set.rateWindowMs == null
    ? ''
    : `${(set.rateWindowMs / 1000).toLocaleString(undefined, { maximumFractionDigits: 0 })} s · ${(rates?.windowTicks ?? 0).toLocaleString()} ticks`;

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

      {set.ratesTruncated && (
        // The engine caps rate rows per archetype per tick, and truncates a PREFIX of present order rather than a
        // rotating sample — so the realms missing here are the same ones on every tick. Without this line the table
        // reads as "the realms that worked" when it is only some of them.
        <p className="border-b bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-400">
          More realms did work than the engine sends rate rows for, so the rates below are a subset — and the same
          realms are omitted on every tick. Narrow the time scope, or read one realm at a time.
        </p>
      )}

      {hasCatalog && catalog && !catalog.liveState && !hasTelemetry && (
        // Once, at the top. A per-row "—" with a tooltip would ask the reader to discover the same fact per realm.
        <p className="border-b bg-muted/40 px-3 py-2 text-xs text-muted-foreground">{catalog.liveStateReason}</p>
      )}

      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full border-collapse">
          <thead className="sticky top-0 bg-background text-fs-2xs uppercase text-muted-foreground">
            {showRates && (
              // TWO CLOCKS, said out loud. Everything left of the rate group is one tick; the rate group is a window.
              // SO-01's opening clause is that this surface carries both kinds, and a table that mixes them without
              // saying which is which is how a reader comes to believe the average describes the tick they are on.
              <tr className="border-b border-border/40">
                <th className="px-2 py-1 text-left font-medium normal-case" colSpan={shapeColumnCount}>
                  <span className="text-muted-foreground">this tick</span>
                </th>
                <th className="px-2 py-1 text-right font-medium normal-case" colSpan={4}>
                  <span className="text-muted-foreground">per second, over {rateWindowLabel}</span>
                </th>
                {hasCatalog && <th />}
              </tr>
            )}
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
              {showRates && <Th title="Entities this realm moved between cells, per second of wall clock over the window">Migr/s</Th>}
              {showRates && <Th title="Entities this realm's drift scan found out of place, per second of wall clock over the window">Drift/s</Th>}
              {showRates && <Th title="Budget this realm's relocations were charged, in milliseconds per second of wall clock">Budget ms/s</Th>}
              {showRates && (
                <Th title="Ticks of the window this realm reported on. The rates divide by the WHOLE window, so a realm busy for a tenth of it reads at a tenth of its working rate — this is what says which.">
                  Busy
                </Th>
              )}
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
                showRates={showRates}
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
  showRates,
  selected,
  onSelect,
}: {
  row: RealmRow;
  showLive: boolean;
  showCatalog: boolean;
  showRates: boolean;
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


      {showRates && <td className="px-2 py-1 text-right">{rateCell(row.rates?.migrationsPerSec)}</td>}
      {showRates && <td className="px-2 py-1 text-right">{rateCell(row.rates?.driftersPerSec)}</td>}
      {showRates && <td className="px-2 py-1 text-right">{rateCell(row.rates?.budgetMsPerSec, 2)}</td>}
      {showRates && (
        <td className="px-2 py-1 text-right text-muted-foreground">
          {row.rates ? `${row.rates.ticksReporting}/${row.rates.windowTicks}` : <span title="Did no work in this window — the engine sends nothing for a realm it did not touch, so this is a measured silence, not a zero.">—</span>}
        </td>
      )}

      {showCatalog && <td className="px-2 py-1 text-left text-muted-foreground">{row.catalog?.source ?? 'live'}</td>}
    </tr>
  );
}

/**
 * A per-second rate, or an em dash when the realm did no work in the window.
 *
 * <b>The dash is not decoration.</b> The engine emits nothing for a realm it did not touch, so an absent row means
 * "measured, and it did nothing" — which a zero would also say, while additionally claiming the realm was measured and
 * found idle on every tick. SO-01 reserves zero for a measured zero; absence is its own reading.
 */
function rateCell(value: number | undefined, digits = 1): React.ReactNode {
  if (value === undefined) {
    return <span className="text-muted-foreground" title="Did no work in this window — a realm the fence did not touch sends no record at all.">—</span>;
  }
  return value.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
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
