import { useMemo } from 'react';
import { Globe } from 'lucide-react';
import { useRealmList } from '@/hooks/realms/useRealmList';
import { useRealmTelemetry } from '@/hooks/realms/useRealmTelemetry';
import { realmLabel, type Realm } from '@/hooks/realms/types';
import { readRealmBoard } from './realmBoardReading';
import { CardRow as Row, CardSection as Section } from './realmCardParts';

/**
 * The Inspector body for a selected realm (#1083) — "what IS realm 7", which is the question a scope-only design cannot
 * answer and the reason a realm is an object as well as a scope.
 *
 * **Both halves, from whichever sources this session has.** Identity and geometry come from the persisted catalog; run
 * state and this tick's spatial work come from the per-realm trace records. This card was written against the catalog
 * alone, so on a live session — where there is no catalog to read, because the running engine holds the file — every
 * realm answered "not in this session's catalog", with a guess at retirement on top. See `useRealmTelemetry` for why
 * the test is "has telemetry", never `kind === 'attach'`.
 *
 * **Identity is the pair `(id, generation)`, and it is shown as a pair.** Realm ids are reused across opens, so an id
 * alone does not name a world: a consumer keying on the id alone would carry a dead realm's state onto its successor
 * (12-realms § 1.1).
 */
export function RealmLeafCard({ id }: { id: number }) {
  const { list, catalog, hasRealms, hasCatalog, isLoading } = useRealmList();
  const { ticks, hasTelemetry } = useRealmTelemetry();

  const realm = list.find((r) => r.id === id);
  const board = useMemo(() => (hasTelemetry ? readRealmBoard(ticks) : null), [hasTelemetry, ticks]);
  const live = board?.rows.find((r) => r.realmId === id) ?? null;

  if (!realm && !live) {
    return <div className="p-3 text-sm text-muted-foreground">{absenceReason(id, { hasRealms, hasCatalog, hasTelemetry, isLoading, tick: board?.tickNumber ?? null })}</div>;
  }

  return (
    <div className="flex flex-col gap-3 p-3 text-sm">
      <header className="flex items-center gap-2">
        <Globe className="h-4 w-4 text-muted-foreground" />
        <span className="font-medium">{realm ? realmLabel(realm) : id === 0 ? 'Realm 0 — primary' : `Realm ${id}`}</span>
      </header>

      {realm && (
        <>
          <Section title="Identity">
            <Row label="Id" value={String(realm.id)} />
            <Row
              label="Generation"
              value={String(realm.generation)}
              hint="Realm ids are reused across opens, so identity is the pair (id, generation)."
            />
            <Row label="Catalog lifecycle" value={realm.lifecycle} hint="What the persisted catalog says this realm is, across opens." />
            <Row
              label="In the realm table"
              value={realm.registered ? 'yes' : 'no'}
              hint="Whether the engine holds it right now — a different question from the catalog's lifecycle."
            />
            <Row label="Read from" value={realm.source} />
          </Section>

          <Section title="Spatial identity">
            <Row
              label="Bounds"
              value={`${fmt(realm.grid.minX)}, ${fmt(realm.grid.minY)}${realm.grid.deep ? `, ${fmt(realm.grid.minZ)}` : ''}`
                + ` … ${fmt(realm.grid.maxX)}, ${fmt(realm.grid.maxY)}${realm.grid.deep ? `, ${fmt(realm.grid.maxZ)}` : ''}`}
              hint="Corners, not an extent: a realm is placed as well as sized, and an interior is not centred on the origin."
            />
            <Row label="Cell size" value={fmt(realm.grid.cellSize)} />
            <Row
              label="Cells"
              value={cellCounts(realm)}
              hint="One cell per realm is the guidance for an interior: cluster slack is O(cells × archetypes)."
            />
            <Row label="Dimensionality" value={realm.grid.deep ? '3D (deep)' : '2D (flat)'} />
            <Row
              label="Crossing hysteresis"
              value={`${(realm.grid.migrationHysteresisRatio * 100).toFixed(1)} % of a cell`}
              hint="Part of a realm's identity, because it decides when an entity changes cell."
            />
          </Section>
        </>
      )}

      {hasTelemetry && (
        <Section title={`State on tick ${board?.tickNumber?.toLocaleString() ?? '—'}`}>
          {live ? (
            <>
              <Row label="Run state" value={live.runStateName} />
              <Row
                label="Divisor"
                value={live.divisor === 1 ? 'every tick' : `once every ${live.divisor} ticks`}
                hint="How often policy lets this realm run. A realm nobody is watching is visited less often, or not at all."
              />
              <Row label="Cells" value={live.cellCount.toLocaleString()} />
              <Row
                label="Archetypes reporting"
                value={String(live.archetypes)}
                hint="One record per (realm, archetype) pair; this is how many were folded into this realm."
              />
              <Row
                label="Clusters"
                value={live.clusters.toLocaleString()}
                hint="Summed across those archetypes — a cluster belongs to the pair, not to the realm."
              />
              <Row label="Escaped clusters" value={live.escapedClusters.toLocaleString()} hint="Clusters whose extent escaped their cell." />
              <Row label="Promoted / blocked cells" value={`${live.promotedCells.toLocaleString()} / ${live.blockedCells.toLocaleString()}`} />
            </>
          ) : (
            // Silent is a state, not a gap. A dormant realm emits no telemetry at all — that is what makes it free.
            <p className="text-xs text-muted-foreground">
              Reported nothing on this tick. A dormant realm emits no telemetry at all, so this is what asleep looks
              like; it reappears on the tick something happens in it.
            </p>
          )}
        </Section>
      )}

      {!hasTelemetry && realm && (
        <Section title="Policy and state">
          {/* The reason, not four dashes. Policy is not persisted and the engine invents a default for a realm the
              application did not register, so there is nothing here to show and a specific thing to say instead. */}
          <p className="text-xs text-muted-foreground">{catalog?.liveStateReason}</p>
        </Section>
      )}

      {hasTelemetry && !realm && (
        <p className="text-xs text-muted-foreground">
          World bounds, generation and catalog lifecycle are not on the wire — open the database as a file for those.
        </p>
      )}
    </div>
  );
}

