import { create } from 'zustand';
import { cityName } from '../data/market';
import { isOver, parseHordeStatus, type HordeRadar, type HordeRally, type HordeStatus } from '../data/horde';
import { NAMED_COLORS, type Waypoint } from '../data/waypoints';
import { useChat } from './chat-store';
import { useMarket } from './market-store';
import { useWaypoints } from './waypoints-store';

/** The waypoint an answer makes to the horde's fortress (Loïc, 2026-11-04), and its colour. */
export const FORTRESS_WAYPOINT_NAME = 'Raider fortress';

/** A waypoint this close to the fortress, by its name, is the one already made: metres. */
const FORTRESS_WAYPOINT_SLACK_M = 5;

/** The fortress waypoint already in the list, or undefined. */
function fortressWaypoint(list: readonly Waypoint[] | null, x: number, z: number): Waypoint | undefined {
  return list?.find(
    (w) =>
      w.name === FORTRESS_WAYPOINT_NAME &&
      Math.abs(w.x - x) <= FORTRESS_WAYPOINT_SLACK_M &&
      Math.abs(w.z - z) <= FORTRESS_WAYPOINT_SLACK_M,
  );
}

/**
 * The horde's call as this player sees it (horde.md § 6): the last status heard, the rally point the server gave this character, and whether the HUD's
 * line is muted for this event — a choice of this browser only, no server state.
 */
export interface HordeState {
  readonly status: HordeStatus | null;
  /** Where the raiders are, as the last radar said (horde.md § 10). */
  readonly radar: HordeRadar | null;
  readonly radared: (radar: HordeRadar) => void;
  readonly rally: HordeRally | null;
  readonly mutedEvent: string | null;
  /** The status the server told; a phase change is said in the chat. */
  readonly heard: (status: HordeStatus) => void;
  readonly rallied: (rally: HordeRally) => void;
  /** Answers the call: the server counts it, or refuses it ("enough", "cooldown"). */
  readonly answer: () => boolean;
  readonly mute: () => void;
  /** Reads the running horde once a session: one that started before this session connected is not broadcast again until it changes. */
  readonly ensure: () => void;
  readonly reset: () => void;
}

let ensured = false;

export const useHorde = create<HordeState>((set, get) => ({
  status: null,
  rally: null,
  radar: null,
  radared: (radar) => {
    set({ radar });
  },
  mutedEvent: null,
  heard: (status) => {
    const before = get().status;
    set({ status });
    if (before === null || before.event !== status.event || before.phase !== status.phase) {
      const where = cityName(status.city + 1) || 'a city';
      const line =
        status.phase === 'incoming'
          ? `A horde gathers to march on ${where}. Answer the call from the line at the top of the screen.`
          : status.phase === 'active'
            ? `The horde marches on ${where}!`
            : status.phase === 'won'
              ? `${where} held: the horde is beaten.`
              : status.phase === 'lost'
                ? `${where} fell to the horde.`
                : status.phase === 'ended'
                  ? `The raid on ${where} is over.`
                  : '';
      if (line !== '') {
        useChat.getState().notice(line, 'system');
      }
    }
  },
  rallied: (rally) => {
    const before = get().rally;
    set({ rally });
    const status = get().status;
    const fortress = status !== null && status.event === rally.event && (status.fortX !== 0 || status.fortZ !== 0) ? status : null;
    if (rally.state === 1 && (before === null || before.event !== rally.event || before.state !== 1)) {
      useChat.getState().notice('You answered the call: the rally point is on your guide.', 'system');
      // A waypoint to the fortress the raiders march from, once: not again on a reconnect's rally if the list already holds it.
      const waypoints = useWaypoints.getState();
      if (fortress !== null && fortressWaypoint(waypoints.list, fortress.fortX, fortress.fortZ) === undefined) {
        waypoints.create(FORTRESS_WAYPOINT_NAME, fortress.fortX, fortress.fortZ, NAMED_COLORS.red);
      }
    } else if (rally.state === 0 && fortress !== null) {
      // The event is over and its fortress gone: so is the waypoint to it.
      const waypoints = useWaypoints.getState();
      const made = fortressWaypoint(waypoints.list, fortress.fortX, fortress.fortZ);
      if (made !== undefined) {
        waypoints.remove(made.id);
      }
    } else if (rally.state === 3) {
      useChat.getState().notice('You did not reach the rally point in time: your answer lapsed.', 'system');
    }
  },
  answer: () => {
    const status = get().status;
    if (status === null || isOver(status.phase)) {
      return false;
    }

    return useMarket.getState().sendCommand({ kind: 'answerCall', event: status.event });
  },
  mute: () => {
    set({ mutedEvent: get().status?.event ?? null });
  },
  ensure: () => {
    const actions = useMarket.getState().actions;
    if (ensured || actions === null) {
      return;
    }

    ensured = true;
    void (async () => {
      try {
        const response = await actions.fetch('swg/horde', { headers: {} });
        const status = response.ok ? parseHordeStatus(await response.json(), performance.now()) : null;
        if (status !== null && get().status === null) {
          get().heard(status);
        }
      } catch {
        // The next change's broadcast says it.
      }
    })();
  },
  reset: () => {
    ensured = false;
    set({ status: null, rally: null, mutedEvent: null, radar: null });
  },
}));
