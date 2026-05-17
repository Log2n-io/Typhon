import { describe, expect, it } from 'vitest';
import { matchMethodName } from '../methodNameMatch';

describe('matchMethodName', () => {
  it('empty query returns [] (matches everything, highlights nothing)', () => {
    expect(matchMethodName('AntHill.AntUpdateSystem.Execute', '')).toEqual([]);
  });

  it('CamelCase hump matches within a single word', () => {
    // A(8)nt U(11)pdate S(17)ystem — offsets into the full declaration.
    expect(matchMethodName('AntHill.AntUpdateSystem.Execute', 'AUS')).toEqual([8, 11, 17]);
  });

  it('does NOT stitch a hump match across words', () => {
    // The whole point: A of Async + u of unsigned + S of System must NOT match — they are three
    // different words. No single word has humps A-U-S.
    expect(matchMethodName('ReadAsync(Memory`1<unsigned>, value class System)', 'AUS')).toBeNull();
  });

  it('hump matching is case-sensitive — lowercase does not hump-match', () => {
    expect(matchMethodName('AntHill.AntUpdateSystem.Execute', 'aus')).toBeNull();
  });

  it('falls back to a case-insensitive substring within a word', () => {
    // "writespatial".indexOf("spatial") === 5; word WriteSpatial starts at offset 14 → 19…25.
    expect(matchMethodName('Typhon.Engine.WriteSpatial', 'spatial')).toEqual([19, 20, 21, 22, 23, 24, 25]);
  });

  it('substring match is confined to one word too', () => {
    // "executevalue" spans the Execute word and the value word — no single word contains it.
    expect(matchMethodName('Foo.Execute(value)', 'executevalue')).toBeNull();
  });

  it('the trailing uppercase of an acronym run is a hump (the P in HTMLParser)', () => {
    expect(matchMethodName('System.HTMLParser', 'HP')).toEqual([7, 11]);
  });

  it('returns null when no word matches', () => {
    expect(matchMethodName('WriteSpatial', 'xyz')).toBeNull();
  });
});
