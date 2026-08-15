// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import CaptureControl from '@/panels/profiler/components/CaptureControl';
import { useProfilerSessionStore, type CaptureState } from '@/stores/useProfilerSessionStore';

// On-demand tick capture (#805) — the Record / Stop control. These cover the three things a user can actually get
// wrong or be misled by: a control that appears when it cannot work, a budget that is silently mangled, and a
// timeline whose tick numbers are quietly untrustworthy.

const SESSION = '11111111-2222-3333-4444-555555555555';

function state(overrides: Partial<CaptureState> = {}): CaptureState {
  return {
    state: 'Idle',
    remaining: 0,
    recordedTicks: 0,
    mode: 'CherryPick',
    tickNumbersAbsolute: true,
    tickNumberingSuspect: false,
    bytesReceived: 1_000,
    bytesRetained: 100,
    ...overrides,
  };
}

function setCapture(s: CaptureState | null) {
  useProfilerSessionStore.setState({ captureState: s });
}

describe('CaptureControl', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    setCapture(null);
  });

  it('renders nothing in capture-everything mode', () => {
    // There is no window to arm, and a disabled Record button would imply the mode can be changed after attaching.
    setCapture(state({ mode: 'Everything', state: 'Everything' }));
    const { container } = render(<CaptureControl sessionId={SESSION} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders nothing before the first capture-state frame arrives', () => {
    setCapture(null);
    const { container } = render(<CaptureControl sessionId={SESSION} />);
    expect(container.innerHTML).toBe('');
  });

  it('arms the requested tick budget', async () => {
    setCapture(state());
    render(<CaptureControl sessionId={SESSION} />);

    fireEvent.change(screen.getByTestId('capture-tick-count'), { target: { value: '250' } });
    fireEvent.click(screen.getByTestId('capture-record'));

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const [url, init] = (fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe(`/api/sessions/${SESSION}/profiler/capture`);
    expect(JSON.parse(init.body)).toEqual({ tickCount: 250 });
  });

  it('refuses a non-positive or non-integer budget instead of sending it', () => {
    setCapture(state());
    render(<CaptureControl sessionId={SESSION} />);

    for (const bad of ['0', '-5', '2.5', 'abc', '']) {
      fireEvent.change(screen.getByTestId('capture-tick-count'), { target: { value: bad } });
      expect((screen.getByTestId('capture-record') as HTMLButtonElement).disabled).toBe(true);
    }
    expect(fetch).not.toHaveBeenCalled();
  });

  it('shows Stop while recording, and stopping sends a zero budget', async () => {
    setCapture(state({ state: 'Recording', remaining: 37, recordedTicks: 63 }));
    render(<CaptureControl sessionId={SESSION} />);

    expect(screen.queryByTestId('capture-record')).toBeNull();
    expect(screen.getByTestId('capture-progress').textContent).toContain('37 ticks left');

    fireEvent.click(screen.getByTestId('capture-stop'));
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const [, init] = (fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(JSON.parse(init.body)).toEqual({ tickCount: 0 });
  });

  it('warns when tick numbering can no longer be trusted', () => {
    // A dropped TickStart renumbers everything after it with nothing in the data looking unusual, so the UI has to
    // say so — a plausible-looking wrong timeline is worse than an admitted one.
    setCapture(state({ tickNumberingSuspect: true, state: 'Recording', remaining: 5 }));
    render(<CaptureControl sessionId={SESSION} />);
    expect(screen.getByTestId('capture-numbering-warning')).toBeTruthy();
  });

  it('says so when ticks are numbered relative to attach', () => {
    // No gauges ⇒ no absolute tick number exists anywhere on the live wire.
    setCapture(state({ tickNumbersAbsolute: false }));
    render(<CaptureControl sessionId={SESSION} />);
    expect(screen.getByTestId('capture-idle').textContent).toContain('relative to attach');
  });
});
