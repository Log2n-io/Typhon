/**
 * What every realm is doing right now, as the server reports it at `/typhon/realms.json` (CLI3D-11).
 *
 * <b>This is the dormancy proof, and the row policy is the proof rather than a size limit.</b> "An unobserved realm
 * costs zero" is the load-bearing claim of the realms design and there was nowhere to see it. Planets, space and live
 * dungeons always have a row; an interior has one only while it is awake, which is exactly while somebody is in it. A
 * row appearing when a bot walks through a door and falling off the list once the room has been empty for the sleep
 * delay IS the demonstration. Everything else is `counts.dormant`.
 *
 * Separate from {@link ServerConfig}, which is what the process was STARTED with — fetched once and immutable for the
 * run. This one is polled.
 */

import { labelForAppTag } from './realm-view';

/** What a realm's policy decided it is doing this tick. The four names the engine's own `RealmRunState` uses. */
export type RealmRunState = 'active' | 'simulated' | 'dormant' | 'closing';

/** One realm's line in the inventory. */
export interface RealmInventoryRow {
  readonly id: number;
  /**
   * Which incarnation of {@link id} this is.
   *
   * <b>A realm's identity is the pair, not the id</b> (`12-realms.md` §1.1): ids are reused once a realm is retired, so comparing by id alone can take a
   * reused id for the realm it replaced. The same number the `REALM` block puts on the wire, so a row and the session's own realm can be compared exactly.
   */
  readonly generation: number;
  /** The packed scene/palette/place/slot tag, the same one the `REALM` block carries — see `realm-view.ts`. */
  readonly appTag: number;
  readonly state: RealmRunState;
  /** Ticks of the base rate per visit of this realm: 1 is full rate. */
  readonly divisor: number;
  /** Unobserved ticks before this realm sleeps, or 0 for one that never does. */
  readonly sleepAfterTicks: number;
  readonly players: number;
  readonly npcs: number;
  readonly creatures: number;
  readonly structures: number;
}

/** How many realms are in each run state, over every realm and not only the listed ones. */
export interface RealmStateCounts {
  readonly active: number;
  readonly simulated: number;
  readonly dormant: number;
  readonly closing: number;
  /** How many run at a divisor above 1. */
  readonly divided: number;
}

/** The whole document. */
export interface RealmInventory {
  /** The tick the states were read at. */
  readonly tick: number;
  /**
   * The tick the populations were counted at, or -1 for a server that has not counted yet.
   *
   * <b>Deliberately not the same number as {@link tick}.</b> The states are read when the document is built; the
   * populations come from a walk the server does about once a second on its tick thread. Showing one timestamp for both
   * would be a quiet lie about how fresh the counts are, so the panel shows this one beside them.
   */
  readonly censusTick: number;
  /** The most rows this server will ever send. */
  readonly maxRows: number;
  /** Realms that would have had a row but for {@link maxRows}. */
  readonly omitted: number;
  readonly counts: RealmStateCounts;
  readonly realms: readonly RealmInventoryRow[];
}

const STATES: readonly string[] = ['active', 'simulated', 'dormant', 'closing'];

/**
 * A non-negative whole number.
 *
 * <b>Whole, not merely finite.</b> Every field this guards is a count or an id, and a realm id is what
 * {@link RealmPanelRow} hands to `ViewRealm` — which puts it in a `VarUInt` on the wire. `2.5` would survive a
 * finite-and-non-negative check and reach that field, which is the bug the toolbar's interior box already had to fix
 * with `Math.trunc`. `Number.isInteger` rejects `NaN` and both infinities on its own.
 */
function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function readCounts(value: unknown): RealmStateCounts | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const raw = value as Record<string, unknown>;
  const { active, simulated, dormant, closing, divided } = raw;
  if (!isCount(active) || !isCount(simulated) || !isCount(dormant) || !isCount(closing) || !isCount(divided)) {
    return null;
  }

  return { active, simulated, dormant, closing, divided };
}

