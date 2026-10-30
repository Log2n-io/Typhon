import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Descriptor } from '../src/assets/descriptor';
import { missionTerminalAction, missionTerminalOf } from '../src/app/ticket-terminal';
import {
  JOURNAL_RETRY_MS,
  listedOffers,
  MISSION_ANSWER_MS,
  MissionJournal,
  missionRefusal,
  missionTitle,
  OCTANT_NAMES,
  sortOffers,
  type HeldMission,
  type MissionOffer,
} from '../src/data/missions';
import { useMissions } from '../src/state/mission-store';

/** Missions on the client (missions.md § 5): the terminal's list, the journal, and what a refusal reads as. */

const offer = (serial: number, over: Partial<MissionOffer> = {}): MissionOffer => ({
  netId: 100 + serial,
  serial,
  kind: 0,
  difficulty: 1,
  level: 1,
  reward: 150,
  octant: 0,
  distanceM: 500,
  ...over,
});

describe('a mission', () => {
  it('is named for what it sends you to break', () => {
    expect(missionTitle('ninja hideout')).toBe('Destroy the ninja hideout');
    expect(missionTitle('soldier camp')).toBe('Destroy the soldier camp');
    expect(missionTitle('rats')).toBe('Clear out the rats');
    expect(missionTitle('')).toBe('Destroy a lair');
  });

  it('reads its direction as the server numbers it: north first, clockwise', () => {
    expect(OCTANT_NAMES).toEqual(['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW']);
  });

  it("reads the server's refusal reasons, and says something for one it does not know", () => {
    // TatooineReplication: FirstApplicationReason (128) + 9 … + 13.
    expect(missionRefusal(138)).toBe('Mission already taken');
    expect(missionRefusal(139)).toContain('3 missions');
    expect(missionRefusal(140)).toBe('Too far from the terminal');
    expect(missionRefusal(141)).toContain('not yours');
    expect(missionRefusal(137)).toContain('down');
    expect(missionRefusal(142)).toContain('not in the world');
    expect(missionRefusal(200)).toBe('Refused (200)');
  });

  it('reads a take refused above your level (placeholder code, until the server names it)', () => {
    expect(missionRefusal(150)).toContain('above your level');
  });
});

