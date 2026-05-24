// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import InspectorTargetSwitcher, { type SwitcherItem } from '@/panels/schemaCommon/InspectorTargetSwitcher';

// PC-9 part 1 — the header target-switcher (A). cmdk + Radix Popover need a few DOM APIs jsdom lacks.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
beforeEach(() => {
  (globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;
  Element.prototype.scrollIntoView = () => {};
  // Radix Popover trigger uses pointer-capture APIs absent in jsdom.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
});
afterEach(cleanup);

const items: SwitcherItem[] = [
  { id: '800', label: '#800', meta: '1,000 ent', keywords: 'Position Velocity' },
  { id: '806', label: '#806', meta: '2,000 ent', keywords: 'Sprite' },
];

describe('InspectorTargetSwitcher', () => {
  it('renders the trigger with the current label; no chip when not auto', () => {
    render(
      <InspectorTargetSwitcher
        label="Archetype"
        currentLabel="#806"
        auto={false}
        items={items}
        onPick={() => {}}
        testId="archetype"
        noun="archetype"
      />,
    );
    expect(screen.getByTestId('archetype-switcher').textContent).toContain('#806');
    expect(screen.queryByTestId('archetype-auto-chip')).toBeNull();
  });

  it('shows the (auto) chip with its heuristic tooltip when auto', () => {
    render(
      <InspectorTargetSwitcher
        label="Archetype"
        currentLabel="#806"
        auto
        autoTitle="Auto-selected the archetype with the most entities — pick another above."
        items={items}
        onPick={() => {}}
        testId="archetype"
        noun="archetype"
      />,
    );
    const chip = screen.getByTestId('archetype-auto-chip');
    expect(chip.getAttribute('title')).toMatch(/most entities/i);
  });

  it('opens the searchable list and picks an item by id', () => {
    const onPick = vi.fn();
    render(
      <InspectorTargetSwitcher
        label="Archetype"
        currentLabel="#806"
        auto
        items={items}
        onPick={onPick}
        testId="archetype"
        noun="archetype"
      />,
    );
    fireEvent.click(screen.getByTestId('archetype-switcher'));
    expect(screen.getByTestId('archetype-switcher-input')).toBeTruthy();
    const rows = screen.getAllByTestId('archetype-switcher-item');
    expect(rows.map((r) => r.getAttribute('data-id'))).toEqual(['800', '806']);
    fireEvent.click(rows[0]);
    expect(onPick).toHaveBeenCalledWith('800');
  });

  it('narrows the list as you type (search)', () => {
    render(
      <InspectorTargetSwitcher
        label="Archetype"
        currentLabel="#806"
        auto={false}
        items={items}
        onPick={() => {}}
        testId="archetype"
        noun="archetype"
      />,
    );
    fireEvent.click(screen.getByTestId('archetype-switcher'));
    expect(screen.getAllByTestId('archetype-switcher-item')).toHaveLength(2);
    // "806" substring-matches only the second item; cmdk filters via our humpFilter (keyword[0] = label).
    fireEvent.change(screen.getByTestId('archetype-switcher-input'), { target: { value: '806' } });
    const rows = screen.getAllByTestId('archetype-switcher-item');
    expect(rows.map((r) => r.getAttribute('data-id'))).toEqual(['806']);
  });
});
