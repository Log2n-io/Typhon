import { create } from 'zustand';
import {
  MISSION_ANSWER_MS,
  missionRefusal,
  missionTitle,
  type CompletedMission,
  type HeldMission,
  type MissionOffer,
  type OfferSort,
} from '../data/missions';

/**
 * Missions on screen (missions.md § 5): the terminal list that is open, how it is sorted, what the server last refused, and
 * the journal.
 *
 * <b>Its own store, beside the UI's</b>, for the chat store's reason: offers and the journal arrive at the world's pace, a
 * handful of changes a minute, and the frame loop publishes them here only when they change. The take and the cancel are
 * the app's to send (`ClientApp` registers them), so this file never sees a socket.
 */

/** What the app sends for the panels. */
export interface MissionActions {
  /** `TakeMission`; false when nothing left the client (the command's rate, or a session that may not). */
  readonly take: (offerNetId: number, serial: number) => boolean;
  /** `AbandonMission`; false when nothing left the client. */
  readonly cancel: (id: string) => boolean;
}

export interface MissionState {
  /** The netId of the mission terminal whose list is open, or `null`. */
  readonly terminal: number | null;
  /** That terminal's offers, as the frame loop last found them. */
  readonly offers: readonly MissionOffer[];
  readonly sort: OfferSort;
  readonly descending: boolean;
  /** The last thing worth saying about a take or a cancel — a refusal, usually — or `null`. */
  readonly notice: string | null;
  /** The serial of the offer last asked for, while no answer has come — for {@link MISSION_ANSWER_MS} at most: its row says so. */
  readonly asking: number | null;
  /** The held missions whose cancel is in flight: their rows' buttons are off, so a second click sends no second cancel. */
  readonly cancelling: readonly string[];
  readonly journalOpen: boolean;
  /** The missions this character holds (`GET /swg/missions`). */
  readonly journal: readonly HeldMission[];
  /**
   * The last missions completed — gone from the journal, and stated completed by the server — and when, for the toast that
   * says so; `null` for none.
   */
  readonly completed: { readonly text: string; readonly atMs: number } | null;
  /** The lair kinds' names, by kind, from the server's creature list: what an offer is called. */
  readonly kindNames: readonly string[];
  readonly actions: MissionActions | null;

  readonly openTerminal: (terminal: number) => void;
  readonly closeTerminal: () => void;
  readonly setOffers: (offers: readonly MissionOffer[]) => void;
  /** Sorts by a column; the same column again flips the order. */
  readonly sortBy: (by: OfferSort) => void;
  readonly take: (offer: MissionOffer) => void;
  readonly cancel: (id: string) => void;
  /** The server refused a take, or the cancel of mission `cancelled`. */
  readonly refused: (reason: number, cancelled?: string | null) => void;
  /** A new read of the journal; a mission gone from it that the server states completed is announced. */
  readonly setJournal: (journal: readonly HeldMission[], completed: readonly CompletedMission[]) => void;
  /** Forgets the journal without announcing anything: a new session. */
  readonly resetJournal: () => void;
  readonly openJournal: () => void;
  readonly closeJournal: () => void;
  readonly toggleJournal: () => void;
  readonly setKindNames: (names: readonly string[]) => void;
  readonly setActions: (actions: MissionActions | null) => void;
}

/** How long the word that a mission was completed stays up, milliseconds. */
export const MISSION_TOAST_MS = 6000;

export const useMissions = create<MissionState>((set, get) => ({
  terminal: null,
  offers: [],
  sort: 'reward',
  descending: true,
  notice: null,
  asking: null,
  cancelling: [],
  journalOpen: false,
  journal: [],
  completed: null,
  kindNames: [],
  actions: null,

  openTerminal: (terminal) => {
    set({ terminal, notice: null, asking: null, offers: get().terminal === terminal ? get().offers : [] });
  },
  closeTerminal: () => {
    if (get().terminal !== null) {
      set({ terminal: null, offers: [], notice: null, asking: null });
    }
  },
  setOffers: (offers) => {
    // The answer to an ask in flight: the offer it named left the list — taken, by this player if the journal grows.
    const asking = get().asking;
    set({ offers, asking: asking !== null && offers.some((o) => o.serial === asking) ? asking : null });
  },
  sortBy: (by) => {
    const s = get();
    // Reward and difficulty read best first; distance and direction nearest and north first.
    set(
      s.sort === by ? { descending: !s.descending } : { sort: by, descending: by === 'reward' || by === 'difficulty' },
    );
  },
  take: (offer) => {
    const sent = get().actions?.take(offer.netId, offer.serial) ?? false;
    if (!sent) {
      set({ notice: 'Not sent — wait a moment and try again' });
      return;
    }

    // Accepted, the offer leaves the list; refused, the reason comes back. Lost — a link dropped with it queued — neither
    // happens, and every Take button would stay off until the panel was reopened.
    set({ asking: offer.serial, notice: null });
    setTimeout(() => {
      if (get().asking === offer.serial) {
        set({ asking: null });
      }
    }, MISSION_ANSWER_MS);
  },
  cancel: (id) => {
    if (get().cancelling.includes(id)) {
      return;
    }

    const sent = get().actions?.cancel(id) ?? false;
    if (!sent) {
      set({ notice: 'Not sent — wait a moment and try again' });
      return;
    }

    set({ notice: null, cancelling: [...get().cancelling, id] });
    setTimeout(() => {
      if (get().cancelling.includes(id)) {
        set({ cancelling: get().cancelling.filter((c) => c !== id) });
      }
    }, MISSION_ANSWER_MS);
  },
  refused: (reason, cancelledId = null) => {
    const cancelling = cancelledId === null ? get().cancelling : get().cancelling.filter((c) => c !== cancelledId);
    set({ notice: missionRefusal(reason), asking: cancelledId === null ? null : get().asking, cancelling });
  },
  setJournal: (journal, done) => {
    // Announced only on the server's word (`completed`): a mission leaves the journal when it is cancelled too, or for reasons
    // a client cannot see, and inferring a completion from its absence once announced a payout nobody was paid. Only one
    // this client saw held, so the last few completions a fresh session is told of are not news.
    const lines: string[] = [];
    let total = 0;
    for (const before of get().journal) {
      if (!journal.some((m) => m.id === before.id) && done.some((d) => d.id === before.id)) {
        lines.push(missionTitle(before.name));
        total += before.reward;
      }
    }

    let completed = get().completed;
    if (lines.length > 0) {
      const what = lines.length === 1 ? 'Mission complete' : `${lines.length} missions complete`;
      completed = { text: `${what}: ${lines.join(', ')} · +${total} cr`, atMs: Date.now() };
      const shown = completed;
      setTimeout(() => {
        if (get().completed === shown) {
          set({ completed: null });
        }
      }, MISSION_TOAST_MS);
    }

    const cancelling = get().cancelling.filter((id) => journal.some((m) => m.id === id));
    set({ journal, completed, cancelling });
  },
  resetJournal: () => {
    set({ journal: [], completed: null, cancelling: [], asking: null });
  },
  openJournal: () => {
    set({ journalOpen: true, notice: null });
  },
  closeJournal: () => {
    set({ journalOpen: false, notice: null });
  },
  toggleJournal: () => {
    set({ journalOpen: !get().journalOpen });
  },
  setKindNames: (kindNames) => {
    set({ kindNames });
  },
  setActions: (actions) => {
    set({ actions });
  },
}));
