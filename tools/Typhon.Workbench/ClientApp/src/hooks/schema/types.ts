import type {
  ArchetypeInfoDto,
  ComponentSchemaDto,
  ComponentSummaryDto,
  FieldDto,
  IndexInfoDto,
  SystemRelationshipDto,
  SystemRelationshipsResponseDto,
} from '@/api/generated/model';

/**
 * Clean, non-null-shaped mirrors of the Orval-generated DTOs. Orval emits ASP.NET Core records with
 * `string | null` and `number | string` because C# non-nullable reference types aren't propagated
 * to OpenAPI by default. The hooks normalize at the boundary so panels/renderer work with the
 * shape the server actually returns.
 */
export interface ComponentSummary {
  typeName: string;
  fullName: string;
  storageSize: number;
  fieldCount: number;
  archetypeCount: number | null;
  entityCount: number;
  indexCount: number;
  /** MVCC storage mode — "Versioned" / "SingleVersion" / "Transient" (GAP-25). */
  storageMode: string;
}

export interface ComponentSchema {
  typeName: string;
  fullName: string;
  storageSize: number;
  totalSize: number;
  allowMultiple: boolean;
  revision: number;
  fields: Field[];
  /** MVCC storage mode — "Versioned" / "SingleVersion" / "Transient" (GAP-25). */
  storageMode: string;
}

export interface Field {
  name: string;
  typeName: string;
  typeFullName: string;
  offset: number;
  size: number;
  fieldId: number;
  isIndexed: boolean;
  indexAllowsMultiple: boolean;
}

const toNumber = (v: number | string | null | undefined, fallback = 0): number =>
  v == null ? fallback : typeof v === 'number' ? v : Number(v);

const toString = (v: string | null | undefined, fallback = ''): string =>
  v == null ? fallback : v;

export function normalizeSummary(raw: ComponentSummaryDto): ComponentSummary {
  return {
    typeName: toString(raw.typeName),
    fullName: toString(raw.fullName),
    storageSize: toNumber(raw.storageSize),
    fieldCount: toNumber(raw.fieldCount),
    archetypeCount: raw.archetypeCount == null ? null : toNumber(raw.archetypeCount),
    entityCount: toNumber(raw.entityCount),
    indexCount: toNumber(raw.indexCount),
    storageMode: toString(raw.storageMode),
  };
}

export function normalizeField(raw: FieldDto): Field {
  return {
    name: toString(raw.name),
    typeName: toString(raw.typeName),
    typeFullName: toString(raw.typeFullName),
    offset: toNumber(raw.offset),
    size: toNumber(raw.size),
    fieldId: toNumber(raw.fieldId),
    isIndexed: raw.isIndexed,
    indexAllowsMultiple: raw.indexAllowsMultiple,
  };
}

export function normalizeSchema(raw: ComponentSchemaDto): ComponentSchema {
  return {
    typeName: toString(raw.typeName),
    fullName: toString(raw.fullName),
    storageSize: toNumber(raw.storageSize),
    totalSize: toNumber(raw.totalSize),
    allowMultiple: raw.allowMultiple,
    revision: toNumber(raw.revision),
    fields: (raw.fields ?? []).map(normalizeField),
    storageMode: toString(raw.storageMode),
  };
}

// ── Phase 2 types ───────────────────────────────────────────────────────────

export type StorageMode = 'cluster' | 'legacy';

export interface ArchetypeInfo {
  archetypeId: string;
  /** Registered archetype name (CLR) when one exists; '' for implicit/anonymous archetypes. */
  name: string;
  componentTypes: string[];
  entityCount: number;
  componentSize: number;
  storageMode: StorageMode;
  chunkCount: number;
  chunkCapacity: number;
  occupancyPct: number;
}

export interface IndexInfo {
  fieldName: string;
  fieldOffset: number;
  fieldSize: number;
  allowsMultiple: boolean;
  indexType: string;
}

export type SystemAccess = 'read' | 'reactive';

export interface SystemRelationship {
  systemName: string;
  systemType: string;
  access: SystemAccess;
  queryViewSchema: string[];
  changeFilterTypes: string[];
}

export interface SystemRelationshipsResponse {
  runtimeHosted: boolean;
  systems: SystemRelationship[];
}

export function normalizeArchetype(raw: ArchetypeInfoDto): ArchetypeInfo {
  const mode = toString(raw.storageMode);
  return {
    archetypeId: toString(raw.archetypeId),
    name: toString(raw.name),
    componentTypes: (raw.componentTypes ?? []).map((s) => s ?? ''),
    entityCount: toNumber(raw.entityCount),
    componentSize: toNumber(raw.componentSize),
    storageMode: mode === 'cluster' ? 'cluster' : 'legacy',
    chunkCount: toNumber(raw.chunkCount),
    chunkCapacity: toNumber(raw.chunkCapacity),
    occupancyPct: toNumber(raw.occupancyPct),
  };
}

export function normalizeIndex(raw: IndexInfoDto): IndexInfo {
  return {
    fieldName: toString(raw.fieldName),
    fieldOffset: toNumber(raw.fieldOffset),
    fieldSize: toNumber(raw.fieldSize),
    allowsMultiple: raw.allowsMultiple,
    indexType: toString(raw.indexType),
  };
}

export function normalizeSystemRelationship(raw: SystemRelationshipDto): SystemRelationship {
  const access = toString(raw.access);
  return {
    systemName: toString(raw.systemName),
    systemType: toString(raw.systemType),
    access: access === 'read' ? 'read' : 'reactive',
    queryViewSchema: (raw.queryViewSchema ?? []).map((s) => s ?? ''),
    changeFilterTypes: (raw.changeFilterTypes ?? []).map((s) => s ?? ''),
  };
}

export function normalizeSystemRelationshipsResponse(
  raw: SystemRelationshipsResponseDto,
): SystemRelationshipsResponse {
  return {
    runtimeHosted: raw.runtimeHosted,
    systems: (raw.systems ?? []).map(normalizeSystemRelationship),
  };
}
