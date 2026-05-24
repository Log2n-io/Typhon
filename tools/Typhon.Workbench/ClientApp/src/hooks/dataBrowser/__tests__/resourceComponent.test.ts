import { describe, it, expect } from 'vitest';
import { componentNameFromResource } from '../resourceComponent';

describe('componentNameFromResource', () => {
  it('recovers the registered component name from a ComponentTable resource', () => {
    expect(componentNameFromResource('ComponentTable', 'ComponentTable_Typhon.Workbench.Fixture.CompA')).toBe(
      'Typhon.Workbench.Fixture.CompA',
    );
  });

  it('returns null for non-ComponentTable resources', () => {
    expect(componentNameFromResource('Memory', 'PageCache')).toBeNull();
    expect(componentNameFromResource('WAL', 'WalManager')).toBeNull();
  });

  it('returns null when the name lacks the expected prefix or is empty after it', () => {
    expect(componentNameFromResource('ComponentTable', 'WeirdName')).toBeNull();
    expect(componentNameFromResource('ComponentTable', 'ComponentTable_')).toBeNull();
  });
});
