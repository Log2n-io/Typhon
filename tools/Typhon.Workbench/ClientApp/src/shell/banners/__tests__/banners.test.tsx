// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import IncompatibleBanner from '@/shell/banners/IncompatibleBanner';
import MigrationRequiredBanner from '@/shell/banners/MigrationRequiredBanner';
import { registerOpenConnect } from '@/shell/commands/baseCommands';
import { useOptionsUiStore } from '@/stores/useOptionsUiStore';
import { useSessionStore } from '@/stores/useSessionStore';

afterEach(() => {
  cleanup();
  registerOpenConnect(null);
  useOptionsUiStore.getState().clearRequested();
  useSessionStore.setState({ schemaDiagnostics: [] });
});

// AC1.12 — blocked states show a diagnostic + a real forward action (not just "close", no dead stub).

describe('blocked-state banners', () => {
  it('Incompatible banner offers a forward action that opens Connect', () => {
    const open = vi.fn();
    registerOpenConnect(open);
    render(<IncompatibleBanner />);
    expect(screen.getByText(/schema incompatible/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /open a different database/i }));
    expect(open).toHaveBeenCalledWith('known');   // the Known tab is the default landing for 'open a database'
  });

  it('Migration banner forward action is live (no disabled stub)', () => {
    const open = vi.fn();
    registerOpenConnect(open);
    render(<MigrationRequiredBanner />);
    expect(screen.getByText(/schema migration required/i)).toBeTruthy(); // diagnostic present, not a bare action
    const btn = screen.getByRole('button', { name: /open a different database/i }) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    fireEvent.click(btn);
    expect(open).toHaveBeenCalledWith('known');   // the Known tab is the default landing for 'open a database'
  });

  // A DLL that could not be FOUND is not an incompatibility. Saying "incompatible" and advising "reopen with binaries
  // that match" sends the user after a problem they do not have — their binaries are fine, just not on the search path.
  it('names a not-found assembly as not-found, never as incompatible', () => {
    useSessionStore.setState({
      schemaDiagnostics: [{ componentName: 'ShardLab', kind: 'missing_assembly', detail: 'not found' }],
    });
    render(<IncompatibleBanner />);

    expect(screen.getByText(/schema assembly not found/i)).toBeTruthy();
    expect(screen.queryByText(/schema incompatible/i)).toBeNull();
    expect(screen.getAllByText(/ShardLab/).length).toBeGreaterThan(0);
    // The fix, not the exit, is the leading action.
    expect(screen.getByRole('button', { name: /locate schema assembly/i })).toBeTruthy();
  });

  it('still reports a genuine mismatch as incompatible', () => {
    useSessionStore.setState({
      schemaDiagnostics: [{ componentName: 'Wallet', kind: 'breaking_change', detail: 'field removed' }],
    });
    render(<IncompatibleBanner />);

    expect(screen.getByText(/schema incompatible/i)).toBeTruthy();
    expect(screen.getByRole('button', { name: /manage schema directories/i })).toBeTruthy();
  });

  // Mixed set: something missing AND something genuinely incompatible. The stronger message is the honest one.
  it('reports a mixed set as incompatible, not merely not-found', () => {
    useSessionStore.setState({
      schemaDiagnostics: [
        { componentName: 'ShardLab', kind: 'missing_assembly', detail: 'not found' },
        { componentName: 'Wallet', kind: 'breaking_change', detail: 'field removed' },
      ],
    });
    render(<IncompatibleBanner />);

    expect(screen.getByText(/schema incompatible/i)).toBeTruthy();
    expect(screen.queryByText(/schema assembly not found/i)).toBeNull();
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
