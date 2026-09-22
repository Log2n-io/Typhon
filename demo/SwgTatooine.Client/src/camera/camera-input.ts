import { DragKind, DragTracker } from './drag-tracker';
import { MapCamera, type Ray } from './map-camera';

export interface CameraInputHandlers {
  /** A click without a drag, in CSS pixels relative to the canvas. */
  onClick(x: number, y: number): void;
  /** The user moved the camera by hand: following stops. */
  onManualMove(): void;
}

/** The canvas size in CSS pixels, kept current by its owner: reading layout on every pointer event would force reflows. */
export interface ViewportSize {
  readonly width: number;
  readonly height: number;
}

const RIGHT_BUTTON = 2;
const RIGHT_BIT = 2;

/**
 * Mouse and keyboard for the map camera. Left drag pans (the ground point under the cursor follows it), right or middle
 * drag orbits, the wheel zooms toward the cursor; WASD / arrows pan, Q / E rotate, R / F tilt, + / − zoom. A cursor ray that
 * grazes the horizon is ignored rather than trusted: there, one pixel spans kilometres.
 */
export class CameraInput {
  private readonly canvas: HTMLCanvasElement;
  private readonly camera: MapCamera;
  private readonly size: ViewportSize;
  private readonly handlers: CameraInputHandlers;
  private readonly keys = new Set<string>();
  private readonly drag = new DragTracker();
  private readonly ray: Ray = { ox: 0, oy: 0, oz: 0, dx: 0, dy: 0, dz: 0 };
  private readonly before = new Float64Array(2);
  private readonly after = new Float64Array(2);
  private readonly abort = new AbortController();
  /**
   * The right button went down or up during a canvas drag. Windows opens the context menu on the release, over whatever is
   * under the pointer then — a panel, if the orbit ended there — so the next context menu is swallowed.
   */
  private swallowContextMenu = false;

  constructor(canvas: HTMLCanvasElement, camera: MapCamera, size: ViewportSize, handlers: CameraInputHandlers) {
    this.canvas = canvas;
    this.camera = camera;
    this.size = size;
    this.handlers = handlers;
    const signal = this.abort.signal;
    const on = <K extends keyof HTMLElementEventMap>(
      target: HTMLElement | Window,
      type: K,
      listener: (e: HTMLElementEventMap[K]) => void,
      options: AddEventListenerOptions = {},
    ): void => {
      target.addEventListener(type, listener as EventListener, { ...options, signal });
    };

    on(canvas, 'pointerdown', (e) => {
      this.onPointerDown(e);
    });
    on(canvas, 'pointermove', (e) => {
      this.onPointerMove(e);
    });
    on(canvas, 'pointerup', (e) => {
      this.onPointerUp(e);
    });
    on(canvas, 'pointercancel', (e) => {
      this.drag.cancel(e.pointerId);
    });
    on(canvas, 'lostpointercapture', (e) => {
      this.drag.cancel(e.pointerId);
    });
    on(
      canvas,
      'wheel',
      (e) => {
        this.onWheel(e);
      },
      { passive: false },
    );
    on(
      window,
      'pointerdown',
      (e) => {
        // A press anywhere else starts afresh: a later right-click on a panel gets its menu.
        if (e.target !== canvas) {
          this.swallowContextMenu = false;
        }
      },
      { capture: true },
    );
    on(
      window,
      'contextmenu',
      (e) => {
        if (e.target === canvas || this.swallowContextMenu) {
          e.preventDefault();
          this.swallowContextMenu = false;
        }
      },
      { capture: true },
    );
    on(window, 'keydown', (e) => {
      if (!isTyping(e)) {
        this.keys.add(e.code);
      }
    });
    on(window, 'keyup', (e) => {
      this.keys.delete(e.code);
    });
    on(window, 'blur', () => {
      this.keys.clear();
      this.drag.cancel();
    });
  }

  dispose(): void {
    this.abort.abort();
  }

