/**
 * The join key between a database's components and a capture's recorded topology (#618, design §4.1).
 *
 * ## Why this exists
 *
 * The two artefacts identify a component by different strings, and only one of them is common to both:
 *
 * | Side | Field | Example |
 * |---|---|---|
 * | Capture — system access declarations | `Type.FullName` | `Typhon.Samples.Swg.Shard.Transform` |
 * | Capture — component table | `POCOType?.FullName ?? def.Name` | `Typhon.Samples.Swg.Shard.Transform` |
 * | Database → `ComponentSummaryDto` | `typeName` = the `[Component("…")]` **schema name** | `Swg.Shard.Transform` |
 * | Database → `ComponentSummaryDto` | `fullName` = `POCOType?.FullName` | `Typhon.Samples.Swg.Shard.Transform` |
 * | Capture → `ComponentSummaryDto` | `typeName` = the **leaf** of the full name | `Transform` |
 *
 * `fullName` is the only column that reads the same on both rows, which makes it the join key. Matching on the
 * display name — as the pre-#618 code did — compares a leaf or a schema name against a CLR full name and silently
 * never matches.
 *
 * ## Why leaf matching is not an acceptable fallback
 *
 * It is tempting to fall back to comparing final segments when the full names differ. Two components in different
 * namespaces routinely share a leaf (`Combat.Position` and `Spatial.Position` — the very case
 * {@link buildComponentNameMap} exists to disambiguate), so a leaf fallback would attribute one component's systems
 * to another. §5.7 is explicit that a bridge whose join key did not survive is **absent, not silently wrong**, and a
 * confident wrong answer is the worst outcome available here.
 */

/** The subset of a component summary this module needs. Both providers populate both fields. */
export interface ComponentIdentityLike {
  typeName?: string | null;
  fullName?: string | null;
}

/**
 * The string a capture would have recorded for this component — its CLR full name.
 *
 * Falls back to `typeName` only when `fullName` is missing entirely. That is not a heuristic: a component whose CLR
 * type could not be resolved records `def.Name` on *both* sides (`POCOType?.FullName ?? def.Name` in
 * `ProfilerStaticDataBuilder`, the same expression in `LiveSchemaProvider`), so for that component the schema name
 * genuinely *is* the recorded identity.
 */
export function traceIdentityOf(component: ComponentIdentityLike | null | undefined): string {
  if (!component) return '';
  const full = component.fullName ?? '';
  if (full.length > 0) return full;
  return component.typeName ?? '';
}

/** True when `identity` appears in a capture's access-declaration array. Exact match, for the reason above. */
export function declaresComponent(declarations: readonly string[] | null | undefined, identity: string): boolean {
  if (!declarations || identity.length === 0) return false;
  for (let i = 0; i < declarations.length; i++) {
    if (declarations[i] === identity) return true;
  }
  return false;
}

/**
 * How a database component relates to the attached capture. Three outcomes, because two of them are commonly
 * conflated and mean opposite things to someone reading the panel:
 *
 * - `absent` — the capture has no record of this component. It post-dates the capture, was removed, or was renamed
 *   past what the journal can resolve. **Nothing can be said about which systems touch it.**
 * - `untouched` — the capture knows the component and no system declared access to it. That is a real finding: this
 *   component is written by nobody in the recorded run.
 * - `declared` — systems declared access; the chips are meaningful.
 */
export type ComponentTraceRelation = 'absent' | 'untouched' | 'declared';

/** Names of the components a capture recorded, as a set keyed by the join identity. */
export function captureComponentIdentities(
  componentTypes: readonly { name?: string | null }[] | null | undefined,
): Set<string> {
  const set = new Set<string>();
  for (const c of componentTypes ?? []) {
    const n = c.name ?? '';
    if (n.length > 0) set.add(n);
  }
  return set;
}

/**
 * Classify a component against a capture. `known` comes from {@link captureComponentIdentities};
 * `declaredAnywhere` is whether any system's declarations named it.
 */
export function classifyComponentTraceRelation(
  identity: string,
  known: Set<string>,
  declaredAnywhere: boolean,
): ComponentTraceRelation {
  if (declaredAnywhere) return 'declared';
  return known.has(identity) ? 'untouched' : 'absent';
}
