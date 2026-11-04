import { describe, expect, it } from 'vitest';

import { InstallationState, paidText, parseInstallations, stateText, withinReach } from '../src/data/installations';
import { marketRefusal, marketWire, MARKET_REASONS } from '../src/data/market';

const ANSWER = {
  installations: [
    {
      id: '77',
      kind: 1,
      family: 3,
      familyName: 'Metal',
      x: 100,
      z: 200,
      rate: 360,
      hopper: 500,
      held: 42,
      state: 1,
      paidSeconds: 5400,
    },
    { id: 'bad' },
  ],
  lots: 1,
  lotsAllowed: 10,
  upkeepPerHour: 100,
};

describe('installations (crafting-build.md § 10)', () => {
  it('reads the endpoint, dropping what is malformed', () => {
    const list = parseInstallations(ANSWER);
    expect(list?.installations.map((i) => [i.id, i.held, i.state])).toEqual([['77', 42, InstallationState.Running]]);
    expect([list?.lots, list?.lotsAllowed, list?.upkeepPerHour]).toEqual([1, 10, 100]);
    expect(parseInstallations({ installations: 'no' })).toBeNull();
  });

  it('says its state, its upkeep left, and whether it is in reach', () => {
    expect([stateText(1), stateText(2), stateText(3)]).toEqual(['running', 'hopper full', 'unpaid — stopped']);
    expect([paidText(0), paidText(5400), paidText(600), paidText(30)]).toEqual([
      'none',
      '1 h 30 min',
      '10 min',
      '1 min',
    ]);
    const i = parseInstallations(ANSWER)?.installations[0];
    expect(i !== undefined && withinReach(i, 120, 210)).toBe(true);
    expect(i !== undefined && withinReach(i, 140, 230)).toBe(false);
  });

  it('goes out as the four commands, and its refusals say why', () => {
    expect(marketWire({ kind: 'installPlace', item: '5', family: 3 })).toEqual({
      name: 'PlaceInstallation',
      values: { item: 5n, family: 3 },
    });
    expect(marketWire({ kind: 'installCollect', installation: '77' })).toEqual({
      name: 'CollectInstallation',
      values: { installation: 77n },
    });
    expect(marketWire({ kind: 'installPay', installation: '77', credits: 100 })).toEqual({
      name: 'PayUpkeep',
      values: { installation: 77n, credits: 100 },
    });
    expect(marketWire({ kind: 'installPickUp', installation: '77' })).toEqual({
      name: 'PickUpInstallation',
      values: { installation: 77n },
    });
    expect([188, 189, 190, 191, 192, 193].every((r) => r in MARKET_REASONS)).toBe(true);
    expect(marketRefusal(191)).toContain('10');
  });
});
