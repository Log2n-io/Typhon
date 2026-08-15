// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import AttachTab from '@/shell/dialogs/tabs/AttachTab';

// On-demand tick capture (#805) — the attach-time mode choice. The default is load-bearing: it is what every
// existing attach workflow gets, so it must remain capture-everything.

describe('AttachTab capture mode', () => {
  afterEach(cleanup);

  it('defaults to capture-everything so existing workflows are unchanged', () => {
    const onAttach = vi.fn();
    render(<AttachTab onAttach={onAttach} />);

    expect((screen.getByTestId('attach-mode-everything') as HTMLInputElement).checked).toBe(true);
    expect((screen.getByTestId('attach-mode-cherry-pick') as HTMLInputElement).checked).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Attach' }));
    expect(onAttach).toHaveBeenCalledWith('localhost:9100', false);
  });

  it('passes cherryPick when the user picks it', () => {
    const onAttach = vi.fn();
    render(<AttachTab onAttach={onAttach} />);

    fireEvent.click(screen.getByTestId('attach-mode-cherry-pick'));
    fireEvent.click(screen.getByRole('button', { name: 'Attach' }));

    expect(onAttach).toHaveBeenCalledWith('localhost:9100', true);
  });

  it('keeps the mode choice independent of endpoint validation', () => {
    const onAttach = vi.fn();
    render(<AttachTab onAttach={onAttach} />);

    fireEvent.click(screen.getByTestId('attach-mode-cherry-pick'));
    fireEvent.change(screen.getByPlaceholderText('localhost:9100'), { target: { value: 'bad endpoint' } });

    expect((screen.getByRole('button', { name: 'Attach' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('attach-mode-cherry-pick') as HTMLInputElement).checked).toBe(true);
  });
});