/** Say which source was missing, rather than speculating about retirement — the message that was reported as wrong. */
function absenceReason(
  id: number,
  ctx: { hasRealms: boolean; hasCatalog: boolean; hasTelemetry: boolean; isLoading: boolean; tick: number | null },
): string {
  if (ctx.isLoading) {
    return 'Reading the realm catalog…';
  }
  if (!ctx.hasRealms) {
    return `This database holds one world, so it has no realm ${id}.`;
  }
  if (ctx.hasCatalog) {
    return `Realm ${id} is not in this database’s realm catalog. It may have been retired, or belong to another database.`;
  }
  return `Realm ${id} reported nothing on tick ${ctx.tick?.toLocaleString() ?? '—'}. A dormant realm emits no telemetry at all,`
    + ' so this is what asleep looks like rather than a missing realm. Open the database as a file to see its identity'
    + ' and geometry while it sleeps.';
}

function fmt(n: number): string {
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

/**
 * Cells per axis, and the product.
 *
 * Recomputed the way the engine's own grid constructor does — `ceil(extent / cellSize)` per axis — rather than shown as
 * a bare extent, because the cell count is the figure that drives a realm's memory: cluster slack is one mostly-empty
 * page per occupied (cell, archetype) pair, so the same room costs 20 KB at one cell and ~300 KB at sixteen.
 */
function cellCounts(realm: Realm): string {
  const { grid } = realm;
  if (grid.cellSize <= 0) {
    return '—';
  }

  const per = (min: number, max: number) => Math.max(1, Math.ceil((max - min) / grid.cellSize));
  const x = per(grid.minX, grid.maxX);
  const y = per(grid.minY, grid.maxY);
  const z = grid.deep ? per(grid.minZ, grid.maxZ) : 1;
  const total = x * y * z;
  const shape = grid.deep ? `${x} × ${y} × ${z}` : `${x} × ${y}`;
  return total === 1 ? '1 (one cell)' : `${shape} = ${total.toLocaleString()}`;
}
