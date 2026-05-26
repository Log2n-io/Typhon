// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryDetailHeader } from '../QueryDetailHeader';
import { makeDef } from './fixtures';
import { categoricalColor } from '@/libs/color/categorical';
import { rgbCss } from '@/libs/color/contrast';

// Spy on the shared reveal commands the header's hand-off buttons call (4D-1, AC3.14).
const spies = vi.hoisted(() => ({
  revealSystemInDag: vi.fn(),
  openComponentInSchema: vi.fn(),
  revealArchetypeInInspector: vi.fn(),
}));
vi.mock('@/shell/commands/openDbMap', () => spies);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('QueryDetailHeader', () => {
  const def = makeDef({
    localId: 1,
    target: 1,
    totalWallNs: 140_000,
    owners: [0],
    method: 'FindByPosition',
    file: 'src/Game/Queries.cs',
    line: 88,
  });

  it('AC5: renders identity, predicate, owning systems, source, and the aggregate stat strip', () => {
    render(<QueryDetailHeader definition={def} archetypeName="Position" ownerNames={['Movement']} targetId={1} targetIsComponent />);

    expect(screen.getByText('View #1')).toBeTruthy();
    expect(screen.getByTestId('query-detail-predicate').textContent).toContain('X >= ?');
    expect(screen.getByTestId('query-detail-systems').textContent).toContain('Movement');

    const src = screen.getByTestId('query-detail-open-in-editor');
    expect(src.textContent).toContain('FindByPosition');
    expect(src.getAttribute('title')).toBe('src/Game/Queries.cs:88');

    expect(screen.getByText('total')).toBeTruthy();
    expect(screen.getByText('selectivity')).toBeTruthy();
  });

  it('AC3 (go-to-source AC3.9): shows an em-dash source when the definition has no resolved call site', () => {
    const noSrc = makeDef({ localId: 2, target: 1, totalWallNs: 1000, method: '', file: '', line: 0 });
    render(<QueryDetailHeader definition={noSrc} archetypeName="Position" ownerNames={[]} targetId={1} targetIsComponent />);
    expect(screen.queryByTestId('query-detail-open-in-editor')).toBeNull();
  });

  it('AC3.10: an owner renders its shared categorical identity dot', () => {
    render(<QueryDetailHeader definition={def} archetypeName="Position" ownerNames={['Movement']} targetId={1} targetIsComponent />);
    const ownerBtn = screen.getByTestId('query-detail-owner');
    const dot = ownerBtn.querySelector('span[aria-hidden]') as HTMLElement | null;
    expect(dot).toBeTruthy();
    expect(dot!.style.backgroundColor).toBe(rgbCss(categoricalColor('Movement')));
  });

  it('AC-D1.1: clicking an owner reveals it in the System DAG', () => {
    render(<QueryDetailHeader definition={def} archetypeName="Position" ownerNames={['Movement']} targetId={1} targetIsComponent />);
    fireEvent.click(screen.getByTestId('query-detail-owner'));
    expect(spies.revealSystemInDag).toHaveBeenCalledWith('Movement');
  });

  it('AC-D1.2: a component target opens the Component Inspector', () => {
    render(<QueryDetailHeader definition={def} archetypeName="Position" ownerNames={['Movement']} targetId={1} targetIsComponent />);
    fireEvent.click(screen.getByTestId('query-detail-target'));
    expect(spies.openComponentInSchema).toHaveBeenCalledWith('Position');
    expect(spies.revealArchetypeInInspector).not.toHaveBeenCalled();
  });

  it('AC-D1.2: an archetype target opens the Archetype Inspector', () => {
    render(<QueryDetailHeader definition={def} archetypeName="Ant" ownerNames={['Movement']} targetId={2002} targetIsComponent={false} />);
    fireEvent.click(screen.getByTestId('query-detail-target'));
    expect(spies.revealArchetypeInInspector).toHaveBeenCalledWith('2002');
    expect(spies.openComponentInSchema).not.toHaveBeenCalled();
  });
});
