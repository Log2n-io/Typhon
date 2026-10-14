import { beforeEach, describe, expect, it } from 'vitest';
import { CameraInput } from '../src/camera/camera-input';
import { EyeCamera } from '../src/camera/eye-camera';
import { MapCamera } from '../src/camera/map-camera';

/**
 * Input routing, which had no test at all and is where a whole class of defect lives: it cannot throw, cannot fail a
 * typecheck, and only shows as the controls feeling wrong. The R/F tilt disagreed in sign between the two cameras for
 * exactly that reason.
 *
 * The DOM here is the smallest thing the class actually uses — `addEventListener`, `focus`, `setPointerCapture` — because
 * the point is the routing, not Babylon.
 */

interface Handler {
  type: string;
  listener: (event: unknown) => void;
}

/**
 * The suite runs in `node`, which has no DOM. `CameraInput` listens on `window` for the keyboard (so a key pressed over a
 * panel still reaches the camera) and calls `instanceof HTMLInputElement` to ignore typing. Three stubs are all it takes;
 * pulling in jsdom for this would be a dependency to test four `if` statements.
 */
function installDomStubs(handlers: Handler[]): void {
  const target = globalThis as unknown as Record<string, unknown>;
  target['window'] = {
    addEventListener: (type: string, listener: (event: unknown) => void) => {
      handlers.push({ type, listener });
    },
    removeEventListener: () => {},
  };
  // `isTyping` tests `target instanceof HTMLInputElement`, which throws when the global is undefined. Any constructor
  // nothing is an instance of will do, and a function is one without being an empty class.
  const absent = function Absent(): void {};
  target['HTMLInputElement'] = absent;
  target['HTMLSelectElement'] = absent;
  target['HTMLTextAreaElement'] = absent;
}

function stubCanvas(handlers: Handler[]): HTMLCanvasElement {
  const canvas = {
    addEventListener: (type: string, listener: (event: unknown) => void) => {
      handlers.push({ type, listener });
    },
    focus: () => {},
    setPointerCapture: () => {},
    releasePointerCapture: () => {},
  };
  return canvas as unknown as HTMLCanvasElement;
}

/** Fires a keyup, so a held key does not leak into the next case. */
function release(handlers: Handler[], code: string, atMs = 0): void {
  for (const h of handlers) {
    if (h.type === 'keyup') {
      h.listener({ code, target: null, timeStamp: atMs });
    }
  }
}

/** Fires a keydown for `code` through whichever window listener the input registered. */
function press(handlers: Handler[], code: string, atMs = 0): void {
  for (const h of handlers) {
    if (h.type === 'keydown') {
      h.listener({ code, target: null, timeStamp: atMs });
    }
  }
}