  /** Applies held keys for `dt` seconds. */
  update(dt: number): void {
    const keys = this.keys;
    if (keys.size === 0) {
      return;
    }

    const speed = this.camera.panSpeed * dt * (keys.has('ShiftLeft') || keys.has('ShiftRight') ? 3 : 1);
    let right = 0;
    let forward = 0;
    if (keys.has('KeyW') || keys.has('ArrowUp')) forward += speed;
    if (keys.has('KeyS') || keys.has('ArrowDown')) forward -= speed;
    if (keys.has('KeyD') || keys.has('ArrowRight')) right += speed;
    if (keys.has('KeyA') || keys.has('ArrowLeft')) right -= speed;
    if (right !== 0 || forward !== 0) {
      this.camera.panScreen(right, forward);
      this.handlers.onManualMove();
    }

    const turn = 1.6 * dt;
    if (keys.has('KeyQ')) this.camera.rotateBy(-turn, 0);
    if (keys.has('KeyE')) this.camera.rotateBy(turn, 0);
    if (keys.has('KeyR')) this.camera.rotateBy(0, turn * 0.6);
    if (keys.has('KeyF')) this.camera.rotateBy(0, -turn * 0.6);
    if (keys.has('Equal') || keys.has('NumpadAdd')) this.camera.zoomBy(Math.exp(-2 * dt));
    if (keys.has('Minus') || keys.has('NumpadSubtract')) this.camera.zoomBy(Math.exp(2 * dt));
  }

  private onPointerDown(e: PointerEvent): void {
    // The canvas owns the pointer: no text selection, no middle-click autoscroll. Preventing the default also keeps focus
    // where it was, so take it: a toolbar slider that kept focus would eat the arrow keys meant for the camera.
    e.preventDefault();
    this.canvas.focus({ preventScroll: true });
    if (e.button === RIGHT_BUTTON) {
      this.swallowContextMenu = true;
    }

    if (!this.drag.down(e.pointerId, e.button, e.offsetX, e.offsetY)) {
      return;
    }

    try {
      this.canvas.setPointerCapture(e.pointerId);
    } catch {
      // A pointer the browser does not track (a synthetic event) cannot be captured; the drag still works over the canvas.
    }
  }

  private onPointerMove(e: PointerEvent): void {
    if (this.drag.kind !== DragKind.None && (e.buttons & RIGHT_BIT) !== 0) {
      this.swallowContextMenu = true;
    }

    const x = e.offsetX;
    const y = e.offsetY;
    if (!this.drag.move(e.pointerId, e.buttons, x, y)) {
      return;
    }

    if (this.drag.kind === DragKind.Pan) {
      if (this.groundAt(this.drag.lastX, this.drag.lastY, this.before) && this.groundAt(x, y, this.after)) {
        this.camera.dragBy(this.before[0] - this.after[0], this.before[1] - this.after[1]);
        this.handlers.onManualMove();
      }
    } else {
      this.camera.rotateBy((x - this.drag.lastX) * 0.005, (y - this.drag.lastY) * 0.004);
    }

    this.drag.moveTo(x, y);
  }

  private onPointerUp(e: PointerEvent): void {
    if (this.drag.up(e.pointerId, e.buttons)) {
      this.handlers.onClick(e.offsetX, e.offsetY);
    }
  }

  private onWheel(e: WheelEvent): void {
    e.preventDefault();
    const factor = Math.exp(Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY), 300) * 0.0015);
    if (this.groundAt(e.offsetX, e.offsetY, this.after)) {
      this.camera.zoomBy(factor, this.after[0], this.after[1]);
    } else {
      this.camera.zoomBy(factor);
    }
  }

  private groundAt(x: number, y: number, out: Float64Array): boolean {
    const { width, height } = this.size;
    this.camera.rayThrough(x, y, width, height, this.ray);
    return MapCamera.groundHit(this.ray, out, this.camera.maxGroundHitM(height));
  }
}

function isTyping(e: KeyboardEvent): boolean {
  const target = e.target;
  return (
    target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement
  );
}
