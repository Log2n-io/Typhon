// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  PANELS_USABLE_WHEN_INCOMPATIBLE,
  isShieldedWhenIncompatible,
  withIncompatibleShield,
} from '../incompatibleShield';

/**
 * The `Incompatible` shield, after it stopped being one sheet over the whole dock.
 *
 * **The bug this fixture exists for, reported from a live session:** opening a database whose schema assemblies cannot
 * be found puts the session in `Incompatible`, and the banner's primary action — "Locate schema assembly…" — opens the
 * Options panel at its Schema tab. That is the only affordance that resolves the state. The shield was a single
 * `absolute inset-0` sheet over the dockview container, so the banner stayed clickable (it renders above the dock) and
 * the panel it summoned came up underneath the sheet: a `not-allowed` cursor everywhere on the pane and no way to
 * register the directory. The state's only exit was unreachable from inside it.
 */

const Panel = (() => <div data-testid="panel-body">panel</div>) as unknown as React.FC<IDockviewPanelProps>;
const props = {} as IDockviewPanelProps;

function setState(state: string | null): void {
  useSessionStore.setState({ sessionState: state as never });
}

beforeEach(() => {
  setState(null);
});

afterEach(cleanup);

describe('which panels the shield covers', () => {
  it('exempts exactly the panels that show no database data', () => {
    expect([...PANELS_USABLE_WHEN_INCOMPATIBLE].sort()).toEqual(['DevFixture', 'Integrity', 'Logs', 'Options']);
  });

  /**
   * The regression assertion. Options is not one exemption among four — it is the one the banner points at, so a
   * change that drops it silently restores the dead end.
   */
  it('never shields Options, because the banner sends you there to fix the very state', () => {
    expect(isShieldedWhenIncompatible('Options')).toBe(false);
  });

  it('shields the panels that DO read the database', () => {
    for (const id of ['SchemaExplorer', 'DataBrowserEntities', 'DbMap', 'QueryConsole', 'Realms']) {
      expect(isShieldedWhenIncompatible(id)).toBe(true);
    }
  });

  /**
   * An exempt panel is returned unwrapped rather than wrapped-and-never-shielded: no extra element, no store
   * subscription, and no way for a later styling change to cover it by accident.
   */
  it('returns an exempt panel untouched, not a wrapper that happens not to shield', () => {
    expect(withIncompatibleShield('Options', Panel)).toBe(Panel);
    expect(withIncompatibleShield('DbMap', Panel)).not.toBe(Panel);
  });
});

describe('what the shield does when mounted', () => {
  it('covers a data panel while the session is Incompatible', () => {
    setState('Incompatible');
    const Shielded = withIncompatibleShield('DbMap', Panel);

    render(<Shielded {...props} />);

    expect(screen.getByTestId('panel-body')).not.toBeNull();
    expect(screen.getByTestId('incompatible-shield-DbMap')).not.toBeNull();
  });

  /** The shield is for one state only — every other session state leaves the panel exactly as it was. */
  it('is absent in every other session state', () => {
    for (const state of [null, 'Open', 'Attached', 'MigrationRequired']) {
      setState(state);
      const Shielded = withIncompatibleShield('DbMap', Panel);
      const { unmount } = render(<Shielded {...props} />);

      expect(screen.queryByTestId('incompatible-shield-DbMap')).toBeNull();
      unmount();
    }
  });

  /** The whole point: the recovery panel renders bare, with nothing over it, in the state it exists to resolve. */
  it('leaves Options uncovered while Incompatible', () => {
    setState('Incompatible');
    const Shielded = withIncompatibleShield('Options', Panel);

    render(<Shielded {...props} />);

    expect(screen.getByTestId('panel-body')).not.toBeNull();
    expect(screen.queryByTestId('incompatible-shield-Options')).toBeNull();
  });
});
