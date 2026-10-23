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

/** How far the pointer must travel before a press becomes a drag rather than a press. */
const DRAG_THRESHOLD_PX = 4;

/**
 * How far the pointer may wander and still have the release count as a click.
 *
 * <b>Separate from {@link DRAG_THRESHOLD_PX}, and that is the point.</b> Reusing the pan threshold to disqualify a click
 * meant 4 px of hand movement silently lost the selection: measured through synthetic pointer events, 0–3 px selected 12
 * of 12 and 4 px selected 0 of 12, which is exactly "it works sometimes on the same object". Panning should start early,
 * because a drag that lags feels broken; selecting should forgive more, because a mouse is not a stylus.
 */
const CLICK_SLOP_PX = 10;

/** `PointerEvent.button` → its bit in `PointerEvent.buttons`: left, middle, right. */
function buttonBit(button: number): number {
  return button === 0 ? 1 : button === 1 ? 4 : 2;
}

export class DragTracker {
  kind: DragKindValue = DragKind.None;
  /** Whether the pointer moved past {@link DRAG_THRESHOLD_PX}: the drag is acting on moves. */
  moved = false;
  /** The farthest the pointer has been from where it was pressed, in pixels. A click is judged on this, not on {@link moved}. */
  maxMoved = 0;
  lastX = 0;
  lastY = 0;
  /** The button that started the drag (0 left, 1 middle, 2 right), or -1. */
  button = -1;
  private pointerId = -1;
  /** Where the press landed. A click is reported HERE, not at the release: you aim when you press. */
  startX = 0;
  startY = 0;

  /** Returns true when this press starts a drag: no drag in progress, and the left, middle or right button. */
  down(pointerId: number, button: number, x: number, y: number): boolean {
    if (this.kind !== DragKind.None || button < 0 || button > 2) {
      return false;
    }

    this.kind = button === 0 ? DragKind.Pan : DragKind.Orbit;
    this.button = button;
    this.pointerId = pointerId;
    this.moved = false;
    this.maxMoved = 0;
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

    // Recorded on EVERY move, including the ones under the threshold: what makes a release a click is how far the
    // pointer ever got, and the early return below would otherwise never see the small ones.
    const travelled = Math.hypot(x - this.startX, y - this.startY);
    if (travelled > this.maxMoved) {
      this.maxMoved = travelled;
    }

    if (!this.moved && travelled < DRAG_THRESHOLD_PX) {
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

    // By distance, not by the `moved` latch: a press that wandered 5 px both panned a little and selected, which is what
    // a person doing either one of them expects.
    const click = this.maxMoved <= CLICK_SLOP_PX && this.kind === DragKind.Pan;
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
    this.maxMoved = 0;
  }
}
