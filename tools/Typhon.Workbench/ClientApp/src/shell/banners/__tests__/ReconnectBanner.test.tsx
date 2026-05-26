// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

const spies = vi.hoisted(() => ({
  postAttach: vi.fn(),
  captureAndAnalyse: vi.fn().mockResolvedValue({ replayPath: 'x', bytesWritten: 1, newSessionId: 's' }),
}));
vi.mock('@/api/generated/sessions/sessions', () => ({
  postApiSessionsAttach: spies.postAttach,
}));
vi.mock('@/shell/commands/captureAndAnalyse', () => ({
  captureAndAnalyse: spies.captureAndAnalyse,
}));

import ReconnectBanner from '../ReconnectBanner';

function setAttachDisconnected(reason: string | null) {
  useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
  useProfilerSessionStore.setState({ connectionStatus: 'disconnected', disconnectReason: reason });
}

beforeEach(() => {
  spies.postAttach.mockReset();
  spies.captureAndAnalyse.mockReset();
  spies.captureAndAnalyse.mockResolvedValue({ replayPath: 'x', bytesWritten: 1, newSessionId: 's' });
});
afterEach(() => {
  cleanup();
  useSessionStore.setState({ kind: 'none', sessionId: null, filePath: null });
  useProfilerSessionStore.setState({ connectionStatus: null, disconnectReason: null });
});

describe('UC-OBS-07a — ReconnectBanner gates on attach + disconnected (PC-2 / suite D / AC4.8)', () => {
  it('renders nothing when sessionKind is not attach', () => {
    useSessionStore.setState({ kind: 'trace', sessionId: 'x', filePath: 'y' });
    useProfilerSessionStore.setState({ connectionStatus: 'disconnected', disconnectReason: 'init_mismatch' });
    const { container } = render(<ReconnectBanner />);
    expect(container.firstChild).toBeNull();
  });

  it('renders nothing when connection is alive', () => {
    useSessionStore.setState({ kind: 'attach', sessionId: 'x', filePath: 'y' });
    useProfilerSessionStore.setState({ connectionStatus: 'connected', disconnectReason: null });
    const { container } = render(<ReconnectBanner />);
    expect(container.firstChild).toBeNull();
  });

  it('renders the banner on attach + disconnected', () => {
    setAttachDisconnected(null);
    render(<ReconnectBanner />);
    expect(screen.getByTestId('engine-reconnect-banner')).toBeTruthy();
  });
});

describe('UC-OBS-08 — incompatible-schema shutdown distinguished from transient drop (AC4.8)', () => {
  it('uses init_mismatch wording when the reason is init_mismatch', () => {
    setAttachDisconnected('init_mismatch');
    render(<ReconnectBanner />);
    const title = screen.getByTestId('engine-reconnect-banner-title');
    expect(title.textContent).toMatch(/incompatible schema/i);
    expect(screen.getByTestId('engine-reconnect-banner').getAttribute('data-reason')).toBe('init_mismatch');
  });

  it('uses generic dropped wording when reason is null', () => {
    setAttachDisconnected(null);
    render(<ReconnectBanner />);
    const title = screen.getByTestId('engine-reconnect-banner-title');
    expect(title.textContent).toMatch(/connection dropped/i);
    expect(screen.getByTestId('engine-reconnect-banner').getAttribute('data-reason')).toBe('transient');
  });

  it('shows the endpoint when one is known', () => {
    setAttachDisconnected(null);
    render(<ReconnectBanner />);
    expect(screen.getByTestId('engine-reconnect-banner-endpoint').textContent).toBe('localhost:9100');
  });
});

describe('UC-OBS-07 — reconnect banner action wiring (AC4.8, GAP-22)', () => {
  it('Reconnect calls postApiSessionsAttach with the stored endpoint', async () => {
    spies.postAttach.mockResolvedValue({ data: { sessionId: 'new', kind: 'attach', filePath: 'localhost:9100' } });
    setAttachDisconnected(null);
    render(<ReconnectBanner />);
    fireEvent.click(screen.getByTestId('engine-reconnect-banner-reconnect'));
    await waitFor(() => {
      expect(spies.postAttach).toHaveBeenCalledWith({ endpointAddress: 'localhost:9100' });
    });
  });

  it('Capture button invokes captureAndAnalyse(sessionId)', async () => {
    setAttachDisconnected('init_mismatch');
    render(<ReconnectBanner />);
    fireEvent.click(screen.getByTestId('engine-reconnect-banner-capture'));
    await waitFor(() => {
      expect(spies.captureAndAnalyse).toHaveBeenCalledWith('sess-A');
    });
  });

  it('Dismiss button hides the banner for this exact event', () => {
    setAttachDisconnected('init_mismatch');
    const { container } = render(<ReconnectBanner />);
    expect(screen.queryByTestId('engine-reconnect-banner')).toBeTruthy();
    fireEvent.click(screen.getByTestId('engine-reconnect-banner-dismiss'));
    expect(container.firstChild).toBeNull();
  });

  it('Re-shows the banner when a different disconnect reason arrives', () => {
    setAttachDisconnected('init_mismatch');
    const { container, rerender } = render(<ReconnectBanner />);
    fireEvent.click(screen.getByTestId('engine-reconnect-banner-dismiss'));
    expect(container.firstChild).toBeNull();
    // Now a generic transient drop arrives — banner returns.
    useProfilerSessionStore.setState({ connectionStatus: 'disconnected', disconnectReason: null });
    rerender(<ReconnectBanner />);
    expect(screen.queryByTestId('engine-reconnect-banner')).toBeTruthy();
  });
});