function readRow(value: unknown): RealmInventoryRow | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const raw = value as Record<string, unknown>;
  const { id, generation, appTag, state, divisor, sleepAfterTicks, players, npcs, creatures, structures } = raw;
  if (!isCount(id) || !isCount(generation) || !isCount(appTag) || !isCount(divisor) || !isCount(sleepAfterTicks)) {
    return null;
  }

  if (!isCount(players) || !isCount(npcs) || !isCount(creatures) || !isCount(structures)) {
    return null;
  }

  // A state this client does not know is a server it does not understand, not a row to draw greyed out: the panel
  // branches on the name, so an unknown one would render as a realm in no state at all.
  if (typeof state !== 'string' || !STATES.includes(state)) {
    return null;
  }

  return { id, generation, appTag, state: state as RealmRunState, divisor, sleepAfterTicks, players, npcs, creatures, structures };
}

/**
 * The inventory, or `null` for anything that is not one.
 *
 * <b>A partial document is no document</b>, the same policy `server-config.ts` states and for the same reason: a panel
 * built from half a list is worse than one that says it does not know, because the missing half is invisible and the
 * viewer cannot tell a short list from a small world. One unreadable row rejects the whole thing.
 */
export function readRealmInventory(value: unknown): RealmInventory | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const raw = value as Record<string, unknown>;
  const { tick, censusTick, maxRows, omitted, realms } = raw;
  if (!isCount(tick) || !isCount(maxRows) || !isCount(omitted)) {
    return null;
  }

  // -1 is a real answer and the only negative one: a server that is up and has not walked the clusters yet.
  if (typeof censusTick !== 'number' || !Number.isInteger(censusTick) || censusTick < -1) {
    return null;
  }

  const counts = readCounts(raw['counts']);
  if (counts === null || !Array.isArray(realms)) {
    return null;
  }

  // The document states its own bound, so a document that breaks it is not one this client trusts. Without this a
  // server bug sending a million rows becomes a million objects and millions of DOM nodes, once a second.
  if (realms.length > maxRows) {
    return null;
  }

  const rows = realms.map(readRow);
  if (rows.some((row) => row === null)) {
    return null;
  }

  return { tick, censusTick, maxRows, omitted, counts, realms: rows as RealmInventoryRow[] };
}

/** Reads the live realm inventory, or `null` when there is none to read. Never throws. */
export async function fetchRealmInventory(): Promise<RealmInventory | null> {
  try {
    const response = await fetch('typhon/realms.json', { cache: 'no-store' });
    if (!response.ok) {
      return null;
    }

    return readRealmInventory(await response.json());
  } catch {
    // A mock session, a server too old to serve it, a page opened from a file, or a poll that raced a shutdown. Not
    // knowing is a state the panel handles; an unhandled rejection once a second is not.
    return null;
  }
}

/** One row as the panel draws it: the server's numbers plus the two things only the client knows. */
export interface RealmPanelRow extends RealmInventoryRow {
  /** The realm's name, from the same rule the HUD uses. */
  readonly label: string;
  /** Whether this is the realm the session is in — the row that must be marked and must not be clickable. */
  readonly here: boolean;
}

/** One clause of the summary line. */
export interface SummaryPart {
  readonly text: string;
  /**
   * Whether to draw this clause in the accent colour.
   *
   * <b>Only the sleeping count is accented, and it is the only number the panel exists for.</b> A first cut coloured
   * the `dormant` STATE in the rows instead, which was a rule that could never match: the server lists a realm only
   * while it is awake, so no row is ever dormant. The count is where dormancy lives.
   */
  readonly accent: boolean;
}

/**
 * The realm the session is in, as the frame reports it.
 *
 * Both halves, because a realm's identity is the pair: a row matched on the id alone would mark "you are here" against a realm that merely inherited the
 * number of the one that was there.
 */
