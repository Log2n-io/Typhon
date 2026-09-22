import { CITIES, POIS } from '../data/world-data';
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

/**
 * Place names and the selected entity's tag, as DOM text over the canvas: a handful of labels, projected each frame, with
 * the browser's own text rendering. A label's style is written only when it appears, hides or moves by a pixel, so a
 * still camera touches no style.
 */
export class Labels {
  private readonly root: HTMLDivElement;
  private readonly places: Label[] = [];
  private readonly selection: Label;
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
  update(matrix: ArrayLike<number>, originX: number, originZ: number, width: number, height: number): void {
    for (const label of this.places) {
      const s = projectToScreen(matrix, label.x - originX, label.lift, label.z - originZ, width, height, this.screen);
      const inside = s.w > 0 && s.x > -200 && s.x < width + 200 && s.y > -50 && s.y < height + 50;
      this.place(label, inside, s.x, s.y);
    }
  }

  /** The selected entity's tag at a planet position; `text` is written only when it differs from what is shown. */
  updateSelection(
    matrix: ArrayLike<number>,
    originX: number,
    originZ: number,
    width: number,
    height: number,
    x: number,
    z: number,
    text: string,
  ): void {
    const label = this.selection;
    const s = projectToScreen(matrix, x - originX, label.lift, z - originZ, width, height, this.screen);
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

  /** Hides every label until the next update. */
  hideAll(): void {
    for (const label of this.places) {
      this.place(label, false, 0, 0);
    }

    this.hideSelection();
  }

  dispose(): void {
    this.root.remove();
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
