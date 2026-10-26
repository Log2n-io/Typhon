import { projectToScreen, type ScreenPoint } from './view-math';

/**
 * A body's box in its own frame, metres: x across it, y up from its feet, z along its facing — the frame a descriptor's
 * bounds and a style's size are both given in.
 */
export interface BodyBox {
  readonly minX: number;
  readonly minY: number;
  readonly minZ: number;
  readonly maxX: number;
  readonly maxY: number;
  readonly maxZ: number;
}

/** A rectangle on screen, CSS pixels from the canvas's top-left corner. */
export interface ScreenRect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/** How a frame is drawn: under the cursor, selected, or selected and in a fight with the viewer. */
export type FrameTone = 'hover' | 'passive' | 'hostile';

/** The box a style's size draws: x and z in [−½, ½] of it, y in [0, 1] (`meshes.ts`). */
export function styleBox(width: number, height: number, length: number): BodyBox {
  return {
    minX: -0.5 * width,
    minY: 0,
    minZ: -0.5 * length,
    maxX: 0.5 * width,
    maxY: height,
    maxZ: 0.5 * length,
  };
}

/**
 * The box round a published body, from its descriptor's bounds.
 *
 * <b>A character's render bounds are its bind pose, arms out</b>: ±0.85 m across for a body 0.5 m wide standing, which
 * framed every person as twice as wide as they are. A character takes its width from its collision box — the body the
 * server walks — and its height from what is drawn. A creature's bind pose is how it stands, so its render box is its
 * silhouette.
 */
export function bodyBox(
  kind: string,
  render: readonly [readonly number[], readonly number[]],
  collision: readonly [readonly number[], readonly number[]] | null,
): BodyBox {
  const [lo, hi] = render;
  if (kind === 'character' && collision !== null) {
    const [clo, chi] = collision;
    return { minX: clo[0], minY: lo[1], minZ: clo[2], maxX: chi[0], maxY: hi[1], maxZ: chi[2] };
  }

  return { minX: lo[0], minY: lo[1], minZ: lo[2], maxX: hi[0], maxY: hi[1], maxZ: hi[2] };
}

/**
 * The screen rectangle round a box standing at render-space `(x, y, z)` and turned by `yaw`.
 *
 * The yaw is the instance shaders' (`turned = (lx·c + lz·s, ly, −lx·s + lz·c)`), so the frame turns with what is drawn. The
 * eight corners are projected and bounded: the projection of a box's hull is the hull of its corners' projections.
 *
 * @returns False when any corner is behind the camera — a rectangle round a body the viewer straddles frames nothing — or
 * when the rectangle is wholly off screen.
 */
export function projectBox(
  m: ArrayLike<number>,
  x: number,
  y: number,
  z: number,
  yaw: number,
  box: BodyBox,
  width: number,
  height: number,
  scratch: ScreenPoint,
  out: ScreenRect,
): boolean {
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);
  let left = Infinity;
  let top = Infinity;
  let right = -Infinity;
  let bottom = -Infinity;
  for (let i = 0; i < 8; i++) {
    const lx = (i & 1) === 0 ? box.minX : box.maxX;
    const ly = (i & 2) === 0 ? box.minY : box.maxY;
    const lz = (i & 4) === 0 ? box.minZ : box.maxZ;
    const p = projectToScreen(m, x + lx * c + lz * s, y + ly, z - lx * s + lz * c, width, height, scratch);
    if (!(p.w > 0) || !Number.isFinite(p.x)) {
      return false;
    }

    left = Math.min(left, p.x);
    right = Math.max(right, p.x);
    top = Math.min(top, p.y);
    bottom = Math.max(bottom, p.y);
  }

  if (right < 0 || left > width || bottom < 0 || top > height) {
    return false;
  }

  out.left = left;
  out.top = top;
  out.right = right;
  out.bottom = bottom;
  return true;
}

/**
 * The screen rectangle round eight corners given in PLANET coordinates (x y z each) — a part of a structure, whose box
 * is turned by its own pose rather than by a yaw. Moved into render space by the origin, as every instance is.
 *
 * @returns False when any corner is behind the camera, or the rectangle is wholly off screen.
 */
export function projectCorners(
  m: ArrayLike<number>,
  corners: Float64Array,
  originX: number,
  originZ: number,
  width: number,
  height: number,
  scratch: ScreenPoint,
  out: ScreenRect,
): boolean {
  let left = Infinity;
  let top = Infinity;
  let right = -Infinity;
  let bottom = -Infinity;
  for (let i = 0; i < 8; i++) {
    const p = projectToScreen(
      m,
      corners[i * 3] - originX,
      corners[i * 3 + 1],
      corners[i * 3 + 2] - originZ,
      width,
      height,
      scratch,
    );
    if (!(p.w > 0) || !Number.isFinite(p.x)) {
      return false;
    }

    left = Math.min(left, p.x);
    right = Math.max(right, p.x);
    top = Math.min(top, p.y);
    bottom = Math.max(bottom, p.y);
  }

  if (right < 0 || left > width || bottom < 0 || top > height) {
    return false;
  }

  out.left = left;
  out.top = top;
  out.right = right;
  out.bottom = bottom;
  return true;
}

