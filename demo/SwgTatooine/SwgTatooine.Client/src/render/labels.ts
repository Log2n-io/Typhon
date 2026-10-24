import { CITIES, POIS } from '../data/world-data';
import type { GroundSampler } from '../terrain/ground-sampler';
import { projectToScreen, type ScreenPoint } from './view-math';

interface Label {
  readonly element: HTMLDivElement;
  readonly x: number;
  readonly z: number;
  readonly lift: number;
  shown: boolean;
  /** Last written screen position, rounded: a style is written only when it changes. */
  px: number;
  py: number;
}

/** Selection tag offset above its anchor, CSS pixels. */
const SELECTION_OFFSET_PX = 18;

/** A health bar over a body: where its top is, in planet metres, and how much health it has left, 0–1. */
export interface HealthBar {
  x: number;
  y: number;
  z: number;
  fraction: number;
  /** A lair its occupants still guard: drawn shielded, because nothing damages it until they are dead. */
  shielded: boolean;
}

interface Bar {
  readonly element: HTMLDivElement;
  readonly fill: HTMLDivElement;
  shown: boolean;
  px: number;
  py: number;
  /** The width last written, in whole percent: a style is written only when it changes. */
  pct: number;
  shielded: boolean;
}

/**
 * Place names and the selected entity's tag, as DOM text over the canvas: a handful of labels, projected each frame, with
 * the browser's own text rendering. A label's style is written only when it appears, hides or moves by a pixel, so a
 * still camera touches no style.
 */
export class Labels {
  private readonly root: HTMLDivElement;
  private readonly places: Label[] = [];
  private readonly selection: Label;
  /** The health bars, pooled: one element per bar ever shown at once, hidden when unused. */
  private readonly bars: Bar[] = [];
  private readonly screen: ScreenPoint = { x: 0, y: 0, w: 0 };

  constructor(parent: HTMLElement) {
    this.root = document.createElement('div');
    this.root.className = 'labels';
    parent.appendChild(this.root);
    for (const city of CITIES) {
      this.places.push(this.create(city.name, 'label label-city', city.x, city.z, 40));
    }

    for (const poi of POIS) {
      this.places.push(this.create(poi.name, 'label label-poi', poi.x, poi.z, 25));
    }

    this.selection = this.create('', 'label label-selection', 0, 0, 3);
  }

  /** Places every label for a view-projection matrix and a CSS viewport. */
  update(
    matrix: ArrayLike<number>,
    originX: number,
    originZ: number,
    width: number,
    height: number,
    ground: GroundSampler,
  ): void {
    for (const label of this.places) {
      // The lift is above the GROUND, not above sea level: a town on a mesa keeps its name over the town.
      const y = label.lift + ground.heightAt(label.x, label.z);
      const s = projectToScreen(matrix, label.x - originX, y, label.z - originZ, width, height, this.screen);
      const inside = s.w > 0 && s.x > -200 && s.x < width + 200 && s.y > -50 && s.y < height + 50;
      this.place(label, inside, s.x, s.y);
    }
  }

  /**
   * The selected entity's tag at a planet position; `text` is written only when it differs from what is shown.
   *
   * `baseY` is the entity's OWN altitude — which already includes the ground under it — not a height re-derived from the
   * terrain here. Re-deriving it agrees for anything standing on the ground and puts the tag on the ground beneath
   * anything that is not, which is exactly the case `pickInstances` was corrected for: an entity picked where it is drawn
   * and labelled somewhere else.
   */
  updateSelection(
    matrix: ArrayLike<number>,
    originX: number,
    originZ: number,
    width: number,
    height: number,
    x: number,
    z: number,
    text: string,
    baseY: number,
  ): void {
    const label = this.selection;
    const y = label.lift + baseY;
    const s = projectToScreen(matrix, x - originX, y, z - originZ, width, height, this.screen);
    // Behind the camera the projection is NaN: hidden, never written.
    const shown = Number.isFinite(s.x);
    if (shown && label.element.textContent !== text) {
      label.element.textContent = text;
    }

    this.place(label, shown, s.x, s.y - SELECTION_OFFSET_PX);
  }

