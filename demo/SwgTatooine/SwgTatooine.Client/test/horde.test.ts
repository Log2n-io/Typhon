import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  canAnswer,
  fromDirection,
  helpPercent,
  hordeGuide,
  hordeLine,
  parseHordeStatus,
  phaseOf,
  siegePercent,
  rallyWaypoint,
  startsIn,
  type HordeStatus,
} from '../src/data/horde';
import { kindOfNumber, mailCategory, senderOf } from '../src/data/mail';
import { marketWire, type MarketCommand } from '../src/data/market';
import { useChat } from '../src/state/chat-store';
import { FORTRESS_WAYPOINT_NAME, useHorde } from '../src/state/horde-store';
import { useWaypoints } from '../src/state/waypoints-store';
import { NAMED_COLORS } from '../src/data/waypoints';
import { useMarket, type MarketActions } from '../src/state/market-store';

/** The horde's call in the browser (horde.md § 6): its status read and said, the line's answer and mute, the rally point on the guide. */

const status = (over: Partial<HordeStatus> = {}): HordeStatus => ({
  event: '9',
  phase: 'incoming',
  city: 1,
  help: 0,
  startsInS: 125,
  heardAtMs: 1000,
  raiders: 0,
  committed: 0,
  present: 0,
  x: -1370,
  z: -3609,
  fortX: -670,
  fortZ: -3863,
  ...over,
});

describe('the horde (horde.md § 6)', () => {
  it('reads GET /swg/horde, and none for null or a malformed answer', () => {
    expect(
      parseHordeStatus(
        { event: '9', phase: 2, city: 1, help: 64, startsIn: 0, raiders: 12, committed: 6, present: 3, x: 1, z: 2 },
        5,
      ),
    ).toEqual({
      event: '9',
      phase: 'active',
      city: 1,
      help: 64,
      startsInS: 0,
      heardAtMs: 5,
      raiders: 12,
      committed: 6,
      present: 3,
      x: 1,
      z: 2,
      fortX: 0,
      fortZ: 0,
      hallX: 0,
      hallZ: 0,
      hallHp: 0,
      fortHp: 0,
    });
    expect(parseHordeStatus(null, 0)).toBeNull();
    expect(parseHordeStatus({ phase: 1 }, 0)).toBeNull();
    expect(phaseOf(9)).toBe('none');
  });

  it('says where, when and how much help is needed, by the client own city names, counting down between statuses', () => {
    expect(helpPercent({ help: 0 })).toBe(100);
    expect(helpPercent({ help: 255 })).toBe(0);
    expect(startsIn(status(), 1000 + 25_000)).toBe(100);
    expect(hordeLine(status(), 1000 + 25_000)).toBe(
      'A horde marches on Whitestone from the east in 1:40 — help needed 100 %',
    );
    expect(hordeLine(status({ phase: 'active', raiders: 12, help: 128, present: 4 }), 0)).toBe(
      'Raid on Whitestone from the east: 12 raiders — help needed 50 % (4 defending)',
    );
    expect(fromDirection({ x: 0, z: 0, fortX: 0, fortZ: 500 })).toBe('north');
    expect(fromDirection({ x: 0, z: 0, fortX: -400, fortZ: -400 })).toBe('south-west');
    expect(hordeLine(status({ phase: 'won' }), 0)).toBe('Whitestone held: the horde is beaten');
    // The siege (horde.md § 10): both pools said, as percentages — 1 + 254ths on the wire, 0 none.
    expect(
      hordeLine(status({ phase: 'active', raiders: 12, help: 128, present: 4, hallHp: 128, fortHp: 255 }), 0),
    ).toBe(
      'Raid on Whitestone from the east: 12 raiders — help needed 50 % (4 defending) — town hall 50 %, fortress 100 %',
    );
    expect(siegePercent(0)).toBeNull();
    expect(siegePercent(1)).toBe(0);
  });

  it('offers to answer while help is needed and this character is not committed; the rally point steers the guide until it lapses', () => {
    const committed = { event: '9', x: 1, z: 2, state: 1 };
    expect(canAnswer(status(), null)).toBe(true);
    expect(canAnswer(status({ help: 255 }), null)).toBe(false);
    expect(canAnswer(status(), committed)).toBe(false);
    expect(canAnswer(status({ phase: 'won' }), null)).toBe(false);
    expect(rallyWaypoint(committed)).toMatchObject({ x: 1, z: 2, active: true, name: 'Rally point' });
    expect(rallyWaypoint({ ...committed, state: 3 })).toBeNull();
    expect(rallyWaypoint({ ...committed, state: 0 })).toBeNull();
  });

  it('guides everyone to the target during a horde, the told rally point first, and nobody who muted it or once it is over', () => {
    const told = { event: '9', x: 5, z: 6, state: 1 };
    expect(hordeGuide(status(), null, null)).toMatchObject({ x: -1370, z: -3609, name: 'Rally point' });
    expect(hordeGuide(status(), told, null)).toMatchObject({ x: 5, z: 6 });
    expect(hordeGuide(status(), null, '9')).toBeNull();
    expect(hordeGuide(status({ phase: 'won' }), null, null)).toBeNull();
    expect(hordeGuide(null, null, null)).toBeNull();
  });

  it('sends the answer as AnswerCall, and files the horde mail under System from the garrison', () => {
    expect(marketWire({ kind: 'answerCall', event: '9' })).toEqual({ name: 'AnswerCall', values: { event: 9n } });
    expect(kindOfNumber(7)).toBe('horde');
    expect(mailCategory('horde')).toBe('system');
    expect(senderOf({ kind: 'horde', from: '' })).toBe('The garrison');
  });
});

