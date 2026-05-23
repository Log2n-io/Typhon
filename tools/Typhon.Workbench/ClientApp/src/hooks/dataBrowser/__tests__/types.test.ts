import { describe, it, expect } from 'vitest';
import { normalizeEntityPage, normalizeEntityDetail } from '../types';

describe('dataBrowser normalizers', () => {
  it('normalizeEntityPage defaults a null entities array to empty', () => {
    const page = normalizeEntityPage({
      archetypeId: '5',
      revision: 3,
      totalCount: 2,
      offset: 0,
      entities: null,
      hasMore: false,
    });
    expect(page.entities).toEqual([]);
    expect(page.totalCount).toBe(2);
    expect(page.archetypeId).toBe('5');
  });

  it('normalizeEntityPage maps rows and defaults a null preview', () => {
    const page = normalizeEntityPage({
      archetypeId: '5',
      revision: 3,
      totalCount: 1,
      offset: 0,
      entities: [{ entityId: '999', preview: null }],
      hasMore: true,
    });
    expect(page.entities[0].entityId).toBe('999');
    expect(page.entities[0].preview).toEqual([]);
    expect(page.hasMore).toBe(true);
  });

  it('normalizeEntityDetail maps components and decoded fields', () => {
    const detail = normalizeEntityDetail({
      entityId: '1',
      archetypeId: '5',
      revision: 2,
      components: [
        { typeName: 'Foo', enabled: true, fields: [{ fieldId: 0, value: 42, raw: '2a000000' }] },
        { typeName: 'Bar', enabled: false, fields: null },
      ],
    });
    expect(detail.components).toHaveLength(2);
    expect(detail.components[0].typeName).toBe('Foo');
    expect(detail.components[0].fields[0].value).toBe(42);
    expect(detail.components[1].enabled).toBe(false);
    expect(detail.components[1].fields).toEqual([]);
  });

  it('normalizeEntityDetail defaults a null components array', () => {
    const detail = normalizeEntityDetail({ entityId: '1', archetypeId: '5', revision: 0, components: null });
    expect(detail.components).toEqual([]);
  });
});
