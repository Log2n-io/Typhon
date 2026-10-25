import { create } from 'zustand';
import { missionRefusal, missionTitle, type HeldMission, type MissionOffer, type OfferSort } from '../data/missions';

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
  /** The serial of the offer last asked for, while no answer has come: its row says so. */
  readonly asking: number | null;
  readonly journalOpen: boolean;
  /** The missions this character holds (`GET /swg/missions`). */
  readonly journal: readonly HeldMission[];
  /**
   * The last mission completed — it left the journal without this player cancelling it, which only breaking its lair does —
   * and when, for the toast that says so; `null` for none.
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
  /** The server refused the last take or cancel. */
  readonly refused: (reason: number) => void;
  /** A new read of the journal; a mission gone from it that was not cancelled here is announced as completed. */
  readonly setJournal: (journal: readonly HeldMission[]) => void;
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

/** The missions this player asked to cancel, so their leaving the journal is not announced as a completion. */
const cancelled = new Set<string>();

export const useMissions = create<MissionState>((set, get) => ({
  terminal: null,
  offers: [],
  sort: 'reward',
  descending: true,
  notice: null,
  asking: null,
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
    set(sent ? { asking: offer.serial, notice: null } : { notice: 'Not sent — wait a moment and try again' });
  },
  cancel: (id) => {
    const sent = get().actions?.cancel(id) ?? false;
    if (sent) {
      cancelled.add(id);
    }

    set({ notice: sent ? null : 'Not sent — wait a moment and try again' });
  },
  refused: (reason) => {
    set({ notice: missionRefusal(reason), asking: null });
  },
  setJournal: (journal) => {
    // A person's mission ends two ways: its lair broken (paid where it fell) or cancelled. One that left without a cancel
    // from here was completed — the only news of the payout a client gets, since credits are not replicated.
    let completed = get().completed;
    for (const before of get().journal) {
      if (journal.some((m) => m.id === before.id)) {
        continue;
      }

      if (!cancelled.delete(before.id)) {
        completed = { text: `Mission complete: ${missionTitle(before.name)} · +${before.reward} cr`, atMs: Date.now() };
        const shown = completed;
        setTimeout(() => {
          if (get().completed === shown) {
            set({ completed: null });
          }
        }, MISSION_TOAST_MS);
      }
    }

    set({ journal, completed });
  },
  resetJournal: () => {
    cancelled.clear();
    set({ journal: [], completed: null });
  },
  openJournal: () => {
    set({ journalOpen: true });
  },
  closeJournal: () => {
    set({ journalOpen: false });
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
