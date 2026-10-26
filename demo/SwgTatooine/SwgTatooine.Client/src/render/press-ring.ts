/**
 * The long press (2026-10-25, Loïc): held left on something that offers actions, a disc grows from the pointer to meet a
 * ring in {@link LONG_PRESS_MS}; then its context menu opens. Let go before it does, and it was a click.
 *
 * <b>Drawn in the page, with the system cursor hidden, rather than as an animated cursor.</b> A CSS cursor cannot be
 * animated reliably — a browser may not repaint a changed cursor until the mouse moves, and a long press is exactly a
 * mouse that does not move. A drawn cursor trails a moving mouse by a frame or more, which is why the ordinary cursors are
 * CSS ones (`game-cursor.ts`); a press that is still has nothing to trail.
 */

/** How long a press is held before the menu opens. */
export const LONG_PRESS_MS = 500;

/** How far a press has come, 0 when it starts to 1 when the menu opens. */
export function pressProgress(startMs: number, nowMs: number): number {
  return Math.min(1, Math.max(0, (nowMs - startMs) / LONG_PRESS_MS));
}

/** The ring and its growing disc, over the canvas at the pressed point. */
export class PressRing {
  private readonly element: HTMLDivElement;
  private readonly fill: HTMLDivElement;
  private shown = false;
  /** The last written position and scale: a style is written only when it changes. */
  private x = Number.NaN;
  private y = Number.NaN;
  private scale = -1;

  constructor(parent: HTMLElement) {
    this.element = document.createElement('div');
    this.element.className = 'press-ring';
    this.element.style.display = 'none';
    this.fill = document.createElement('div');
    this.fill.className = 'press-ring-fill';
    this.element.appendChild(this.fill);
    parent.appendChild(this.element);
  }

  /** Shows the ring at `(x, y)`, CSS pixels over the canvas, its disc grown to `progress` of it. */
  show(x: number, y: number, progress: number): void {
    if (!this.shown) {
      this.element.style.display = 'block';
      this.shown = true;
    }

    const px = Math.round(x);
    const py = Math.round(y);
    if (px !== this.x || py !== this.y) {
      this.x = px;
      this.y = py;
      this.element.style.transform = `translate(${px}px, ${py}px)`;
    }

    const scale = Math.round(progress * 100) / 100;
    if (scale !== this.scale) {
      this.scale = scale;
      this.fill.style.transform = `scale(${scale})`;
    }
  }

  hide(): void {
    if (this.shown) {
      this.element.style.display = 'none';
      this.shown = false;
    }
  }

  dispose(): void {
    this.element.remove();
  }
}
