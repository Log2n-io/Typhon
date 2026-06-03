/**
 * Map a Resource-tree node to the component it stores, for the "Open in → Data Browser" handoff from a
 * Resource (GAP-03 / AC2.6). A ComponentTable resource is named `ComponentTable_<RegisteredComponentName>`
 * by the engine resource graph (the registered `def.Name`, e.g. `Typhon.Workbench.Fixture.Player` — which is
 * NOT the CLR `fullName`). We recover that registered name. It's a naming-convention coupling, so callers
 * must treat the result as a *hint*: resolve it against the live component list (by `typeName`) and show the
 * verb only when it matches — a convention change then makes the verb vanish (PC-6), never breaks the handoff.
 */
const COMPONENT_TABLE_PREFIX = 'ComponentTable_';

export function componentNameFromResource(kind: string, name: string): string | null {
  if (kind !== 'ComponentTable' || !name.startsWith(COMPONENT_TABLE_PREFIX)) {
    return null;
  }
  const recovered = name.slice(COMPONENT_TABLE_PREFIX.length).trim();
  return recovered.length > 0 ? recovered : null;
}