describe("a terminal's filter", () => {
  const levels = (l: readonly MissionOffer[]) => l.map((o) => o.level);
  const all = Array.from({ length: 10 }, (_, level) => offer(level + 1, { level }));

  it("lists only the offers from five below the viewer's CL to three above, and says how many it left out", () => {
    const listed = listedOffers(all, 5, false);
    expect(levels(listed.rows)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8]);
    expect(listed.hidden).toBe(1);
  });

  it('lists every offer with "Show all"', () => {
    const listed = listedOffers(all, 5, true);
    expect(levels(listed.rows)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    expect(listed.hidden).toBe(0);
  });

  it('lists every offer without a viewer CL', () => {
    expect(levels(listedOffers(all, null, false).rows)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
  });

  it('is toggled in the store', () => {
    useMissions.setState({ showAll: false });
    useMissions.getState().toggleShowAll();
    expect(useMissions.getState().showAll).toBe(true);
    useMissions.getState().toggleShowAll();
    expect(useMissions.getState().showAll).toBe(false);
  });
});

describe('a terminal list', () => {
  const list = [
    offer(1, { reward: 200, distanceM: 1200, octant: 3 }),
    offer(2, { reward: 300, distanceM: 1900, octant: 0 }),
    offer(3, { reward: 150, distanceM: 500, octant: 3 }),
    offer(4, { reward: 200, distanceM: 1100, octant: 0, level: 7 }),
  ];

  it('sorts by reward, best first, and by distance, nearest first', () => {
    expect(sortOffers(list, 'reward', true).map((o) => o.serial)).toEqual([2, 1, 4, 3]);
    expect(sortOffers(list, 'distance', false).map((o) => o.serial)).toEqual([3, 4, 1, 2]);
    expect(sortOffers(list, 'level', true)[0].serial).toBe(4);
  });

  it('sorts by direction north first, clockwise, nearest first within one: missions packed by area', () => {
    expect(sortOffers(list, 'direction', false).map((o) => o.serial)).toEqual([4, 2, 3, 1]);
  });

  it('never swaps two equal rows between two frames: ties break on the serial', () => {
    const tied = [offer(9), offer(7), offer(8)];
    expect(sortOffers(tied, 'level', false).map((o) => o.serial)).toEqual([7, 8, 9]);
    expect(sortOffers([...tied].reverse(), 'level', false).map((o) => o.serial)).toEqual([7, 8, 9]);
  });
});

describe('a mission terminal', () => {
  it("is used from its published 'mission' interaction, and boxed as its asset is drawn", () => {
    const terminal = {
      id: 'mission_terminal',
      interactions: [{ kind: 'mission', pos: [0, 0, 0.65], rot: [0, 0, 0, 1], forward: '-Z', radius: 1.2 }],
      children: [],
      bounds: {
        render: [
          [-0.5, 0, -0.4],
          [0.5, 2, 0.4],
        ],
        collision: null,
      },
    } as unknown as Descriptor;
    const use = missionTerminalOf(terminal, 1000, 50, 2000, 0);
    expect(use).not.toBeNull();
    expect(use!.x).toBeCloseTo(1000, 6);
    expect(use!.z).toBeCloseTo(2000.65, 6);
    expect(use!.radiusM).toBeCloseTo(1.7, 6);
    expect(use!.corners).not.toBeNull();
    expect(missionTerminalOf({ ...terminal, interactions: [] }, 0, 0, 0, 0)).toBeNull();
  });

  it("opens its own list of missions at once, the journal being the toolbar's", () => {
    expect(missionTerminalAction(42)).toEqual({ kind: 'missions', terminal: 42 });
  });
});

describe('the journal', () => {
  const held = (id: string): HeldMission => ({
    id,
    kind: 6,
    name: 'ninja hideout',
    difficulty: 1,
    level: 4,
    reward: 200,
    octant: 2,
    distanceM: 1000,
    lair: { x: 10, z: 20 },
  });

  /** The server's answer: the held missions, and the completions it states. */
  const answerOf = (missions: HeldMission[], completed: unknown[] = []) => ({ missions, completed });

  const harness = (answers: unknown[], token: () => string | null = () => 'tok') => {
    let clock = 0;
    const urls: { url: string; auth: string | undefined }[] = [];
    let resolveNext: (() => void) | null = null;
    const journal = new MissionJournal(
      token,
      (url, init) => {
        urls.push({ url, auth: init.headers['Authorization'] });
        const answer = answers.shift();
        return new Promise((resolve) => {
          resolveNext = () => {
            resolve(
              answer === 'fail'
                ? { ok: false, status: 500, json: () => Promise.resolve(null) }
                : answer === 401
                  ? { ok: false, status: 401, json: () => Promise.resolve(null) }
                  : { ok: true, status: 200, json: () => Promise.resolve(answer) },
            );
          };
        });
      },
      () => clock,
    );
    const settle = async (): Promise<void> => {
      const r = resolveNext;
      resolveNext = null;
      r?.();
      for (let i = 0; i < 5; i++) {
        await Promise.resolve();
      }
    };
    return { journal, urls, settle, advance: (ms: number) => (clock += ms) };
  };

  it('is read when the revision moves, with the bearer token, and not again until it moves again', async () => {
    const h = harness([answerOf([held('7')]), answerOf([held('7'), held('8')])]);
    h.journal.update(Number.NaN);
    expect(h.urls).toHaveLength(0);
    h.journal.update(1);
    await h.settle();
    expect(h.urls).toEqual([{ url: 'swg/missions', auth: 'Bearer tok' }]);
    expect(h.journal.missions.map((m) => m.id)).toEqual(['7']);
    h.journal.update(1);
    h.journal.update(1);
    expect(h.urls).toHaveLength(1);
    h.journal.update(2);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['7', '8']);
  });

  it('is read again at once when the revision moved during a read', async () => {
    const h = harness([answerOf([]), answerOf([held('9')])]);
    h.journal.update(1);
    h.journal.update(2);
    await h.settle();
    expect(h.urls).toHaveLength(2);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['9']);
  });

  it('waits before trying a failed read again', async () => {
    const h = harness(['fail', answerOf([held('3')])]);
    h.journal.update(1);
    await h.settle();
    h.journal.update(1);
    expect(h.urls).toHaveLength(1);
    h.advance(JOURNAL_RETRY_MS);
    h.journal.update(1);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['3']);
  });

  it('carries the completions the server states', async () => {
    const h = harness([answerOf([], [{ id: '5', kind: 0, name: 'rats', reward: 150 }, { bad: true }])]);
    h.journal.update(1);
    await h.settle();
    expect(h.journal.completed).toEqual([{ id: '5', kind: 0, name: 'rats', reward: 150 }]);
  });

  it("drops an answer that lands after a clear: it is another session's", async () => {
    const h = harness([answerOf([held('1')]), answerOf([held('2')])]);
    h.journal.update(1);
    h.journal.clear();
    await h.settle();
    expect(h.journal.missions, "the old session's answer was applied").toEqual([]);
    h.journal.update(1);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['2']);
  });

  it('does not ask again with a token the server refused, until the token changes; nor ask at all without one', async () => {
    let token: string | null = null;
    const h = harness([401, answerOf([held('4')])], () => token);
    h.journal.update(1);
    expect(h.urls, 'asked without a token').toHaveLength(0);
    token = 'old';
    h.journal.update(1);
    await h.settle();
    h.advance(JOURNAL_RETRY_MS * 10);
    h.journal.update(1);
    expect(h.urls, 'a refused token was tried again').toHaveLength(1);
    token = 'new';
    h.journal.update(1);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['4']);
  });
});

