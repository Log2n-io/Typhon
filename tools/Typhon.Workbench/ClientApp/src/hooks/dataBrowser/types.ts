/**
 * Local raw + normalized shapes for the Data Browser endpoints. The Data Browser uses raw `customFetch` (not the Orval
 * generated client), so the wire shapes are declared here rather than imported from `@/api/generated`. `entityId` is a
 * decimal string — the raw 64-bit packed value exceeds JS Number precision (see the server EntityRowDto).
 */

// A JSON-native scalar (number | boolean | string) for primitive fields, or null for complex types (raw hex carries them).
export type FieldValue = number | boolean | string | null;

export interface ComponentValueRaw {
  fieldId: number;
  value: FieldValue;
  raw: string;
}

export interface EntityRowRaw {
  entityId: string;
  preview: ComponentValueRaw[] | null;
}

export interface EntityPageRaw {
  archetypeId: string;
  revision: number;
  totalCount: number;
  offset: number;
  entities: EntityRowRaw[] | null;
  hasMore: boolean;
}

export interface ComponentInstanceRaw {
  typeName: string;
  enabled: boolean;
  fields: ComponentValueRaw[] | null;
}

export interface EntityDetailRaw {
  entityId: string;
  archetypeId: string;
  revision: number;
  components: ComponentInstanceRaw[] | null;
}

export interface ComponentValue {
  fieldId: number;
  value: FieldValue;
  raw: string;
}

export interface EntityRow {
  entityId: string;
  preview: ComponentValue[];
}

export interface EntityPage {
  archetypeId: string;
  revision: number;
  totalCount: number;
  offset: number;
  entities: EntityRow[];
  hasMore: boolean;
}

export interface ComponentInstance {
  typeName: string;
  enabled: boolean;
  fields: ComponentValue[];
}

export interface EntityDetail {
  entityId: string;
  archetypeId: string;
  revision: number;
  components: ComponentInstance[];
}

export function normalizeEntityPage(raw: EntityPageRaw): EntityPage {
  return {
    archetypeId: raw.archetypeId ?? '',
    revision: raw.revision ?? 0,
    totalCount: raw.totalCount ?? 0,
    offset: raw.offset ?? 0,
    entities: (raw.entities ?? []).map((e) => ({ entityId: e.entityId, preview: e.preview ?? [] })),
    hasMore: raw.hasMore ?? false,
  };
}

export function normalizeEntityDetail(raw: EntityDetailRaw): EntityDetail {
  return {
    entityId: raw.entityId,
    archetypeId: raw.archetypeId,
    revision: raw.revision ?? 0,
    components: (raw.components ?? []).map((c) => ({
      typeName: c.typeName,
      enabled: c.enabled,
      fields: c.fields ?? [],
    })),
  };
}
