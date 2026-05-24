import { describe, it, expect } from 'vitest';
import { defaultPreviewFields, samePreviewField, serializePreview } from '../previewFields';
import type { ComponentSchema, Field } from '@/hooks/schema/types';

function field(name: string, fieldId: number, typeName: string): Field {
  return { name, fieldId, typeName, typeFullName: typeName, offset: 0, size: 4, isIndexed: false, indexAllowsMultiple: false };
}
function schema(typeName: string, fields: Field[]): ComponentSchema {
  return { typeName, fullName: typeName, storageSize: 0, totalSize: 0, allowMultiple: false, revision: 1, fields, storageMode: 'Versioned' };
}

describe('defaultPreviewFields', () => {
  const schemas = new Map<string, ComponentSchema>([
    ['A', schema('A', [field('X', 0, 'Int'), field('Box', 1, 'AABB3F'), field('Y', 2, 'Float')])],
    ['B', schema('B', [field('Flag', 0, 'Boolean')])],
  ]);

  it('picks scalar fields in component then offset order, skipping complex types', () => {
    const fields = defaultPreviewFields(['A', 'B'], schemas);
    // Box (AABB3F) is skipped; result is A.X, A.Y, B.Flag.
    expect(fields).toEqual([
      { typeName: 'A', fieldId: 0 },
      { typeName: 'A', fieldId: 2 },
      { typeName: 'B', fieldId: 0 },
    ]);
  });

  it('caps at the requested max', () => {
    const fields = defaultPreviewFields(['A', 'B'], schemas, 1);
    expect(fields).toEqual([{ typeName: 'A', fieldId: 0 }]);
  });

  it('returns empty until schemas are available', () => {
    expect(defaultPreviewFields(['A'], new Map())).toEqual([]);
    expect(defaultPreviewFields([], schemas)).toEqual([]);
  });
});

describe('preview field helpers', () => {
  it('serializePreview encodes typeName:fieldId comma-joined', () => {
    expect(serializePreview([{ typeName: 'A', fieldId: 0 }, { typeName: 'B', fieldId: 2 }])).toBe('A:0,B:2');
    expect(serializePreview([])).toBe('');
  });

  it('samePreviewField compares both parts', () => {
    expect(samePreviewField({ typeName: 'A', fieldId: 1 }, { typeName: 'A', fieldId: 1 })).toBe(true);
    expect(samePreviewField({ typeName: 'A', fieldId: 1 }, { typeName: 'A', fieldId: 2 })).toBe(false);
    expect(samePreviewField({ typeName: 'A', fieldId: 1 }, { typeName: 'B', fieldId: 1 })).toBe(false);
  });
});
