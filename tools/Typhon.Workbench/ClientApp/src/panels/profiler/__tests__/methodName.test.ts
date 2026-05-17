import { describe, expect, it } from 'vitest';
import { friendlyMethodName } from '../methodName';

describe('friendlyMethodName', () => {
  it('drops the namespace and parameter list → Type.Method', () => {
    expect(friendlyMethodName('AntHill.AntUpdateSystem.Execute(value class Typhon.Engine.TickContext)'))
      .toBe('AntUpdateSystem.Execute');
  });

  it('strips generic-instantiation brackets and arity markers', () => {
    expect(
      friendlyMethodName('Typhon.Engine.ClusterRef`1[System.__Canon].WriteSpatial(value class Typhon.Engine.Comp`1<!!0>,int32,!!0&)'),
    ).toBe('ClusterRef.WriteSpatial');
  });

  it('handles a generic enumerator', () => {
    expect(friendlyMethodName('Typhon.Engine.ClusterEnumerator`1[System.__Canon].MoveNext()'))
      .toBe('ClusterEnumerator.MoveNext');
  });

  it('handles a generic event queue (instantiation contains a dotted type)', () => {
    expect(friendlyMethodName('Typhon.Engine.EventQueue`1[AntHill.FoodPickedUpEvent].Push(!!0)'))
      .toBe('EventQueue.Push');
  });

  it('keeps a property-accessor name as-is', () => {
    expect(friendlyMethodName('AntHill.WorldBounds.get_X()')).toBe('WorldBounds.get_X');
  });

  it('keeps the symbol side of a native module!symbol frame', () => {
    expect(friendlyMethodName('coreclr.dll!JIT_New')).toBe('JIT_New');
  });

  it('returns an unresolved frame unchanged', () => {
    expect(friendlyMethodName('?')).toBe('?');
  });

  it('returns an empty string unchanged', () => {
    expect(friendlyMethodName('')).toBe('');
  });
});
