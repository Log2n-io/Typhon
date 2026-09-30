/* eslint-disable react-refresh/only-export-components --
 * This module exports no component: a set, a predicate, and a HOC that *returns* one. The rule is about Fast Refresh,
 * which has nothing to preserve here — `DockHost` calls the HOC once at module scope to build its component registry,
 * so an edit to this file re-runs that registry regardless. Splitting the four lines of set-and-predicate into a
 * second file to satisfy a lint about hot reload would cost a file and gain nothing.
 */
import type { IDockviewPanelProps } from 'dockview';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * The `Incompatible` shield — which panels it covers, and why it is per panel rather than one sheet over the dock.
 *
 * A session goes `Incompatible` when the database opened but its schema assemblies could not be resolved, so every
 * panel that reads schema or data would render something misleading. Covering them is right. Covering *everything* was
 * not: the shield used to be a single `absolute inset-0` sheet over the whole dockview container
 * (`DockHost.tsx`), and the `IncompatibleBanner`'s primary action — "Locate schema assembly…" — opens the **Options**
 * panel at its Schema tab, which is the one affordance that resolves the state. The banner renders above the dock and
 * stayed clickable; the panel it summoned appeared underneath the sheet, dead, with a `not-allowed` cursor over it.
 * `OptionsPanel` carries a comment defending exactly that path — *"this panel is where the schema banner sends you
 * when a database's schema assembly cannot be found, so a spinner that never resolves strands the one recovery
 * path"* — and the shield stranded it regardless.
 *
 * A sheet over the container cannot make a hole for one panel: it does not know where that panel is. So the shield
 * moves inside the panel, where "not for this one" is simply not rendering it.
 */

/**
 * Panels that stay usable while the session is `Incompatible`.
 *
 * **The rule: the shield says "the data behind this panel cannot be trusted", so a panel that shows no database data
 * has nothing to distrust.** Each id below reads something other than the unopenable database — settings, the local
 * log, the on-disk bundle, a generator — so shielding it buys nothing and costs the user their way out.
 *
 * Deliberately **not** derived from `viewSessionScope(id) === 'any'`, which happens to select the same four ids today.
 * That map answers "which session kind may open this view" — a different question that currently agrees by accident,
 * and scoping Options to `open` later would silently re-shield it. An explicit set fails loudly instead, and its own
 * test pins the membership.
 */
export const PANELS_USABLE_WHEN_INCOMPATIBLE: ReadonlySet<string> = new Set([
  // The exit from the state. See above.
  'Options',
  // The whole point of Integrity is a database that will not open; shielding it hides the tool from its own case.
  'Integrity',
  // The log is where the schema diagnostic is written — evidence *about* the failure, not data *from* it.
  'Logs',
  // Generates a NEW database. Nothing it shows comes from the broken one.
  'DevFixture',
]);

/** Whether a panel is covered while the session is `Incompatible`. */
export function isShieldedWhenIncompatible(id: string): boolean {
  return !PANELS_USABLE_WHEN_INCOMPATIBLE.has(id);
}

/**
 * Wrap a panel component so it is covered while the session is `Incompatible`.
 *
 * Exempt ids are returned unwrapped — no extra element, no extra subscription — so the common case costs nothing and
 * an exempt panel cannot be shielded by a later styling accident.
 */
export function withIncompatibleShield(
  id: string,
  Panel: React.FC<IDockviewPanelProps>,
): React.FC<IDockviewPanelProps> {
  if (!isShieldedWhenIncompatible(id)) {
    return Panel;
  }

  const Shielded: React.FC<IDockviewPanelProps> = (props) => {
    const blocked = useSessionStore((s) => s.sessionState === 'Incompatible');
    return (
      <div className="relative h-full w-full">
        <Panel {...props} />
        {blocked && (
          <div
            data-testid={`incompatible-shield-${id}`}
            className="pointer-events-auto absolute inset-0 cursor-not-allowed bg-background/40"
            aria-hidden="true"
          />
        )}
      </div>
    );
  };
  Shielded.displayName = `IncompatibleShield(${id})`;
  return Shielded;
}
