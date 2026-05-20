// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import {
  TimeAreaContextMenu,
  type ChunkMenuProps,
  type SpanMenuProps,
} from '@/panels/profiler/sections/TimeAreaContextMenu';

/**
 * Component tests for the Profiler time-area context menu — both shapes: the `span` variant (call-tree
 * merged scope, zoom, open emission site, copy name / id) and the `chunk` variant (call-tree system
 * scope, zoom, show / open system source, copy system name), plus their disabled states.
 */

const writeText = vi.fn();

beforeEach(() => {
  writeText.mockClear();
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
});
afterEach(cleanup);

const btn = (name: RegExp) => screen.getByRole('button', { name });

function renderSpan(over: Partial<SpanMenuProps> = {}) {
  const props: SpanMenuProps = {
    kind: 'span',
    x: 0, y: 0,
    spanName: 'BTree.Insert',
    spanId: '4242',
    callTreeAvailable: true,
    sourceAvailable: true,
    onClose: vi.fn(),
    onViewInCallTree: vi.fn(),
    onZoom: vi.fn(),
    onOpenSource: vi.fn(),
    ...over,
  };
  render(<TimeAreaContextMenu {...props} />);
  return props;
}

function renderChunk(over: Partial<ChunkMenuProps> = {}) {
  const props: ChunkMenuProps = {
    kind: 'chunk',
    x: 0, y: 0,
    systemName: 'MovementSystem',
    callTreeAvailable: true,
    sourceAvailable: true,
    onClose: vi.fn(),
    onViewInCallTree: vi.fn(),
    onZoom: vi.fn(),
    onShowSourceInline: vi.fn(),
    onOpenSource: vi.fn(),
    ...over,
  };
  render(<TimeAreaContextMenu {...props} />);
  return props;
}

describe('TimeAreaContextMenu — span', () => {
  it('renders the span-name header and all five items', () => {
    renderSpan();
    expect(screen.getByText('BTree.Insert')).toBeTruthy();
    expect(btn(/view in call tree \(merged\)/i)).toBeTruthy();
    expect(btn(/zoom to span/i)).toBeTruthy();
    expect(btn(/open emission site in editor/i)).toBeTruthy();
    expect(btn(/copy span name/i)).toBeTruthy();
    expect(btn(/copy span id/i)).toBeTruthy();
  });

  it('fires the call-tree and zoom actions', () => {
    const props = renderSpan();
    fireEvent.click(btn(/view in call tree \(merged\)/i));
    fireEvent.click(btn(/zoom to span/i));
    expect(props.onViewInCallTree).toHaveBeenCalledOnce();
    expect(props.onZoom).toHaveBeenCalledOnce();
  });

  it('copies the span name and id', () => {
    renderSpan();
    fireEvent.click(btn(/copy span name/i));
    expect(writeText).toHaveBeenLastCalledWith('BTree.Insert');
    fireEvent.click(btn(/copy span id/i));
    expect(writeText).toHaveBeenLastCalledWith('4242');
  });

  it('disables the call-tree item — with a reason — for non-trace sessions', () => {
    const props = renderSpan({ callTreeAvailable: false });
    expect((btn(/view in call tree \(merged\)/i) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(btn(/view in call tree \(merged\)/i));
    expect(props.onViewInCallTree).not.toHaveBeenCalled();
    expect(screen.getByText(/trace sessions only/i)).toBeTruthy();
  });

  it('disables copy-span-id when the span carries no id', () => {
    renderSpan({ spanId: undefined });
    expect((btn(/copy span id/i) as HTMLButtonElement).disabled).toBe(true);
  });

  it('closes on Escape', () => {
    const props = renderSpan();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  // Regression: the canvas calls preventDefault() on pointerdown, which suppresses the compatibility
  // mousedown — so the outside-click listener must be on pointerdown, not mousedown.
  it('closes on an outside pointer-down', () => {
    const props = renderSpan();
    fireEvent.pointerDown(document.body);
    expect(props.onClose).toHaveBeenCalledOnce();
  });
});

describe('TimeAreaContextMenu — chunk', () => {
  it('renders the system-name header and all five items', () => {
    renderChunk();
    expect(screen.getByText('MovementSystem')).toBeTruthy();
    expect(btn(/view in call tree \(system\)/i)).toBeTruthy();
    expect(btn(/zoom to chunk/i)).toBeTruthy();
    expect(btn(/show system source inline/i)).toBeTruthy();
    expect(btn(/open system source in editor/i)).toBeTruthy();
    expect(btn(/copy system name/i)).toBeTruthy();
  });

  it('fires the call-tree, zoom and show-inline actions', () => {
    const props = renderChunk();
    fireEvent.click(btn(/view in call tree \(system\)/i));
    fireEvent.click(btn(/zoom to chunk/i));
    fireEvent.click(btn(/show system source inline/i));
    expect(props.onViewInCallTree).toHaveBeenCalledOnce();
    expect(props.onZoom).toHaveBeenCalledOnce();
    expect(props.onShowSourceInline).toHaveBeenCalledOnce();
  });

  it('copies the system name', () => {
    renderChunk();
    fireEvent.click(btn(/copy system name/i));
    expect(writeText).toHaveBeenLastCalledWith('MovementSystem');
  });

  it('disables the source items when the system has no resolved source', () => {
    const props = renderChunk({ sourceAvailable: false });
    expect((btn(/show system source inline/i) as HTMLButtonElement).disabled).toBe(true);
    expect((btn(/open system source in editor/i) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(btn(/show system source inline/i));
    fireEvent.click(btn(/open system source in editor/i));
    expect(props.onShowSourceInline).not.toHaveBeenCalled();
    expect(props.onOpenSource).not.toHaveBeenCalled();
  });

  it('disables the call-tree item — with a reason — for non-trace sessions', () => {
    const props = renderChunk({ callTreeAvailable: false });
    expect((btn(/view in call tree \(system\)/i) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(btn(/view in call tree \(system\)/i));
    expect(props.onViewInCallTree).not.toHaveBeenCalled();
    expect(screen.getByText(/trace sessions only/i)).toBeTruthy();
  });
});