describe('CameraInput routing', () => {
  let handlers: Handler[];
  let map: MapCamera;
  let eye: EyeCamera;
  let input: CameraInput;
  let isEye = false;

  beforeEach(() => {
    handlers = [];
    installDomStubs(handlers);
    map = new MapCamera();
    eye = new EyeCamera();
    isEye = false;
    input = new CameraInput(
      stubCanvas(handlers),
      map,
      { width: 800, height: 600 },
      { onClick: () => {}, onManualMove: () => {} },
      eye,
      () => isEye,
    );
  });

  /**
   * Applies held keys and then lets both cameras smooth. `rotateBy` and `panScreen` move a GOAL; nothing moves until the
   * camera itself updates, so a test that only called `input.update` would read zero deltas and pass whatever the routing
   * did.
   */
  const settle = (dt = 0.1): void => {
    input.update(dt);
    map.update(dt);
    eye.update(dt);
  };

  /** Presses a key in one mode and reports what each camera's angle did. Only the ACTIVE camera is routed to, so the two
   * modes have to be exercised separately — an earlier version pressed once and compared, and read a zero delta from the
   * camera the input was never routed to. */
  const tiltIn = (mode: 'eye' | 'map', code: string, read: (c: MapCamera | EyeCamera) => number): number => {
    isEye = mode === 'eye';
    const camera = isEye ? eye : map;
    const before = read(camera);
    press(handlers, code);
    settle();
    release(handlers, code);
    return read(camera) - before;
  };

  it('tilts the same way in both modes for the same key', () => {
    // The defect this exists for: R tilted one way with the map camera and the other way in eye mode. Asserted as a
    // comparison between the two cameras rather than against a fixed sign, because the convention is allowed to change —
    // the two disagreeing is what is never allowed.
    map.jumpTo(0, 0, 500);
    eye.jumpTo(0, 0, 0, 0);
    const mapDelta = tiltIn('map', 'KeyR', (c) => c.pitch);
    const eyeDelta = tiltIn('eye', 'KeyR', (c) => c.pitch);

    expect(mapDelta).not.toBe(0);
    expect(eyeDelta).not.toBe(0);
    expect(Math.sign(mapDelta)).toBe(Math.sign(eyeDelta));
  });

  it('turns the same way in both modes for the same key', () => {
    map.jumpTo(0, 0, 500);
    eye.jumpTo(0, 0, 0, 0);
    const mapDelta = tiltIn('map', 'KeyE', (c) => c.yaw);
    const eyeDelta = tiltIn('eye', 'KeyE', (c) => c.yaw);

    expect(mapDelta).not.toBe(0);
    expect(eyeDelta).not.toBe(0);
    expect(Math.sign(mapDelta)).toBe(Math.sign(eyeDelta));
  });

  it('gives the spectator no way to move: WASD is inert in eye mode and the map camera is not driven either', () => {
    // The spectator rule lives in a comment today. This pins it: in eye mode no key may move anything's position.
    isEye = true;
    eye.jumpTo(100, 5, 200, 0);
    map.jumpTo(0, 0, 500);
    const mapTargetX = map.targetX;
    const mapTargetZ = map.targetZ;
    const anchorX = eye.targetX;
    const anchorZ = eye.targetZ;

    for (const code of ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']) {
      press(handlers, code);
    }

    settle();
    expect(eye.targetX).toBe(anchorX);
    expect(eye.targetZ).toBe(anchorZ);
    // And the map camera, which is still alive underneath, must not have been panned either.
    expect(map.targetX).toBe(mapTargetX);
    expect(map.targetZ).toBe(mapTargetZ);
  });

  it('pans the map camera with WASD when the map camera IS the active one', () => {
    // The other half of the test above: it would also pass if WASD were dead everywhere.
    isEye = false;
    map.jumpTo(0, 0, 500);
    press(handlers, 'KeyW');
    settle();
    expect(map.targetZ).not.toBe(0);
  });
});

/**
 * When the held set last changed, which A14b needs to be the KEY's instant and not the frame's.
 *
 * A frame notices a press up to a frame after it happened, and both the predictor and the server are told how far into
 * their own step the intent changed — so a frame-rounded instant puts a sixteenth of a second of travel back into the
 * figure the sub-tick phase exists to make exact.
 */
describe('CameraInput key instants', () => {
  let handlers: Handler[];
  let input: CameraInput;

  beforeEach(() => {
    handlers = [];
    installDomStubs(handlers);
    input = new CameraInput(
      stubCanvas(handlers),
      new MapCamera(),
      { width: 800, height: 600 },
      { onClick: () => {}, onManualMove: () => {} },
      new EyeCamera(),
      () => true,
    );
  });

  it('is NaN until a key has moved, so a caller can tell it apart from a real zero', () => {
    expect(input.keysChangedAtMs).toBeNaN();
  });

  it('takes the event timestamp of a press, and of the release that ends it', () => {
    press(handlers, 'KeyW', 1234.5);
    expect(input.keysChangedAtMs).toBe(1234.5);
    release(handlers, 'KeyW', 1890.25);
    expect(input.keysChangedAtMs).toBe(1890.25);
  });

  /**
   * The browser delivers `keydown` every few tens of milliseconds for a key that is already down. Taking those would
   * re-stamp a held key for ever, and the client would then report a fresh "the intent just changed" on whichever frame
   * the repeat landed in — a phase that is a lie about an intent that has not moved since.
   */
  it('ignores auto-repeat, which is a press that adds nothing', () => {
    press(handlers, 'KeyW', 1000);
    press(handlers, 'KeyW', 1050);
    press(handlers, 'KeyW', 1100);
    expect(input.keysChangedAtMs).toBe(1000);
  });

  it('ignores a release of something that was never held', () => {
    press(handlers, 'KeyW', 1000);
    release(handlers, 'KeyA', 1200);
    expect(input.keysChangedAtMs).toBe(1000);
  });

  it('is not moved by a keystroke that went to a text field', () => {
    // `isTyping` already stops those reaching the held set; this says the instant does not move either, or chatting would
    // tell the predictor the avatar's gait had just changed.
    press(handlers, 'KeyW', 1000);
    for (const h of handlers) {
      if (h.type === 'keydown') {
        h.listener({
          code: 'KeyS',
          target: new (globalThis as unknown as { HTMLInputElement: new () => unknown }).HTMLInputElement(),
          timeStamp: 1500,
        });
      }
    }

    expect(input.keysChangedAtMs).toBe(1000);
  });
});
