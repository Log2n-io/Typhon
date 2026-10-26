// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import AttachTab from '@/shell/dialogs/tabs/AttachTab';
import { useAttachEndpointsStore } from '@/stores/useAttachEndpointsStore';

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

// W3 — the saved endpoints and the non-loopback badge. The badge is the half that is easy to get subtly wrong: it is
// about where the BYTES go, and the ordinary deployed case (an SSM forward to 127.0.0.1) must NOT be badged, or the
// warning becomes noise that gets ignored on the day it matters.

describe('AttachTab remote endpoints', () => {
  beforeEach(() => useAttachEndpointsStore.setState({ entries: [] }));
  afterEach(cleanup);

  it('says nothing about the default, which is loopback', () => {
    render(<AttachTab onAttach={vi.fn()} />);
    expect(screen.queryByTestId('attach-remote-warning')).toBeNull();
  });

  it('warns once the endpoint leaves this machine', () => {
    render(<AttachTab onAttach={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText('localhost:9100'), {
      target: { value: 'tatooine.demo.typhondb.io:9100' },
    });

    const warning = screen.getByTestId('attach-remote-warning');
    expect(warning.textContent).toContain('Not loopback');
    // And it names the forward, because that is the answer rather than "do not do this".
    expect(warning.textContent).toContain('attach-remote.ps1');
  });

  it('does not warn about a forwarded port, which is what a deployed attach actually looks like', () => {
    render(<AttachTab onAttach={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText('localhost:9100'), { target: { value: '127.0.0.1:9100' } });

    expect(screen.queryByTestId('attach-remote-warning')).toBeNull();
  });

  it('says nothing while the endpoint is half-typed', () => {
    render(<AttachTab onAttach={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText('localhost:9100'), { target: { value: 'bad endpoint' } });

    // The shape is invalid, so there is no host to judge: the invalid-endpoint pill owns this state.
    expect(screen.queryByTestId('attach-remote-warning')).toBeNull();
  });

  it('offers a saved endpoint and fills the field from it', () => {
    useAttachEndpointsStore.getState().record('10.0.0.5:9100', 'tatooine demo');
    const onAttach = vi.fn();
    render(<AttachTab onAttach={onAttach} />);

    fireEvent.click(screen.getByTestId('attach-saved-10.0.0.5:9100'));
    fireEvent.click(screen.getByRole('button', { name: 'Attach' }));

    expect(onAttach).toHaveBeenCalledWith('10.0.0.5:9100', false);
  });

  it('marks a saved endpoint that is not loopback', () => {
    useAttachEndpointsStore.getState().record('localhost:9100');
    useAttachEndpointsStore.getState().record('10.0.0.5:9100');
    render(<AttachTab onAttach={vi.fn()} />);

    expect(screen.getByTestId('attach-saved-10.0.0.5:9100').textContent).toContain('remote');
    expect(screen.getByTestId('attach-saved-localhost:9100').textContent).not.toContain('remote');
  });

  it('forgets an endpoint on request', () => {
    useAttachEndpointsStore.getState().record('10.0.0.5:9100');
    render(<AttachTab onAttach={vi.fn()} />);

    fireEvent.click(screen.getByLabelText('Forget 10.0.0.5:9100'));

    expect(useAttachEndpointsStore.getState().entries).toHaveLength(0);
    expect(screen.queryByTestId('attach-saved')).toBeNull();
  });
});