  hideSelection(): void {
    this.place(this.selection, false, 0, 0);
  }

  /**
   * Draws a health bar over each of the first `count` bodies — the enemies fighting this session's avatar — and hides the
   * rest of the pool.
   */
  updateBars(
    matrix: ArrayLike<number>,
    originX: number,
    originZ: number,
    width: number,
    height: number,
    bodies: readonly HealthBar[],
    count: number,
  ): void {
    for (let i = 0; i < count; i++) {
      if (i >= this.bars.length) {
        const element = document.createElement('div');
        element.className = 'label hpbar';
        element.style.display = 'none';
        const fill = document.createElement('div');
        fill.className = 'hpbar-fill';
        element.appendChild(fill);
        this.root.appendChild(element);
        this.bars.push({ element, fill, shown: false, px: Number.NaN, py: Number.NaN, pct: -1, shielded: false });
      }

      const body = bodies[i];
      const bar = this.bars[i];
      const s = projectToScreen(matrix, body.x - originX, body.y, body.z - originZ, width, height, this.screen);
      // On screen or near it, as the place names are: off it, a bar is a style written for nothing.
      const shown = s.w > 0 && s.x > -100 && s.x < width + 100 && s.y > -50 && s.y < height + 50;
      this.placeBar(bar, shown, s.x, s.y);
      if (body.shielded !== bar.shielded) {
        bar.shielded = body.shielded;
        bar.element.classList.toggle('shielded', body.shielded);
      }

      const pct = Math.round(Math.max(0, Math.min(1, body.fraction)) * 100);
      if (shown && pct !== bar.pct) {
        bar.pct = pct;
        bar.fill.style.width = `${String(pct)}%`;
      }
    }

    for (let i = count; i < this.bars.length; i++) {
      this.placeBar(this.bars[i], false, 0, 0);
    }
  }

  /**
   * Hides the place names, leaving the selection's tag alone.
   *
   * <b>Place names belong to the planet.</b> They are built once from `CITIES` and `POIS` and are Tatooine's towns; in a
   * room or in space they are names of somewhere else entirely, and they were drawn there — "Port Sable" floating in
   * orbit — because nothing had ever needed to turn them off separately from the selection, which is realm-agnostic
   * and must keep working.
   */
  hidePlaces(): void {
    for (const label of this.places) {
      this.place(label, false, 0, 0);
    }
  }

  /** Hides every label until the next update. */
  hideAll(): void {
    for (const label of this.places) {
      this.place(label, false, 0, 0);
    }

    this.hideSelection();
    for (const bar of this.bars) {
      this.placeBar(bar, false, 0, 0);
    }
  }

  dispose(): void {
    this.root.remove();
  }

  private placeBar(bar: Bar, shown: boolean, x: number, y: number): void {
    if (shown !== bar.shown) {
      bar.element.style.display = shown ? 'block' : 'none';
      bar.shown = shown;
    }

    if (!shown) {
      return;
    }

    const px = Math.round(x);
    const py = Math.round(y);
    if (px !== bar.px || py !== bar.py) {
      bar.px = px;
      bar.py = py;
      bar.element.style.transform = `translate(${String(px)}px, ${String(py)}px)`;
    }
  }

  private place(label: Label, shown: boolean, x: number, y: number): void {
    if (shown !== label.shown) {
      label.element.style.display = shown ? 'block' : 'none';
      label.shown = shown;
    }

    if (!shown) {
      return;
    }

    const px = Math.round(x);
    const py = Math.round(y);
    if (px !== label.px || py !== label.py) {
      label.px = px;
      label.py = py;
      label.element.style.transform = `translate(${px}px, ${py}px)`;
    }
  }

  private create(text: string, className: string, x: number, z: number, lift: number): Label {
    const element = document.createElement('div');
    element.className = className;
    element.textContent = text;
    element.style.display = 'none';
    this.root.appendChild(element);
    return { element, x, z, lift, shown: false, px: Number.NaN, py: Number.NaN };
  }
}
