import { afterEach, describe, expect, it } from 'vitest';
import type { Descriptor } from '../src/assets/descriptor';
import { missionMenuItems, missionTerminalOf } from '../src/app/ticket-terminal';
import {
  JOURNAL_RETRY_MS,
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
    expect(missionRefusal(200)).toBe('Refused (200)');
  });
});

describe('a terminal list', () => {
  const list = [
    offer(1, { reward: 200, distanceM: 1200, octant: 3 }),
    offer(2, { reward: 300, distanceM: 1900, octant: 0 }),
    offer(3, { reward: 150, distanceM: 500, octant: 3 }),
    offer(4, { reward: 200, distanceM: 1100, octant: 0, difficulty: 2 }),
  ];

  it('sorts by reward, best first, and by distance, nearest first', () => {
    expect(sortOffers(list, 'reward', true).map((o) => o.serial)).toEqual([2, 1, 4, 3]);
    expect(sortOffers(list, 'distance', false).map((o) => o.serial)).toEqual([3, 4, 1, 2]);
    expect(sortOffers(list, 'difficulty', true)[0].serial).toBe(4);
  });

  it('sorts by direction north first, clockwise, nearest first within one: missions packed by area', () => {
    expect(sortOffers(list, 'direction', false).map((o) => o.serial)).toEqual([4, 2, 3, 1]);
  });

  it('never swaps two equal rows between two frames: ties break on the serial', () => {
    const tied = [offer(9), offer(7), offer(8)];
    expect(sortOffers(tied, 'difficulty', false).map((o) => o.serial)).toEqual([7, 8, 9]);
    expect(sortOffers([...tied].reverse(), 'difficulty', false).map((o) => o.serial)).toEqual([7, 8, 9]);
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

  it('offers its list and the journal', () => {
    expect(missionMenuItems(42).map((i) => i.action)).toEqual([
      { kind: 'missions', terminal: 42 },
      { kind: 'journal' },
    ]);
  });
});

describe('the journal', () => {
  const held = (id: string): HeldMission => ({
    id,
    kind: 6,
    name: 'ninja hideout',
    difficulty: 1,
    reward: 200,
    octant: 2,
    distanceM: 1000,
    lair: { x: 10, z: 20 },
  });

  const harness = (answers: unknown[]) => {
    let clock = 0;
    const urls: { url: string; auth: string | undefined }[] = [];
    let resolveNext: (() => void) | null = null;
    const journal = new MissionJournal(
      () => 'tok',
      (url, init) => {
        urls.push({ url, auth: init.headers['Authorization'] });
        const answer = answers.shift();
        return new Promise((resolve) => {
          resolveNext = () => {
            resolve(
              answer === 'fail'
                ? { ok: false, json: () => Promise.resolve(null) }
                : { ok: true, json: () => Promise.resolve(answer) },
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
    const h = harness([[held('7')], [held('7'), held('8')]]);
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
    const h = harness([[], [held('9')]]);
    h.journal.update(1);
    h.journal.update(2);
    await h.settle();
    expect(h.urls).toHaveLength(2);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['9']);
  });

  it('waits before trying a failed read again', async () => {
    const h = harness(['fail', [held('3')]]);
    h.journal.update(1);
    await h.settle();
    h.journal.update(1);
    expect(h.urls).toHaveLength(1);
    h.advance(JOURNAL_RETRY_MS);
    h.journal.update(1);
    await h.settle();
    expect(h.journal.missions.map((m) => m.id)).toEqual(['3']);
  });
});

describe('the mission panels', () => {
  afterEach(() => {
    useMissions.setState({ terminal: null, offers: [], notice: null, asking: null, actions: null, journalOpen: false });
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

  it('announce a mission that left the journal uncancelled as completed, and not one this player cancelled', () => {
    const mission = (id: string, reward: number): HeldMission => ({
      id,
      kind: 0,
      name: 'rats',
      difficulty: 1,
      reward,
      octant: 0,
      distanceM: 900,
      lair: null,
    });
    const store = useMissions.getState();
    store.resetJournal();
    store.setActions({ take: () => true, cancel: () => true });
    useMissions.getState().setJournal([mission('1', 150), mission('2', 300)]);
    expect(useMissions.getState().completed).toBeNull();

    useMissions.getState().cancel('1');
    useMissions.getState().setJournal([mission('2', 300)]);
    expect(useMissions.getState().completed, 'a cancel is not a completion').toBeNull();

    useMissions.getState().setJournal([]);
    expect(useMissions.getState().completed?.text).toBe('Mission complete: Clear out the rats · +300 cr');

    useMissions.getState().resetJournal();
    expect(useMissions.getState().completed, 'a new session announces nothing').toBeNull();
  });
});