describe('the mission panels', () => {
  afterEach(() => {
    vi.useRealTimers();
    useMissions.setState({ terminal: null, offers: [], notice: null, asking: null, actions: null, journalOpen: false });
    useMissions.getState().resetJournal();
  });

  it('send a take through the app, and say so when nothing left the client', () => {
    const sent: number[] = [];
    useMissions.getState().setActions({
      take: (netId) => {
        sent.push(netId);
        return sent.length === 1;
      },
      cancel: () => false,
    });
    useMissions.getState().take(offer(5));
    expect(sent).toEqual([105]);
    expect(useMissions.getState().asking).toBe(5);
    useMissions.getState().take(offer(6));
    expect(useMissions.getState().notice).toContain('Not sent');
  });

  it('stop waiting for a take that is never answered', () => {
    vi.useFakeTimers();
    useMissions.getState().setActions({ take: () => true, cancel: () => true });
    useMissions.getState().take(offer(5));
    expect(useMissions.getState().asking).toBe(5);
    vi.advanceTimersByTime(MISSION_ANSWER_MS);
    expect(useMissions.getState().asking, 'every Take button stays off').toBeNull();
  });

  it('send one cancel per mission while it is in flight, and take it back on a refusal', () => {
    let sent = 0;
    useMissions.getState().setActions({ take: () => true, cancel: () => ++sent > 0 });
    useMissions.getState().cancel('9');
    useMissions.getState().cancel('9');
    expect(sent, 'a double click sent two cancels').toBe(1);
    expect(useMissions.getState().cancelling).toEqual(['9']);
    useMissions.getState().refused(141, '9');
    expect(useMissions.getState().cancelling).toEqual([]);
    expect(useMissions.getState().notice).toContain('not yours');
  });

  it("show the server's refusal, and stop waiting for the row once it leaves the list", () => {
    useMissions.setState({ asking: 5, offers: [offer(5)] });
    useMissions.getState().setOffers([offer(6)]);
    expect(useMissions.getState().asking).toBeNull();
    useMissions.getState().refused(138);
    expect(useMissions.getState().notice).toBe('Mission already taken');
  });

  it('flip the order on a second click of the same column', () => {
    const s = useMissions.getState();
    s.sortBy('distance');
    expect(useMissions.getState()).toMatchObject({ sort: 'distance', descending: false });
    useMissions.getState().sortBy('distance');
    expect(useMissions.getState().descending).toBe(true);
    useMissions.getState().sortBy('reward');
    expect(useMissions.getState()).toMatchObject({ sort: 'reward', descending: true });
  });

  it("announce a mission completed only on the server's word, and every one that completed together", () => {
    const mission = (id: string, reward: number, name = 'rats'): HeldMission => ({
      id,
      kind: 0,
      name,
      difficulty: 1,
      level: 2,
      reward,
      octant: 0,
      distanceM: 900,
      lair: null,
    });
    const done = (id: string) => ({ id, name: 'rats', reward: 0 });
    const store = useMissions.getState();
    store.resetJournal();
    useMissions
      .getState()
      .setJournal([mission('1', 150), mission('2', 300), mission('3', 100, 'ninja hideout')], [done('0')]);
    expect(useMissions.getState().completed, 'an old completion, of a mission this client never saw held').toBeNull();

    useMissions.getState().setJournal([mission('2', 300), mission('3', 100, 'ninja hideout')], [done('0')]);
    expect(useMissions.getState().completed, 'gone, but not stated completed: a cancel, or anything else').toBeNull();

    useMissions.getState().setJournal([mission('3', 100, 'ninja hideout')], [done('0'), done('2')]);
    expect(useMissions.getState().completed?.text).toBe('Mission complete: Clear out the rats · +300 cr');

    useMissions.getState().setJournal([mission('4', 50)], [done('3')]);
    useMissions.getState().setJournal([], [done('3'), done('4')]);
    expect(useMissions.getState().completed?.text).toBe('Mission complete: Clear out the rats · +50 cr');

    useMissions.getState().setJournal([mission('5', 10), mission('6', 20)], []);
    useMissions.getState().setJournal([], [done('5'), done('6')]);
    expect(useMissions.getState().completed?.text).toBe(
      '2 missions complete: Clear out the rats, Clear out the rats · +30 cr',
    );

    useMissions.getState().resetJournal();
    expect(useMissions.getState().completed, 'a new session announces nothing').toBeNull();
  });
});