describe('the horde store (horde.md § 6)', () => {
  const sent: MarketCommand[] = [];

  beforeEach(() => {
    sent.length = 0;
    useHorde.getState().reset();
    useChat.getState().clear();
    const actions: MarketActions = {
      send: (command) => {
        sent.push(command);
        return true;
      },
      fetch: () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(null) }),
      token: () => 't',
    };
    useMarket.getState().setActions(actions);
  });

  afterEach(() => {
    useMarket.getState().setActions(null);
  });

  it('says a phase change in the chat once, answers the running event, and mutes its line', () => {
    useHorde.getState().heard(status());
    useHorde.getState().heard(status({ committed: 3, help: 30 }));
    useHorde.getState().heard(status({ phase: 'active' }));
    expect(useChat.getState().histories.system.map((l) => l.text)).toEqual([
      'A horde gathers to march on Whitestone. Answer the call from the line at the top of the screen.',
      'The horde marches on Whitestone!',
    ]);

    expect(useHorde.getState().answer()).toBe(true);
    expect(sent).toEqual([{ kind: 'answerCall', event: '9' }]);
    useHorde.getState().mute();
    expect(useHorde.getState().mutedEvent).toBe('9');
  });

  it('makes a waypoint to the fortress on answering, once, and takes it away when the event ends', () => {
    useWaypoints.getState().reset();
    useHorde.getState().heard(status());
    useHorde.getState().rallied({ event: '9', x: 1, z: 2, state: 1 });
    expect(sent).toContainEqual({
      kind: 'waypointCreate',
      name: FORTRESS_WAYPOINT_NAME,
      x: -670,
      z: -3863,
      color: NAMED_COLORS.red,
      installation: '0',
    });

    // A reconnect's rally, the waypoint already listed: none made again; the event over: it is deleted.
    useMarket.getState().settle('waypoint');
    sent.length = 0;
    const made = {
      id: '77',
      name: FORTRESS_WAYPOINT_NAME,
      kind: 1,
      color: NAMED_COLORS.red,
      x: -670,
      z: -3863,
      realm: 0,
      installation: '0',
      active: false,
    };
    useWaypoints.setState({ list: [made] });
    useHorde.getState().reset();
    useHorde.getState().heard(status());
    useHorde.getState().rallied({ event: '9', x: 1, z: 2, state: 1 });
    expect(sent.filter((c) => c.kind === 'waypointCreate')).toEqual([]);
    useHorde.getState().rallied({ event: '9', x: 1, z: 2, state: 0 });
    expect(sent).toContainEqual({ kind: 'waypointDelete', waypoint: '77' });
    useMarket.getState().settle('waypoint');
  });

  it('says the rally point taken and the answer lapsed', () => {
    useHorde.getState().rallied({ event: '9', x: 1, z: 2, state: 1 });
    useHorde.getState().rallied({ event: '9', x: 1, z: 2, state: 3 });
    expect(useChat.getState().histories.system.map((l) => l.text)).toEqual([
      'You answered the call: the rally point is on your guide.',
      'You did not reach the rally point in time: your answer lapsed.',
    ]);
  });
});
