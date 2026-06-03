// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import IncompatibleBanner from '@/shell/banners/IncompatibleBanner';
import MigrationRequiredBanner from '@/shell/banners/MigrationRequiredBanner';
import { registerOpenConnect } from '@/shell/commands/baseCommands';
import { useOptionsUiStore } from '@/stores/useOptionsUiStore';

afterEach(() => {
  cleanup();
  registerOpenConnect(null);
  useOptionsUiStore.getState().clearRequested();
});

// AC1.12 — blocked states show a diagnostic + a real forward action (not just "close", no dead stub).

describe('blocked-state banners', () => {
  it('Incompatible banner offers a forward action that opens Connect', () => {
    const open = vi.fn();
    registerOpenConnect(open);
    render(<IncompatibleBanner />);
    expect(screen.getByText(/schema incompatible/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /open another file/i }));
    expect(open).toHaveBeenCalledWith('open');
  });

  it('Migration banner forward action is live (no disabled stub)', () => {
    const open = vi.fn();
    registerOpenConnect(open);
    render(<MigrationRequiredBanner />);
    expect(screen.getByText(/schema migration required/i)).toBeTruthy(); // diagnostic present, not a bare action
    const btn = screen.getByRole('button', { name: /open another file/i }) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    fireEvent.click(btn);
    expect(open).toHaveBeenCalledWith('open');
  });

  // ADR-055 Phase 2 — both blocked-state banners offer a "Manage schema directories…" action that deep-links
  // the Options panel to its Schema category (so the user can register a compatible schema build).
  it.each([
    ['Incompatible', IncompatibleBanner],
    ['Migration', MigrationRequiredBanner],
  ] as const)('%s banner deep-links to the schema options category', (_name, Banner) => {
    render(<Banner />);
    fireEvent.click(screen.getByRole('button', { name: /manage schema directories/i }));
    expect(useOptionsUiStore.getState().requestedCategory).toBe('schema');
  });
});
