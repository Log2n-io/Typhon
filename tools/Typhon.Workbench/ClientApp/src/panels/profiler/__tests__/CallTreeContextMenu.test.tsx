// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { CallTreeContextMenu, type CallTreeContextMenuProps } from '@/panels/profiler/CallTreeContextMenu';

/**
 * Component tests for the Call Tree row context menu. Covers all seven items — show inline, open in
 * editor, focus tree, expand / collapse subtree, copy method name / full signature — plus the
 * no-source and leaf (no-children) disabled states and Escape dismissal.
 */

const writeText = vi.fn();

beforeEach(() => {
  writeText.mockClear();
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
});
afterEach(cleanup);

/** Renders the menu with all-available defaults; `over` patches individual props per test. */
function renderMenu(over: Partial<CallTreeContextMenuProps> = {}) {
  const props: CallTreeContextMenuProps = {
    x: 0, y: 0,
    methodName: 'BTree.Insert',
    fullSignature: 'Typhon.Engine.BTree.Insert(value class TKey, value class TValue)',
    sourceAvailable: true,
    hasChildren: true,
    onClose: vi.fn(),
    onShowInline: vi.fn(),
    onOpenInEditor: vi.fn(),
    onFocusTree: vi.fn(),
    onExpandSubtree: vi.fn(),
    onCollapseSubtree: vi.fn(),
    ...over,
  };
  render(<CallTreeContextMenu {...props} />);
  return props;
}

const btn = (name: RegExp) => screen.getByRole('button', { name });

describe('CallTreeContextMenu', () => {
  it('renders the method-name header and all seven items', () => {
    renderMenu();
    expect(screen.getByText('BTree.Insert')).toBeTruthy();
    expect(btn(/show inline/i)).toBeTruthy();
    expect(btn(/open in editor/i)).toBeTruthy();
    expect(btn(/focus tree on this frame/i)).toBeTruthy();
    expect(btn(/expand subtree/i)).toBeTruthy();
    expect(btn(/collapse subtree/i)).toBeTruthy();
    expect(btn(/copy method name/i)).toBeTruthy();
    expect(btn(/copy full signature/i)).toBeTruthy();
  });

  it('fires the navigation actions when their items are clicked', () => {
    const props = renderMenu();
    fireEvent.click(btn(/show inline/i));
    fireEvent.click(btn(/open in editor/i));
    fireEvent.click(btn(/focus tree on this frame/i));
    fireEvent.click(btn(/expand subtree/i));
    fireEvent.click(btn(/collapse subtree/i));
    expect(props.onShowInline).toHaveBeenCalledOnce();
    expect(props.onOpenInEditor).toHaveBeenCalledOnce();
    expect(props.onFocusTree).toHaveBeenCalledOnce();
    expect(props.onExpandSubtree).toHaveBeenCalledOnce();
    expect(props.onCollapseSubtree).toHaveBeenCalledOnce();
  });

  it('copies the friendly name and the full signature to the clipboard', () => {
    const props = renderMenu();
    fireEvent.click(btn(/copy method name/i));
    expect(writeText).toHaveBeenLastCalledWith('BTree.Insert');
    fireEvent.click(btn(/copy full signature/i));
    expect(writeText).toHaveBeenLastCalledWith('Typhon.Engine.BTree.Insert(value class TKey, value class TValue)');
    expect(props.onClose).toHaveBeenCalledTimes(2);
  });

  it('disables the source items — and surfaces a reason — for a frame with no source', () => {
    const props = renderMenu({ sourceAvailable: false });
    expect((btn(/show inline/i) as HTMLButtonElement).disabled).toBe(true);
    expect((btn(/open in editor/i) as HTMLButtonElement).disabled).toBe(true);
    // Focus-tree, expand/collapse and copy need no source — they stay enabled.
    expect((btn(/focus tree on this frame/i) as HTMLButtonElement).disabled).toBe(false);
    expect((btn(/copy method name/i) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(btn(/show inline/i));
    fireEvent.click(btn(/open in editor/i));
    expect(props.onShowInline).not.toHaveBeenCalled();
    expect(props.onOpenInEditor).not.toHaveBeenCalled();
    expect(screen.getByText(/BCL \/ native frame/i)).toBeTruthy();
  });

  it('disables expand / collapse subtree on a leaf frame', () => {
    const props = renderMenu({ hasChildren: false });
    expect((btn(/expand subtree/i) as HTMLButtonElement).disabled).toBe(true);
    expect((btn(/collapse subtree/i) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(btn(/expand subtree/i));
    fireEvent.click(btn(/collapse subtree/i));
    expect(props.onExpandSubtree).not.toHaveBeenCalled();
    expect(props.onCollapseSubtree).not.toHaveBeenCalled();
  });

  it('closes on Escape', () => {
    const props = renderMenu();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(props.onClose).toHaveBeenCalledOnce();
  });
});
