/**
 * Which drag is in progress, from raw pointer events — kept apart from the DOM so it can be tested.
 *
 * Browsers send `pointerdown` for the first button only and `pointerup` for the last one; pressing or releasing a second
 * button while one is held arrives as a `pointermove` with a different `buttons` mask. So a drag ends when the mask no
 * longer holds its button, whatever event carries that news, and a cancel ends it too. One pointer drives a drag: a second
 * finger or pen is ignored until the first is lifted.
 */

export const DragKind = { None: 0, Pan: 1, Orbit: 2 } as const;
export type DragKindValue = (typeof DragKind)[keyof typeof DragKind];

const DRAG_THRESHOLD_PX = 4;

/** `PointerEvent.button` → its bit in `PointerEvent.buttons`: left, middle, right. */
function buttonBit(button: number): number {
  return button === 0 ? 1 : button === 1 ? 4 : 2;
}

export class DragTracker {
  kind: DragKindValue = DragKind.None;
  /** Whether the pointer moved past the threshold: a release without it is a click. */
  moved = false;
  lastX = 0;
  lastY = 0;
  /** The button that started the drag (0 left, 1 middle, 2 right), or -1. */
  button = -1;
  private pointerId = -1;
  private startX = 0;
  private startY = 0;

  /** Returns true when this press starts a drag: no drag in progress, and the left, middle or right button. */
  down(pointerId: number, button: number, x: number, y: number): boolean {
    if (this.kind !== DragKind.None || button < 0 || button > 2) {
      return false;
    }

    this.kind = button === 0 ? DragKind.Pan : DragKind.Orbit;
    this.button = button;
    this.pointerId = pointerId;
    this.moved = false;
    this.startX = this.lastX = x;
    this.startY = this.lastY = y;
    return true;
  }

  /**
   * A move with the current button mask. Returns true when the drag should act on this move; false while under the
   * threshold, when no drag is in progress, for another pointer, or when the drag just ended because its button is no
   * longer held.
   */
  move(pointerId: number, buttons: number, x: number, y: number): boolean {
    if (this.kind === DragKind.None || pointerId !== this.pointerId) {
      return false;
    }

    if ((buttons & buttonBit(this.button)) === 0) {
      this.cancel();
      return false;
    }

    if (!this.moved && Math.hypot(x - this.startX, y - this.startY) < DRAG_THRESHOLD_PX) {
      return false;
    }

    this.moved = true;
    return true;
  }

  /** Records where the last acted-on move was. */
  moveTo(x: number, y: number): void {
    this.lastX = x;
    this.lastY = y;
  }

  /** A release. Returns true when it completes a click (the drag's own pointer and button, never moved, a pan). */
  up(pointerId: number, buttons: number): boolean {
    if (this.kind === DragKind.None || pointerId !== this.pointerId || (buttons & buttonBit(this.button)) !== 0) {
      return false;
    }

    const click = !this.moved && this.kind === DragKind.Pan;
    this.cancel();
    return click;
  }

  /** Ends the drag of `pointerId` (a cancelled or lost pointer); any pointer when omitted. */
  cancel(pointerId?: number): void {
    if (pointerId !== undefined && pointerId !== this.pointerId) {
      return;
    }

    this.kind = DragKind.None;
    this.button = -1;
    this.pointerId = -1;
    this.moved = false;
  }
}
