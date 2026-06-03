import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import { buildComponentNameMap, leafSegment } from '@/libs/componentNames';

// Pure data model for the Schema Explorer (Stage 2, GAP-02 consolidation of the Component + Archetype
// browsers). Kept separate from the panel so the join / filter / sort / Types-totals logic is unit-tested
// without rendering (conformance: the cheapest sufficient layer). The panel is a thin view over this.

export type SchemaExplorerMode = 'archetypes' | 'types';

// ── Archetype-rooted tree (Archetypes mode) ────────────────────────────────────────────────────────
// Archetype nodes with their component *types* as children, per the granularity spine (IA §2.2).

export interface ComponentChildNode {
  /** Globally-unique tree id (react-arborist requires it): `<archUid>/comp:<fullName>`. */
  id: string;
  kind: 'component';
  archetypeId: string;
  fullName: string;
  typeName: string;
  /** The resolved component summary when the type is in the component list; null if unresolved. */
  summary: ComponentSummary | null;
}

export interface ArchetypeTreeNode {
  /** `arch:<archetypeId>`. */
  id: string;
  kind: 'archetype';
  archetype: ArchetypeInfo;
  /** Friendly display label — the shortened archetype name, or `#<id>` for implicit/unnamed archetypes. */
  label: string;
  children: ComponentChildNode[];
}

const LARGE_THRESHOLD = 128;

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
}

/**
 * Friendly archetype display labels — the archetype counterpart to {@link useComponentNames}, computed over the
 * WHOLE archetype set so the shortening is stable regardless of the active filter. Each named archetype's CLR
 * name is shortened to its shortest dot-suffix that is still unique across the set (`...Fixtures.PlayerArch` →
 * `PlayerArch`); colliding leaves keep one more segment. Returns `id → label`, named archetypes only — the caller
 * falls back to `#<id>` for implicit/anonymous ones (which have no name to show).
 */
export function buildArchetypeLabelMap(archetypes: ArchetypeInfo[]): Map<string, string> {
  const named = archetypes.filter((a) => a.name);
  const shortByName = buildComponentNameMap(named.map((a) => a.name));
  const byId = new Map<string, string>();
  for (const a of named) {
    byId.set(a.archetypeId, shortByName.get(a.name) ?? leafSegment(a.name));
  }
  return byId;
}

/**
 * Build the archetype-rooted tree by joining each archetype's `componentTypes` (full names) against the
 * component list. Unresolved types still render (fallback to the stripped name) so the tree never hides a
 * declared component just because the summary endpoint lagged. Each archetype node carries its friendly
 * `label` (the shortened name, or `#<id>` when unnamed) so the row renders the name instead of the bare id;
 * pass a label map from {@link buildArchetypeLabelMap} (built over the full set), else every node falls back to `#<id>`.
 */
export function buildArchetypeTree(
  archetypes: ArchetypeInfo[],
  components: ComponentSummary[],
  labelById?: Map<string, string>,
): ArchetypeTreeNode[] {
  const byFullName = new Map(components.map((c) => [c.fullName, c]));
  return archetypes.map((archetype) => {
    const archUid = `arch:${archetype.archetypeId}`;
    const children: ComponentChildNode[] = archetype.componentTypes.map((fullName) => {
      const summary = byFullName.get(fullName) ?? null;
      return {
        id: `${archUid}/comp:${fullName}`,
        kind: 'component',
        archetypeId: archetype.archetypeId,
        fullName,
        typeName: summary?.typeName ?? stripNamespace(fullName),
        summary,
      };
    });
    const label = labelById?.get(archetype.archetypeId) ?? `#${archetype.archetypeId}`;
    return { id: archUid, kind: 'archetype', archetype, label, children };
  });
}

/**
 * Filter the archetype tree by a query: keep an archetype if its id matches, or any of its component
 * children match (by type or full name) — matching children are kept, an id-match keeps all children.
 * Empty query returns the tree unchanged.
 */
export function filterArchetypeTree(tree: ArchetypeTreeNode[], query: string): ArchetypeTreeNode[] {
  const t = query.trim().toLowerCase();
  if (t.length === 0) return tree;
  const out: ArchetypeTreeNode[] = [];
  for (const node of tree) {
    const idMatch = `#${node.archetype.archetypeId}`.includes(t) || String(node.archetype.archetypeId).includes(t);
    if (idMatch) {
      out.push(node);
      continue;
    }
    const kids = node.children.filter(
      (c) => c.typeName.toLowerCase().includes(t) || c.fullName.toLowerCase().includes(t),
    );
    if (kids.length > 0) out.push({ ...node, children: kids });
  }
  return out;
}

export interface ArchetypeFilters {
  noEntities?: boolean;
  legacy?: boolean;
}

export function applyArchetypeFilters(list: ArchetypeInfo[], f: ArchetypeFilters): ArchetypeInfo[] {
  return list.filter((a) => {
    if (f.noEntities && a.entityCount !== 0) return false;
    if (f.legacy && a.storageMode !== 'legacy') return false;
    return true;
  });
}

// ── Types mode (flat component list) ───────────────────────────────────────────────────────────────
// Resolves open question SE-2: Types-mode counts are TOTALS across archetypes — `ComponentSummary.entityCount`
// is already the type-global total, and `archetypeCount` is the "used in N archetypes" number. Per-archetype
// counts live under the archetype tree (Archetypes mode).

export interface ComponentFilters {
  noEntities?: boolean;
  noIndexes?: boolean;
  large?: boolean;
  indexed?: boolean;
}

export function applyComponentFilters(
  list: ComponentSummary[],
  f: ComponentFilters,
  largeThreshold = LARGE_THRESHOLD,
): ComponentSummary[] {
  return list.filter((c) => {
    if (f.noEntities && c.entityCount !== 0) return false;
    if (f.noIndexes && c.indexCount !== 0) return false;
    if (f.indexed && c.indexCount === 0) return false;
    if (f.large && c.storageSize < largeThreshold) return false;
    return true;
  });
}

export type SortDir = 'asc' | 'desc';
export type ComponentSortKey = 'typeName' | 'storageSize' | 'fieldCount' | 'entityCount' | 'indexCount' | 'archetypeCount';

export function sortComponents(
  list: ComponentSummary[],
  key: ComponentSortKey,
  dir: SortDir,
): ComponentSummary[] {
  const read = (c: ComponentSummary): string | number => {
    switch (key) {
      case 'typeName': return c.typeName;
      case 'storageSize': return c.storageSize;
      case 'fieldCount': return c.fieldCount;
      case 'entityCount': return c.entityCount;
      case 'indexCount': return c.indexCount;
      case 'archetypeCount': return c.archetypeCount ?? 0;
    }
  };
  return [...list].sort((a, b) => {
    const va = read(a);
    const vb = read(b);
    if (typeof va === 'string' && typeof vb === 'string') {
      return dir === 'asc' ? va.localeCompare(vb) : vb.localeCompare(va);
    }
    return dir === 'asc' ? Number(va) - Number(vb) : Number(vb) - Number(va);
  });
}
