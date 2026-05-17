/**
 * Friendly display name for a profiler frame's full method declaration.
 *
 * A captured CPU-sample frame carries the full CLR declaration — e.g.
 * `AntHill.AntUpdateSystem.Execute(value class Typhon.Engine.TickContext)` or
 * `Typhon.Engine.ClusterRef`1[System.__Canon].WriteSpatial(value class Typhon.Engine.Comp`1<!!0>,int32,!!0&)`
 * — far too verbose for a tree row or a breadcrumb crumb. `friendlyMethodName` reduces it to
 * `Type.Method` (`AntUpdateSystem.Execute`, `ClusterRef.WriteSpatial`): the parameter list, the
 * namespace prefix, generic-instantiation brackets and arity markers are all dropped. Callers keep
 * the full declaration as a tooltip.
 */

/** Reduces a full method declaration to a concise `Type.Method` display name. */
export function friendlyMethodName(decl: string): string {
  if (!decl) {
    return decl;
  }

  // Drop the parameter list — everything from the first '('. CLR generic args use [] / <>, never (),
  // so the first paren reliably starts the parameter list.
  const paren = decl.indexOf('(');
  let path = paren >= 0 ? decl.slice(0, paren) : decl;

  // Drop generic-instantiation brackets [...] / <...> — innermost-first so nested instantiations
  // collapse cleanly — and the `N arity markers.
  let prev = '';
  while (prev !== path) {
    prev = path;
    path = path.replace(/\[[^[\]]*\]/g, '').replace(/<[^<>]*>/g, '');
  }
  path = path.replace(/`\d+/g, '');

  // Native frames render as "module!symbol" — keep the symbol side.
  const bang = path.lastIndexOf('!');
  if (bang >= 0) {
    path = path.slice(bang + 1);
  }

  // Keep the last two dotted segments — Type.Method.
  const segments = path.split('.').filter((s) => s.length > 0);
  if (segments.length === 0) {
    return decl;
  }
  if (segments.length === 1) {
    return segments[0];
  }
  return `${segments[segments.length - 2]}.${segments[segments.length - 1]}`;
}