export interface RealmHere {
  readonly realmId: number;
  readonly generation: number;
}

/** Everything the panel draws, derived once from the document and the session's own realm. */
export interface RealmPanelView {
  /** The aggregate line: the claim this panel exists to show, before any row. */
  readonly summary: readonly SummaryPart[];
  /** How fresh the populations are, and what was left out. */
  readonly freshness: string;
  readonly rows: readonly RealmPanelRow[];
}

/**
 * The panel's content, or `null` when there is nothing to draw.
 *
 * <b>Derived here rather than in the component</b>, for the reason `frame-policy.ts` and `realm-switch.ts` exist: no
 * test in this client can render React — there is no DOM — so anything that lives in JSX is untested by construction.
 * The component that consumes this has no decisions left in it.
 */
export function realmPanelView(inventory: RealmInventory | null, here: RealmHere | null): RealmPanelView | null {
  if (inventory === null) {
    return null;
  }

  const { counts, realms, omitted, censusTick } = inventory;
  const parts: SummaryPart[] = [
    { text: 'Realms', accent: false },
    { text: `${counts.active} active`, accent: false },
    { text: `${counts.simulated} simulated`, accent: false },
    { text: `${counts.dormant} asleep`, accent: true },
  ];

  if (counts.closing > 0) {
    // Only when there is one: a realm being torn down is rare and a permanent "0 closing" would be noise on every line.
    parts.push({ text: `${counts.closing} closing`, accent: false });
  }

  if (counts.divided > 0) {
    // The realms running at a divisor. Part of the same story as sleeping — work the engine is declining to do — and
    // the document already carries it, so leaving it undrawn would mean rejecting documents over a field nothing uses.
    parts.push({ text: `${counts.divided} divided`, accent: false });
  }

  // Two facts, because they are taken at two instants. The states are read as the document is built; the populations
  // come from a walk the server does about once a second. One timestamp for both would claim the counts are current.
  const fresh = censusTick < 0 ? 'populations not counted yet' : `populations as of tick ${censusTick}`;
  return {
    summary: parts,
    freshness: omitted > 0 ? `${fresh} · ${omitted} more not shown` : fresh,
    rows: labelled(realms).map((row) => ({ ...row, here: isHere(row, here) })),
  };
}

/**
 * Names every row, disambiguating any name that would otherwise appear twice.
 *
 * <b>Two realms really can carry the same `AppTag`.</b> The tag's slot field holds an interior's index within ITS
 * PLANET (`RealmTag.Interior(realm - first)`), so planet 0's fifth interior and planet 2's fifth interior are both
 * "Interior 5" — two rows with different populations and nothing to tell them apart, in a panel whose whole purpose is
 * watching one specific room. Dungeons have the same shape.
 *
 * Disambiguated from the data rather than by special-casing the scene: a world with one planet, which is the default,
 * keeps clean names, and any future tag whose slot is not globally unique is covered without another rule.
 */
/**
 * Whether a row is the realm the session is in.
 *
 * <b>Both halves must match.</b> The id alone is not a realm's identity — `realm-view.ts` says so for the frame, and it is no less true here. A dungeon slot
 * recycled between two polls would otherwise mark a row "you are here" for a world the session left, and suppress the one control that would take it back.
 */
function isHere(row: RealmInventoryRow, here: RealmHere | null): boolean {
  return here !== null && row.id === here.realmId && row.generation === here.generation;
}

function labelled(rows: readonly RealmInventoryRow[]): (RealmInventoryRow & { readonly label: string })[] {
  const used = new Map<string, number>();
  for (const row of rows) {
    const name = labelForAppTag(row.appTag);
    used.set(name, (used.get(name) ?? 0) + 1);
  }

  return rows.map((row) => {
    const name = labelForAppTag(row.appTag);
    return { ...row, label: (used.get(name) ?? 0) > 1 ? `${name} · #${row.id}` : name };
  });
}