/** The smallest frame drawn, CSS pixels a side: four corners round a body a few pixels tall would read as one blob. */
export const FRAME_MIN_PX = 14;

/** A corner's arm, as a share of the frame's shorter side, within {@link CORNER_MIN_PX}–{@link CORNER_MAX_PX}. */
const CORNER_SHARE = 0.22;
const CORNER_MIN_PX = 4;
const CORNER_MAX_PX = 16;

/** How far a frame stands off the body's own box, CSS pixels: the corners frame the body rather than touch it. */
const FRAME_PAD_PX = 3;

interface Frame {
  readonly element: HTMLDivElement;
  shown: boolean;
  tone: FrameTone | null;
  /** The last written geometry, whole pixels: a style is written only when it changes. */
  x: number;
  y: number;
  w: number;
  h: number;
  corner: number;
}

/**
 * The hover and selection frames: the four corners of the rectangle a body covers on screen, as DOM over the canvas — the
 * same layer the tag and the health bars are drawn in, with the same write-on-change discipline.
 */
export class SelectionFrames {
  private readonly root: HTMLDivElement;
  private readonly frames: Frame[] = [];

  constructor(parent: HTMLElement) {
    this.root = document.createElement('div');
    this.root.className = 'sel-frames';
    parent.appendChild(this.root);
    for (let i = 0; i < 2; i++) {
      const element = document.createElement('div');
      element.className = 'sel-frame';
      element.style.display = 'none';
      for (const corner of ['tl', 'tr', 'bl', 'br']) {
        const arm = document.createElement('div');
        arm.className = `sel-corner ${corner}`;
        element.appendChild(arm);
      }

      this.root.appendChild(element);
      this.frames.push({ element, shown: false, tone: null, x: Number.NaN, y: Number.NaN, w: 0, h: 0, corner: 0 });
    }
  }

  /** Draws frame `slot` (0 the hover, 1 the selection) round `rect` in `tone`. */
  show(slot: 0 | 1, rect: ScreenRect, tone: FrameTone): void {
    const frame = this.frames[slot];
    const geometry = frameGeometry(rect);
    if (!frame.shown) {
      frame.element.style.display = 'block';
      frame.shown = true;
    }

    if (frame.tone !== tone) {
      if (frame.tone !== null) {
        frame.element.classList.remove(`tone-${frame.tone}`);
      }

      frame.element.classList.add(`tone-${tone}`);
      frame.tone = tone;
    }

    if (geometry.x !== frame.x || geometry.y !== frame.y) {
      frame.x = geometry.x;
      frame.y = geometry.y;
      frame.element.style.transform = `translate(${geometry.x}px, ${geometry.y}px)`;
    }

    if (geometry.w !== frame.w || geometry.h !== frame.h) {
      frame.w = geometry.w;
      frame.h = geometry.h;
      frame.element.style.width = `${geometry.w}px`;
      frame.element.style.height = `${geometry.h}px`;
    }

    if (geometry.corner !== frame.corner) {
      frame.corner = geometry.corner;
      frame.element.style.setProperty('--corner', `${geometry.corner}px`);
    }
  }

  hide(slot: 0 | 1): void {
    const frame = this.frames[slot];
    if (frame.shown) {
      frame.element.style.display = 'none';
      frame.shown = false;
    }
  }

  dispose(): void {
    this.root.remove();
  }
}

/**
 * Where a frame round `rect` is drawn, whole CSS pixels: padded off the body, grown about its centre to
 * {@link FRAME_MIN_PX}, its corners' arms a share of its shorter side.
 */
export function frameGeometry(rect: ScreenRect): { x: number; y: number; w: number; h: number; corner: number } {
  const cx = (rect.left + rect.right) * 0.5;
  const cy = (rect.top + rect.bottom) * 0.5;
  const w = Math.max(FRAME_MIN_PX, rect.right - rect.left + 2 * FRAME_PAD_PX);
  const h = Math.max(FRAME_MIN_PX, rect.bottom - rect.top + 2 * FRAME_PAD_PX);
  const corner = Math.round(Math.min(CORNER_MAX_PX, Math.max(CORNER_MIN_PX, Math.min(w, h) * CORNER_SHARE)));
  return { x: Math.round(cx - w * 0.5), y: Math.round(cy - h * 0.5), w: Math.round(w), h: Math.round(h), corner };
}
