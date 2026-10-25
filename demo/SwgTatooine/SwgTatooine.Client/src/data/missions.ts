/**
 * Missions as a client sees them (claude/design/SwgTatooine/design/missions.md): a terminal's offers, replicated, and the
 * missions this character holds — its journal — read over HTTP.
 *
 * <b>Offers arrive by replication, the journal by request.</b> An offer is a `MissionOffer` entity standing at its
 * terminal, a member of the city set: a session in the city holds every offer there, live and the same for everyone, so
 * a list is simply "the offers at this terminal's position" and a taken one leaves it in the next frame. The journal is
 * not on the body — three slots there would cost every scripted body 48 bytes — so the body carries a revision (the
 * owner field `journal`) and {@link MissionJournal} re-reads `GET /swg/missions` whenever it moves.
 */

/** The eight directions an offer reads, by the octant the server numbers them: 0 north, clockwise. */
export const OCTANT_NAMES = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;

/** The most missions a character holds at once: the server's `TatooineData.MaxConcurrentMissions`. */
export const MAX_MISSIONS = 3;

/** One offer at a terminal, as its enter record carried it. */
export interface MissionOffer {
  readonly netId: number;
  /** Sent back with a take: a netId is reused, a serial is not. */
  readonly serial: number;
  /** The lair kind, which is also the name: an index into the server's creature list (`demo.json`). */
  readonly kind: number;
  readonly difficulty: number;
  readonly reward: number;
  /** 0 north, clockwise to 7 — see {@link OCTANT_NAMES}. */
  readonly octant: number;
  readonly distanceM: number;
}

/** One mission this character holds, as `GET /swg/missions` answers it. */
export interface HeldMission {
  /** The held mission's raw entity id, as a decimal string: 64 bits do not fit a JavaScript number. */
  readonly id: string;
  readonly kind: number;
  /** The lair kind's name, the server's: "ninja hideout", "rats". */
  readonly name: string;
  readonly difficulty: number;
  readonly reward: number;
  readonly octant: number;
  readonly distanceM: number;
  /** Where its lair stands now, planet metres; `null` while there is none (a new one is being drawn). */
  readonly lair: { readonly x: number; readonly z: number } | null;
}

/** What a mission sends you to do, from its lair kind's name: "Destroy the ninja hideout", "Clear out the rats". */
export function missionTitle(name: string): string {
  if (name.length === 0) {
    return 'Destroy a lair';
  }

  // A beasts' nest is named for its beasts, plural ("rats", "vipers"); a camp for what it is ("ninja hideout").
  return /(camp|hideout)$/.test(name) ? `Destroy the ${name}` : `Clear out the ${name}`;
}

