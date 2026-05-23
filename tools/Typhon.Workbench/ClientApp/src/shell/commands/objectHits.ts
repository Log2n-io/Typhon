import type { SelectionObjectType } from '@/stores/useSelectionStore';
import type { SessionKind } from '@/stores/useSessionStore';

/**
 * A palette object-resolution hit (`@`/`#` modes). Selecting it writes the unified bus leaf
 * (`select(type, ref)`) so the Inspector + breadcrumb re-target. Pure builder → unit-tested (suite C).
 */
export interface ObjectHit {
  /** Unique key (also the cmdk value). */
  readonly id: string;
  readonly type: SelectionObjectType;
  /** Bus-select ref (a stable id, or a small descriptor for entity/query). */
  readonly ref: unknown;
  readonly label: string;
  readonly sublabel?: string;
  /** Display group + ordering bucket. */
  readonly group: ObjectGroup;
}

export type ObjectGroup = 'Resources' | 'Components' | 'Archetypes' | 'Systems' | 'Queries';

const GROUP_ORDER: ObjectGroup[] = ['Resources', 'Components', 'Archetypes', 'Systems', 'Queries'];

/** Which object groups are reachable in each session kind (session-kind filtering, suite C law 4). */
const GROUPS_BY_KIND: Record<SessionKind, ObjectGroup[]> = {
  none: [],
  open: ['Resources', 'Components', 'Archetypes'],
  trace: ['Components', 'Archetypes', 'Systems', 'Queries'],
  attach: ['Systems', 'Queries'],
};

export interface ObjectSources {
  readonly resources?: ReadonlyArray<{ id: string; name: string; kind: string; path: string[]; raw: unknown }>;
  readonly components?: ReadonlyArray<{ typeName: string }>;
  readonly archetypes?: ReadonlyArray<{ archetypeId: string; componentTypes?: string[] }>;
  readonly systems?: ReadonlyArray<{ index: number | string; name: string | null }>;
  readonly queries?: ReadonlyArray<{ instanceId: { kind: number | string; localId: number | string } }>;
}

const PER_GROUP_CAP = 25;

function matches(query: string, ...fields: string[]): boolean {
  if (query === '') return true;
  const q = query.toLowerCase();
  return fields.some((f) => f.toLowerCase().includes(q));
}

/**
 * Build object hits for the `@`/`#` palette modes, filtered by `query` and the session kind, in the
 * canonical group order. Sources for groups not reachable in `kind` are ignored even if provided.
 */
export function buildObjectHits(query: string, sources: ObjectSources, kind: SessionKind): ObjectHit[] {
  const allowed = new Set(GROUPS_BY_KIND[kind]);
  const out: ObjectHit[] = [];

  for (const group of GROUP_ORDER) {
    if (!allowed.has(group)) continue;

    if (group === 'Resources' && sources.resources) {
      for (const r of sources.resources) {
        if (out.length >= cap(out, group)) break;
        if (matches(query, r.name, r.kind, r.path.join('/'))) {
          out.push({ id: `resource:${r.id}`, type: 'resource', ref: { resourceId: r.id, kind: r.kind, name: r.name, path: r.path, raw: r.raw }, label: r.name, sublabel: r.path.join(' / '), group });
        }
      }
    } else if (group === 'Components' && sources.components) {
      for (const c of sources.components) {
        if (out.length >= cap(out, group)) break;
        if (matches(query, c.typeName)) {
          out.push({ id: `component:${c.typeName}`, type: 'component', ref: c.typeName, label: c.typeName, group });
        }
      }
    } else if (group === 'Archetypes' && sources.archetypes) {
      for (const a of sources.archetypes) {
        if (out.length >= cap(out, group)) break;
        const composition = (a.componentTypes ?? []).join(', ');
        if (matches(query, a.archetypeId, composition)) {
          out.push({ id: `archetype:${a.archetypeId}`, type: 'archetype', ref: a.archetypeId, label: `Archetype ${a.archetypeId}`, sublabel: composition || undefined, group });
        }
      }
    } else if (group === 'Systems' && sources.systems) {
      for (const s of sources.systems) {
        if (out.length >= cap(out, group)) break;
        const name = s.name ?? `System[${s.index}]`;
        if (matches(query, name)) {
          out.push({ id: `system:${name}`, type: 'system', ref: name, label: name, group });
        }
      }
    } else if (group === 'Queries' && sources.queries) {
      for (const q of sources.queries) {
        if (out.length >= cap(out, group)) break;
        const localId = String(q.instanceId.localId);
        if (matches(query, localId, `query ${localId}`)) {
          out.push({ id: `query:${q.instanceId.kind}:${localId}`, type: 'query', ref: { kind: q.instanceId.kind, localId: q.instanceId.localId }, label: `Query #${localId}`, group });
        }
      }
    }
  }
  return out;
}

// Per-group cap relative to where the current group started.
function cap(out: ObjectHit[], group: ObjectGroup): number {
  const inGroup = out.filter((h) => h.group === group).length;
  return out.length - inGroup + PER_GROUP_CAP;
}
