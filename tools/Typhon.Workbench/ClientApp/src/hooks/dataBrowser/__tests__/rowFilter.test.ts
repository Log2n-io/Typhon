import { describe, it, expect } from 'vitest';
import { parseRowFilter, applyRowFilter } from '../rowFilter';
import type { EntityRow, ComponentValue } from '../types';
import type { PreviewField } from '../previewFields';

describe('parseRowFilter', () => {
  it('parses "field = value"', () => {
    expect(parseRowFilter('X = 0.5')).toEqual({ field: 'X', value: '0.5' });
  });

  it('trims and tolerates missing spaces', () => {
    expect(parseRowFilter('  Health=75 ')).toEqual({ field: 'Health', value: '75' });
  });

  it('allows an empty value (match the empty string)', () => {
    expect(parseRowFilter('Name =')).toEqual({ field: 'Name', value: '' });
  });

  it('returns null with no "=" or no field', () => {
    expect(parseRowFilter('just text')).toBeNull();
    expect(parseRowFilter('   ')).toBeNull();
    expect(parseRowFilter('= 5')).toBeNull();
  });
});

describe('applyRowFilter', () => {
  const columns: PreviewField[] = [
    { typeName: 'Position', fieldId: 0 },
    { typeName: 'Health', fieldId: 0 },
  ];
  const fieldNameOf = (pf: PreviewField) => (pf.typeName === 'Position' ? 'X' : 'Current');
  const formatCell = (v: ComponentValue) => String(v.value);

  const rows: EntityRow[] = [
    { entityId: '1001', preview: [{ fieldId: 0, value: 17.4, raw: '' }, { fieldId: 0, value: 75, raw: '' }] },
    { entityId: '1002', preview: [{ fieldId: 0, value: 4.59, raw: '' }, { fieldId: 0, value: 50, raw: '' }] },
    { entityId: '1017', preview: [{ fieldId: 0, value: 17.9, raw: '' }, { fieldId: 0, value: 75, raw: '' }] },
  ];

  it('returns all rows with no filter', () => {
    expect(applyRowFilter(rows, null, columns, fieldNameOf, formatCell).rows).toHaveLength(3);
  });

  it('filters by a preview column (case-insensitive contains)', () => {
    const out = applyRowFilter(rows, { field: 'current', value: '75' }, columns, fieldNameOf, formatCell);
    expect(out.fieldKnown).toBe(true);
    expect(out.rows.map((r) => r.entityId)).toEqual(['1001', '1017']);
  });

  it('filters by the Entity Id pseudo-column', () => {
    const out = applyRowFilter(rows, { field: 'id', value: '101' }, columns, fieldNameOf, formatCell);
    expect(out.rows.map((r) => r.entityId)).toEqual(['1017']); // contains "101"
  });

  it('flags an unknown field (cannot filter client-side) and returns rows untouched', () => {
    const out = applyRowFilter(rows, { field: 'Nope', value: '1' }, columns, fieldNameOf, formatCell);
    expect(out.fieldKnown).toBe(false);
    expect(out.rows).toHaveLength(3);
  });
});
