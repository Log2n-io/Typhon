// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';

// Mutable store state the mock projects through the component's selectors. `vi.hoisted` makes it
// available inside the (hoisted) vi.mock factory below.
const state = vi.hoisted(() => ({
  directories: [] as string[],
  setSchema: vi.fn<(schema: { directories: string[] }) => Promise<void>>(),
}));

vi.mock('@/stores/useOptionsStore', () => ({
  useOptionsStore: (selector: (s: unknown) => unknown) =>
    selector({ options: { schema: { directories: state.directories } }, setSchema: state.setSchema }),
}));

import { SchemaForm } from '@/panels/options/SchemaForm';

beforeEach(() => {
  state.directories = [];
  state.setSchema.mockReset();
  state.setSchema.mockResolvedValue(undefined);
});

afterEach(() => cleanup());

describe('SchemaForm', () => {
  it('shows the empty state when no directories are registered', () => {
    render(<SchemaForm />);
    expect(screen.getByText(/no directories registered/i)).toBeTruthy();
  });

  it('lists registered directories and removes one via setSchema', () => {
    state.directories = ['C:\\a\\bin', 'C:\\b\\bin'];
    render(<SchemaForm />);
    expect(screen.getByText('C:\\a\\bin')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /remove C:\\a\\bin/i }));
    expect(state.setSchema).toHaveBeenCalledWith({ directories: ['C:\\b\\bin'] });
  });

  it('adds an absolute path via the paste field (appended to the existing list)', () => {
    state.directories = ['C:\\existing'];
    render(<SchemaForm />);
    const input = screen.getByPlaceholderText(/bin\\Debug/i);
    fireEvent.change(input, { target: { value: 'C:\\Dev\\schema\\bin' } });
    fireEvent.click(screen.getByRole('button', { name: /^add$/i }));
    expect(state.setSchema).toHaveBeenCalledWith({ directories: ['C:\\existing', 'C:\\Dev\\schema\\bin'] });
  });

  it('rejects a relative path with an inline error and never calls setSchema', () => {
    render(<SchemaForm />);
    const input = screen.getByPlaceholderText(/bin\\Debug/i);
    fireEvent.change(input, { target: { value: 'relative/dir' } });
    fireEvent.click(screen.getByRole('button', { name: /^add$/i }));
    expect(screen.getByText(/absolute directory path/i)).toBeTruthy();
    expect(state.setSchema).not.toHaveBeenCalled();
  });

  it('rejects a duplicate (case-insensitive) without calling setSchema', async () => {
    state.directories = ['C:\\Dev\\Schema'];
    render(<SchemaForm />);
    const input = screen.getByPlaceholderText(/bin\\Debug/i);
    fireEvent.change(input, { target: { value: 'c:\\dev\\schema' } });
    fireEvent.click(screen.getByRole('button', { name: /^add$/i }));
    await waitFor(() => expect(screen.getByText(/already registered/i)).toBeTruthy());
    expect(state.setSchema).not.toHaveBeenCalled();
  });
});
