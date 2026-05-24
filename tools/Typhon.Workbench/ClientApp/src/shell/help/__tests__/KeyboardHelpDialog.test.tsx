// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import KeyboardHelpDialog from '@/shell/help/KeyboardHelpDialog';

afterEach(cleanup);

describe('KeyboardHelpDialog (Help → Quick Doc → Keyboard navigation)', () => {
  it('documents the chord family + the tab-cycle', () => {
    render(<KeyboardHelpDialog onClose={() => {}} />);
    expect(screen.getByRole('dialog', { name: 'Keyboard navigation' })).toBeTruthy();
    expect(screen.getByText('Component Inspector')).toBeTruthy();
    expect(screen.getByText('Database File Map')).toBeTruthy(); // g m
    expect(screen.getByText('Next tab')).toBeTruthy();          // ]
    expect(screen.getAllByText('g').length).toBeGreaterThan(0); // chord leader
  });

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(<KeyboardHelpDialog onClose={onClose} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on backdrop click but not on content click', () => {
    const onClose = vi.fn();
    render(<KeyboardHelpDialog onClose={onClose} />);
    fireEvent.click(screen.getByText('Keyboard navigation')); // inside the card → stopPropagation
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('dialog')); // the backdrop
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes via the ✕ button', () => {
    const onClose = vi.fn();
    render(<KeyboardHelpDialog onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