/** Metres under a kilometre, kilometres with one decimal past it. */
export function formatMissionDistance(m: number): string {
  return m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(1)} km`;
}

/** How a terminal's list can be ordered. */
export type OfferSort = 'difficulty' | 'direction' | 'distance' | 'reward';

/**
 * The offers in an order: by the key asked for, then by reward and the serial so two equal rows never swap places between
 * frames. Direction sorts north first, clockwise, then nearest first within a direction — which is what "pack missions in one
 * area" wants.
 */
export function sortOffers(offers: readonly MissionOffer[], by: OfferSort, descending: boolean): MissionOffer[] {
  const sign = descending ? -1 : 1;
  const primary = (a: MissionOffer, b: MissionOffer): number => {
    switch (by) {
      case 'difficulty':
        return a.difficulty - b.difficulty;
      case 'direction':
        return a.octant - b.octant || a.distanceM - b.distanceM;
      case 'distance':
        return a.distanceM - b.distanceM;
      case 'reward':
        return a.reward - b.reward;
    }
  };

  return [...offers].sort((a, b) => sign * primary(a, b) || b.reward - a.reward || a.serial - b.serial);
}

/** A refusal of a take or a cancel, as the server's ACKS reasons name them (`TatooineReplication.MissionTaken` …). */
export const MISSION_REASONS: Readonly<Record<number, string>> = {
  137: 'You are down and cannot take a mission',
  138: 'Mission already taken',
  139: `You already hold ${MAX_MISSIONS} missions`,
  140: 'Too far from the terminal',
  141: 'That mission is not yours',
};

/** What a refusal reason reads as; a reason this table does not know still says something. */
export function missionRefusal(reason: number): string {
  return MISSION_REASONS[reason] ?? `Refused (${reason})`;
}

/** How long a failed journal read waits before it is tried again, milliseconds. */
export const JOURNAL_RETRY_MS = 3000;

type Fetch = (
  url: string,
  init: { headers: Record<string, string> },
) => Promise<{ ok: boolean; json(): Promise<unknown> }>;

/**
 * The character's held missions, re-read whenever the body's `journal` revision moves.
 *
 * <b>The revision is the trigger, not a clock.</b> A take, a cancel, a completion and a lair drawn somewhere new each move
 * it; nothing else changes the journal, so polling would only re-read the same answer. A read that fails is retried after
 * {@link JOURNAL_RETRY_MS}; one whose revision moved again meanwhile is superseded and its answer dropped.
 */
export class MissionJournal {
  /** Bumped whenever a new list arrives, so a pane built from it knows to be rebuilt. */
  version = 0;

  private list: readonly HeldMission[] = [];
  private wantedRev = -1;
  private readRev = -1;
  private inFlight = false;
  private retryAtMs = 0;

  private readonly fetcher: Fetch;
  private readonly token: () => string | null;
  private readonly now: () => number;

  /**
   * @param token The bearer token of this session's login, or `null` without one (no journal to read).
   * @param fetcher How a request is made; `fetch`, same-origin.
   * @param now A monotonic clock in milliseconds, for the retry delay.
   */
  constructor(
    token: () => string | null,
    fetcher: Fetch = (url, init) => fetch(url, init),
    now: () => number = () => performance.now(),
  ) {
    this.token = token;
    this.fetcher = fetcher;
    this.now = now;
  }

  /** The missions held, as last read. */
  get missions(): readonly HeldMission[] {
    return this.list;
  }

  /**
   * Called every frame with the body's `journal` owner field; reads the journal when it moved since the last read.
   *
   * @param rev The revision, or `NaN` while the body has not reported one.
   */
  update(rev: number): void {
    if (!Number.isFinite(rev)) {
      return;
    }

    this.wantedRev = rev;
    if (this.inFlight || rev === this.readRev || this.now() < this.retryAtMs) {
      return;
    }

    void this.read(rev);
  }

  /** Forgets everything: a new session may be another character on another server. */
  clear(): void {
    this.list = [];
    this.wantedRev = -1;
    this.readRev = -1;
    this.retryAtMs = 0;
    this.version++;
  }

  private async read(rev: number): Promise<void> {
    const token = this.token();
    if (token === null) {
      return;
    }

    this.inFlight = true;
    let answer: unknown;
    try {
      const response = await this.fetcher('swg/missions', { headers: { Authorization: `Bearer ${token}` } });
      answer = response.ok ? await response.json() : null;
    } catch {
      answer = null;
    } finally {
      this.inFlight = false;
    }

    if (!Array.isArray(answer)) {
      this.retryAtMs = this.now() + JOURNAL_RETRY_MS;
      return;
    }

    this.readRev = rev;
    this.list = answer.filter(isHeldMission);
    this.version++;

    // Moved again while this one was in flight: read it again now rather than on the next frame's tick of the revision.
    if (this.wantedRev !== rev) {
      this.update(this.wantedRev);
    }
  }
}

function isHeldMission(value: unknown): value is HeldMission {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const m = value as Record<string, unknown>;
  return typeof m.id === 'string' && typeof m.kind === 'number' && typeof m.reward === 'number';
}
