import { describe, expect, it } from 'vitest';
import { parseChatCommand } from '../src/data/chat-commands';
import { marketRefusal, marketWire } from '../src/data/market';
import {
  colorOf,
  cssColor,
  GROUP_COLORS,
  KIND_CUSTOM,
  KIND_INSTALLATION,
  namedWaypoints,
  parseColor,
  parseWayArgs,
  parseWaypoints,
  type Waypoint,
} from '../src/data/waypoints';
import { navFilterKey } from '../src/ui/NavPane';

/** Waypoints on the client (client.md C38, U3): the server's list read, colours by kind and chosen, `/way`'s arguments, the commands' wire. */

const waypoint = (over: Partial<Waypoint>): Waypoint => ({
  id: '1',
  name: 'Home',
  kind: KIND_CUSTOM,
  color: 0,
  x: 0,
  z: 0,
  realm: 0,
  installation: '0',
  active: false,
  ...over,
});

describe('the server’s list', () => {
  it('is read, a row that is not a waypoint dropped on its own', () => {
    const list = parseWaypoints({
      max: 100,
      waypoints: [
        {
          id: '12',
          name: 'Krayt',
          kind: 1,
          color: 0x3399ff,
          x: 10.5,
          z: -3,
          realm: 0,
          installation: '0',
          active: true,
        },
        { id: 'x', name: 3 },
        { id: '13', name: 'Mine', kind: 2, color: 0, x: 1, z: 2, installation: '99' },
      ],
    });
    expect(list?.max).toBe(100);
    expect(list?.waypoints.map((w) => w.id)).toEqual(['12', '13']);
    expect(list?.waypoints[0]).toMatchObject({ name: 'Krayt', active: true, color: 0x3399ff });
    expect(list?.waypoints[1]).toMatchObject({ kind: KIND_INSTALLATION, installation: '99', active: false });
    expect(parseWaypoints({ nope: 1 })).toBeNull();
    expect(parseWaypoints(null)).toBeNull();
  });
});

describe('a waypoint’s colour (U3-2)', () => {
  it('is its kind’s unless its owner chose one', () => {
    expect(colorOf(waypoint({}))).toBe(GROUP_COLORS.custom);
    expect(colorOf(waypoint({ kind: KIND_INSTALLATION }))).toBe(GROUP_COLORS.structure);
    expect(colorOf(waypoint({ color: 0x3399ff }))).toBe('#3399ff');
    expect(cssColor(0x000001)).toBe('#000001');
  });

  it('is read from #rrggbb, #rgb or a name, and 0 is never sent for black', () => {
    expect(parseColor('#ff8800')).toBe(0xff8800);
    expect(parseColor('#F80')).toBe(0xff8800);
    expect(parseColor('#Red')).toBe(0xff5252);
    expect(parseColor('#000')).toBe(1);
    expect(parseColor('#nope')).toBeNull();
    expect(parseColor('red')).toBeNull();
  });

  it('gives the pane’s filter its key: a city structure by kind, the rest by group', () => {
    expect(navFilterKey({ group: 'city', kind: 3 })).toBe('city:3');
    expect(navFilterKey({ group: 'mission', kind: 1001 })).toBe('mission');
    expect(navFilterKey({ group: 'custom', kind: 2000 })).toBe('custom');
  });
});

describe('/way (U3-1)', () => {
  it('takes a place, a name and a colour, each optional but the name', () => {
    expect(parseWayArgs(['120', '-45.5', 'Krayt', 'bones', '#blue'])).toEqual({
      ok: true,
      x: 120,
      z: -45.5,
      name: 'Krayt bones',
      color: 0x448aff,
    });
    expect(parseWayArgs(['Krayt'])).toEqual({ ok: true, x: null, z: null, name: 'Krayt', color: 0 });
    expect(parseWayArgs(['12', 'Krayt'])).toEqual({ ok: true, x: null, z: null, name: '12 Krayt', color: 0 });
    expect(parseWayArgs(['Krayt', '#ff0000'])).toEqual({ ok: true, x: null, z: null, name: 'Krayt', color: 0xff0000 });
  });

  it('refuses no name, a colour that is not one, and a name too long, saying why', () => {
    for (const args of [[], ['10', '20'], ['#red'], ['Krayt', '#mauve'], ['x'.repeat(33)]]) {
      const way = parseWayArgs(args);
      expect(way.ok, args.join(' ')).toBe(false);
    }

    expect(parseWayArgs(['é'.repeat(16)]).ok).toBe(true);
    expect(parseWayArgs(['é'.repeat(17)]).ok).toBe(false);
  });

  it('is a chat command, with /waydel and /waylist beside it', () => {
    expect(parseChatCommand('/way 1 2 Home #green')).toEqual({
      kind: 'way',
      x: 1,
      z: 2,
      name: 'Home',
      color: 0x66bb6a,
    });
    expect(parseChatCommand('/waydel  Krayt bones ')).toEqual({ kind: 'waydel', name: 'Krayt bones' });
    expect(parseChatCommand('/waydel')?.kind).toBe('error');
    expect(parseChatCommand('/waylist')).toEqual({ kind: 'waylist' });
  });

  it('deletes by name, case-insensitive, never a pin', () => {
    const list = [
      waypoint({ id: '1', name: 'Home' }),
      waypoint({ id: '2', name: 'home' }),
      waypoint({ id: '3', name: 'Home', kind: KIND_INSTALLATION }),
    ];
    expect(namedWaypoints(list, ' HOME ').map((w) => w.id)).toEqual(['1', '2']);
  });
});

describe('the commands on the wire', () => {
  it('carry what the server declares, the name as text and the ids as 64-bit', () => {
    expect(
      marketWire({ kind: 'waypointCreate', name: 'Home', x: 1.5, z: -2, color: 0xff0000, installation: '0' }),
    ).toEqual({
      name: 'CreateWaypoint',
      values: { name: 'Home', x: 1.5, z: -2, color: 0xff0000, installation: 0n },
    });
    expect(marketWire({ kind: 'waypointDelete', waypoint: '42' })).toEqual({
      name: 'DeleteWaypoint',
      values: { waypoint: 42n },
    });
    expect(marketWire({ kind: 'waypointActivate', waypoint: '0' })).toEqual({
      name: 'SetActiveWaypoint',
      values: { waypoint: 0n },
    });
  });

  it('name the server’s refusals', () => {
    for (const reason of [194, 195, 196]) {
      expect(marketRefusal(reason, 0)).not.toContain('Refused (');
    }
  });
});
