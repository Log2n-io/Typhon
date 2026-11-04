import { describe, expect, it } from 'vitest';
import { marketWire, type MarketCommand } from '../src/data/market';
import { MARKET_COMMAND_NAMES } from '../src/data/source';

/** One command of every kind: a `Record` over the kinds, so a kind added to `MarketCommand` without a sample here does not compile. */
const SAMPLES: { readonly [K in MarketCommand['kind']]: Extract<MarketCommand, { kind: K }> } = {
  list: { kind: 'list', template: 1, mk: 1, quantity: 1, unitPrice: 1 },
  cancel: { kind: 'cancel', listing: '1' },
  buy: { kind: 'buy', listing: '1', unitPrice: 1 },
  collect: { kind: 'collect', listing: '1' },
  vendor: { kind: 'vendor', template: 1, mk: 1, quantity: 1 },
  standard: { kind: 'standard', template: 1, price: 1 },
  equip: { kind: 'equip', item: '1' },
  unequip: { kind: 'unequip', slot: 101 },
  craftStart: { kind: 'craftStart', schematic: 1, mks: [], parts: [] },
  craftPoint: { kind: 'craftPoint', line: 0 },
  craftEnhance: { kind: 'craftEnhance', mk: 1 },
  craftFinish: { kind: 'craftFinish', schematic: 1 },
  repair: { kind: 'repair', item: '1' },
  salvage: { kind: 'salvage', item: '1' },
  refine: { kind: 'refine', template: 1, mk: 1, count: 1 },
  pack: { kind: 'pack', item: '1', into: '2' },
  unpack: { kind: 'unpack', item: '1', count: 1 },
  move: { kind: 'move', item: '1', slot: 0, count: 1 },
  discard: { kind: 'discard', item: '1', count: 1 },
  mailRead: { kind: 'mailRead', mail: '1' },
  mailDelete: { kind: 'mailDelete', mail: '0' },
  tell: { kind: 'tell', to: 'han', text: 'hello' },
  installPlace: { kind: 'installPlace', item: '1', family: 1 },
  installCollect: { kind: 'installCollect', installation: '1' },
  installPay: { kind: 'installPay', installation: '1', credits: 1 },
  installPickUp: { kind: 'installPickUp', installation: '1' },
  waypointCreate: { kind: 'waypointCreate', name: 'w', x: 0, z: 0, color: 0, installation: '0' },
  waypointDelete: { kind: 'waypointDelete', waypoint: '1' },
  waypointActivate: { kind: 'waypointActivate', waypoint: '1' },
};

describe('the commands a market command is sent as', () => {
  it('are every one looked up at bind: a name the session does not look up is never sent', () => {
    const names = Object.values(SAMPLES).map((c) => marketWire(c)?.name);
    expect(names.every((n) => n !== undefined)).toBe(true);
    expect(names.filter((n) => !MARKET_COMMAND_NAMES.includes(n ?? ''))).toEqual([]);
  });
});
