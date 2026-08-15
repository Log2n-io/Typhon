// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';

// Mutable holder so each test can swap the topology the (mocked) data hook returns.
const hoisted = vi.hoisted(() => ({ topology: null as unknown }));

vi.mock('@/hooks/data/useTopology', () => ({
  useTopology: () => ({ data: hoisted.topology }),
}));

// Imported after the mock is registered (vi.mock calls are hoisted above all imports by vitest).
import AccessChips from '../AccessChips';

/** A capture's topology as the wire delivers it: declarations hold CLR full names. */
function topologyWith(systems: Record<string, unknown>[], componentNames: string[]) {
  return { systems, componentTypes: componentNames.map((name) => ({ name })) };
}

const MOVEMENT = {
  name: 'Movement',
  writes: ['Typhon.Samples.Swg.Shard.Transform'],
  sideWrites: [],
  reads: ['Typhon.Samples.Swg.Shard.Velocity'],
  readsFresh: [],
  readsSnapshot: [],
  additionalReads: [],
};

const DAMAGE = {
  name: 'Damage',
  writes: [],
  sideWrites: [],
  reads: [],
  readsFresh: [],
  readsSnapshot: ['Typhon.Samples.Swg.Shard.Transform'],
  additionalReads: [],
};

describe('AccessChips (#618 §4.1)', () => {
  beforeEach(() => {
    hoisted.topology = null;
    useSessionStore.setState({ sessionId: 'sid', kind: 'open', capabilities: ['database', 'profiler'] });
  });
  afterEach(cleanup);

  it('names the systems that touch a component, joined on its full name', () => {
    // The case that never worked before #618: the panel holds the DATABASE's schema name, the capture holds the CLR
    // full name. Joining on fullName is what makes this render at all.
    hoisted.topology = topologyWith([MOVEMENT, DAMAGE], ['Typhon.Samples.Swg.Shard.Transform']);

    render(<AccessChips component={{ typeName: 'Swg.Shard.Transform', fullName: 'Typhon.Samples.Swg.Shard.Transform' }} />);

    expect(screen.getByTestId('access-chips')).toBeTruthy();
    expect(screen.getByText('Movement')).toBeTruthy();
    expect(screen.getByText('Damage')).toBeTruthy();
    expect(screen.getByText('writes:')).toBeTruthy();
    expect(screen.getByText('reads snapshot:')).toBeTruthy();
  });

  it('does not attribute another component’s systems on a shared leaf name', () => {
    hoisted.topology = topologyWith(
      [{ ...MOVEMENT, writes: ['Game.Combat.Position'] }],
      ['Game.Combat.Position', 'Game.Spatial.Position'],
    );

    render(<AccessChips component={{ typeName: 'Position', fullName: 'Game.Spatial.Position' }} />);

    // Known to the capture, but nothing declares it — "untouched", not Combat.Position's writer.
    expect(screen.getByTestId('access-chips-empty').getAttribute('data-relation')).toBe('untouched');
    expect(screen.queryByText('Movement')).toBeNull();
  });

  it('says the capture never saw a component rather than implying nothing touches it', () => {
    hoisted.topology = topologyWith([MOVEMENT], ['Typhon.Samples.Swg.Shard.Transform']);

    render(<AccessChips component={{ typeName: 'Inventory', fullName: 'Game.Inventory' }} />);

    const empty = screen.getByTestId('access-chips-empty');
    expect(empty.getAttribute('data-relation')).toBe('absent');
    expect(empty.textContent).toContain('has no record of');
  });

  it('distinguishes "no system declares it" from "not in this capture"', () => {
    hoisted.topology = topologyWith([MOVEMENT], ['Typhon.Samples.Swg.Shard.Transform', 'Game.Unloved']);

    render(<AccessChips component={{ typeName: 'Unloved', fullName: 'Game.Unloved' }} />);

    const empty = screen.getByTestId('access-chips-empty');
    expect(empty.getAttribute('data-relation')).toBe('untouched');
    expect(empty.textContent).toContain('No system declared access');
  });

  it('renders nothing while the topology is still loading', () => {
    // Rather than flashing "not in this capture" and correcting itself a moment later.
    hoisted.topology = null;
    const { container } = render(<AccessChips component={{ typeName: 'X', fullName: 'Game.X' }} />);
    expect(container.firstChild).toBeNull();
  });
});
