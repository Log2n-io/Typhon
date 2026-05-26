// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSessionStore } from '@/stores/useSessionStore';

// Hoisted spies — vi.mock factories below reference them, kept mutable for per-test response shaping.
const spies = vi.hoisted(() => ({
  saveReplay: vi.fn(),
  postTrace: vi.fn(),
  toggleProfiler: vi.fn(),
}));

vi.mock('@/api/generated/profiler/profiler', () => ({
  postApiSessionsSessionIdProfilerSaveReplay: spies.saveReplay,
}));
vi.mock('@/api/generated/sessions/sessions', () => ({
  postApiSessionsTrace: spies.postTrace,
}));
vi.mock('@/shell/commands/profilerCommands', () => ({
  toggleViewProfiler: spies.toggleProfiler,
}));

import { captureAndAnalyse } from '../captureAndAnalyse';

beforeEach(() => {
  spies.saveReplay.mockReset();
  spies.postTrace.mockReset();
  spies.toggleProfiler.mockReset();
});
afterEach(() => {
  useSessionStore.setState({ kind: 'none', sessionId: null, filePath: null });
});

describe('UC-OBS-05 / UC-OBS-06 — Capture & Analyse saves replay + lands in J2 (AC4.7, GAP-22 one gesture)', () => {
  it('chains save-replay → /sessions/trace → setSession → toggleViewProfiler in order', async () => {
    spies.saveReplay.mockResolvedValue({ data: { path: 'C:/tmp/foo.typhon-replay', bytesWritten: 12345 } });
    spies.postTrace.mockResolvedValue({ data: {
      sessionId: 'new-trace-sess',
      kind: 'trace',
      filePath: 'C:/tmp/foo.typhon-replay',
      state: 'Ready',
    } });

    const setSession = vi.spyOn(useSessionStore.getState(), 'setSession');
    const result = await captureAndAnalyse('attach-sess');

    expect(spies.saveReplay).toHaveBeenCalledWith('attach-sess', {});
    expect(spies.postTrace).toHaveBeenCalledWith({ filePath: 'C:/tmp/foo.typhon-replay' });
    expect(setSession).toHaveBeenCalledOnce();
    expect(spies.toggleProfiler).toHaveBeenCalledOnce();
    expect(result.replayPath).toBe('C:/tmp/foo.typhon-replay');
    expect(result.bytesWritten).toBe(12345);
    expect(result.newSessionId).toBe('new-trace-sess');
  });

  it('throws if /save-replay returns 200 but no path field', async () => {
    spies.saveReplay.mockResolvedValue({ data: { path: null, bytesWritten: 0 } });
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow(/no path/);
    expect(spies.postTrace).not.toHaveBeenCalled();
  });

  it('rethrows save-replay errors (toggleProfiler not invoked)', async () => {
    spies.saveReplay.mockRejectedValue(new Error('disk full'));
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow('disk full');
    expect(spies.postTrace).not.toHaveBeenCalled();
    expect(spies.toggleProfiler).not.toHaveBeenCalled();
  });

  it('rethrows /sessions/trace errors after save succeeded', async () => {
    spies.saveReplay.mockResolvedValue({ data: { path: 'C:/tmp/foo.typhon-replay', bytesWritten: 100 } });
    spies.postTrace.mockRejectedValue(new Error('trace open failed'));
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow('trace open failed');
    expect(spies.toggleProfiler).not.toHaveBeenCalled();
  });
});
