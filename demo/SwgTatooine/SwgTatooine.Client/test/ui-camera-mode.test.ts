import { beforeEach, describe, expect, it } from 'vitest';

import { useUi } from '../src/state/ui-store';

/**
 * The camera-mode rules, which assumed eye mode always rides the SELECTION.
 *
 * That held until A10 gave a player an avatar of its own to ride. A player has an avatar and typically no selection at
 * all, so `setCameraMode('eye')` was refused silently and a logged-in player sat in the god camera for the whole
 * session — panning a free camera over a world that is, correctly, only streamed around the avatar, which is why it also
 * looked like replication had stopped. Found in a browser, not by this suite, because nothing here knew the two subjects
 * could differ.
 */

beforeEach(() => {
  useUi.setState({ cameraMode: 'god', selectedNetId: 0, follow: false, playing: false });
});

describe('setCameraMode', () => {
  it('refuses eye mode for a god camera with nothing selected', () => {
    useUi.getState().setCameraMode('eye');
    expect(useUi.getState().cameraMode).toBe('god');
  });

  it('allows eye mode for a god camera that has a selection', () => {
    useUi.setState({ selectedNetId: 77 });
    useUi.getState().setCameraMode('eye');
    expect(useUi.getState().cameraMode).toBe('eye');
  });

  /** The case that was broken: an avatar is a subject even though nothing is selected. */
  it('allows eye mode for a playing session with no selection', () => {
    useUi.setState({ playing: true });
    useUi.getState().setCameraMode('eye');
    expect(useUi.getState().cameraMode).toBe('eye');
  });

  it('still lets anyone go back to the god camera', () => {
    useUi.setState({ playing: true, cameraMode: 'eye' });
    useUi.getState().setCameraMode('god');
    expect(useUi.getState().cameraMode).toBe('god');
  });
});

describe('select', () => {
  it('drops a god camera out of eye mode when the selection is cleared', () => {
    useUi.setState({ selectedNetId: 77, cameraMode: 'eye' });
    useUi.getState().select(0);
    expect(useUi.getState().cameraMode).toBe('god');
  });

  /**
   * A9 makes clicking empty ground a legitimate way to clear the target. For a player that must not also mean "leave
   * third person", because the camera was never riding the selection.
   */
  it('keeps a playing session in eye mode when the selection is cleared', () => {
    useUi.setState({ playing: true, selectedNetId: 77, cameraMode: 'eye' });
    useUi.getState().select(0);

    expect(useUi.getState().cameraMode).toBe('eye');
    expect(useUi.getState().selectedNetId).toBe(0);
  });

  it('keeps the selection it was given either way', () => {
    useUi.getState().select(9);
    expect(useUi.getState().selectedNetId).toBe(9);
  });
});
