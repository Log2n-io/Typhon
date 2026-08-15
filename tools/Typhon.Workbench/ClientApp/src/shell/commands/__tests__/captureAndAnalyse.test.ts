// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSessionStore } from '@/stores/useSessionStore';

// Hoisted spies — vi.mock factories below reference them, kept mutable for per-test response shaping.
const spies = vi.hoisted(() => ({
  saveReplay: vi.fn(),
  getSession: vi.fn(),
  toggleProfiler: vi.fn(),
}));

vi.mock('@/api/generated/profiler/profiler', () => ({
  postApiSessionsSessionIdProfilerSaveReplay: spies.saveReplay,
}));
vi.mock('@/api/generated/sessions/sessions', () => ({
  getApiSessionsId: spies.getSession,
}));
vi.mock('@/shell/commands/profilerCommands', () => ({
  toggleViewProfiler: spies.toggleProfiler,
}));

import { captureAndAnalyse } from '../captureAndAnalyse';

const ATTACHED_SESSION = {
  sessionId: 'attach-sess',
  kind: 'Attach',
  state: 'Attached',
  filePath: 'localhost:9000',
  capabilities: ['profiler'],
  activeProfileId: 'profile-1',
};

beforeEach(() => {
  spies.saveReplay.mockReset();
  spies.getSession.mockReset();
  spies.toggleProfiler.mockReset();
  spies.getSession.mockResolvedValue({ data: ATTACHED_SESSION });
});
afterEach(() => {
  useSessionStore.setState({ kind: 'none', sessionId: null, filePath: null });
});

/**
 * #621 changed the shape of this flow. `save-replay` now writes the replay AND attaches it back to the same live
 * session, returning the new `profileId`; there is no second POST creating a trace session, because the standalone
 * trace session no longer exists. The session id is therefore *preserved* rather than swapped — which is what keeps the
 * session token, and every panel keyed on it, alive across the gesture.
 */
describe('UC-OBS-05 / UC-OBS-06 — Capture & Analyse saves replay + lands in J2 (AC4.7, GAP-22 one gesture)', () => {
  it('chains save-replay → re-read session → setSession → toggleViewProfiler in order', async () => {
    spies.saveReplay.mockResolvedValue({
      data: { path: 'C:/tmp/foo.typhon-replay', bytesWritten: 12345, profileId: 'profile-1' },
    });

    const setSession = vi.spyOn(useSessionStore.getState(), 'setSession');
    const result = await captureAndAnalyse('attach-sess');

    expect(spies.saveReplay).toHaveBeenCalledWith('attach-sess', {});
    // The session is re-read because attaching changed its capabilities server-side; pushing the save response alone
    // would leave the store describing a session that cannot profile — the #617 seam.
    expect(spies.getSession).toHaveBeenCalledWith('attach-sess');
    expect(setSession).toHaveBeenCalledWith(ATTACHED_SESSION);
    expect(spies.toggleProfiler).toHaveBeenCalledOnce();

    expect(result.replayPath).toBe('C:/tmp/foo.typhon-replay');
    expect(result.bytesWritten).toBe(12345);
    // Same session — the replay attached to it rather than replacing it.
    expect(result.newSessionId).toBe('attach-sess');
  });

  it('throws if /save-replay returns 200 but no path field', async () => {
    spies.saveReplay.mockResolvedValue({ data: { path: null, bytesWritten: 0 } });
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow(/no path/);
    expect(spies.getSession).not.toHaveBeenCalled();
  });

  it('rethrows save-replay errors (toggleProfiler not invoked)', async () => {
    spies.saveReplay.mockRejectedValue(new Error('disk full'));
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow('disk full');
    expect(spies.getSession).not.toHaveBeenCalled();
    expect(spies.toggleProfiler).not.toHaveBeenCalled();
  });

  it('fails loudly when the replay was written but could not be attached', async () => {
    // The file is on disk, so the save genuinely succeeded — but with nothing attached there is no capture to land in.
    // Reporting success would leave the user staring at an unchanged live view, looking like the command did nothing.
    spies.saveReplay.mockResolvedValue({
      data: { path: 'C:/tmp/foo.typhon-replay', bytesWritten: 100, profileId: null },
    });
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow(/could not be attached/i);
    expect(spies.toggleProfiler).not.toHaveBeenCalled();
  });

  it('rethrows a failed session re-read after the save succeeded', async () => {
    spies.saveReplay.mockResolvedValue({
      data: { path: 'C:/tmp/foo.typhon-replay', bytesWritten: 100, profileId: 'profile-1' },
    });
    spies.getSession.mockRejectedValue(new Error('session read failed'));
    await expect(captureAndAnalyse('attach-sess')).rejects.toThrow('session read failed');
    expect(spies.toggleProfiler).not.toHaveBeenCalled();
  });
});
