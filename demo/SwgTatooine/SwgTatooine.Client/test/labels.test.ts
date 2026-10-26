import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { entityTag, type EntityTag } from '../src/data/content-level';
import { BAR_STACK_PX, Labels, pileOn, STACK_GAP_PX, TAG_STACK_PX, type HealthBar } from '../src/render/labels';

/** The tags over what is selected and what is hovered (Loïc, 2026-10-26): the same label, said the same way. */

interface FakeText {
  data: string;
}

interface FakeElement {
  className: string;
  textContent: string;
  style: Record<string, string>;
  children: FakeElement[];
  /** What `append` put in, text and elements, in order: a tag's name and its level. */
  nodes: (FakeElement | FakeText)[];
  appendChild(child: FakeElement): void;
  append(...nodes: (FakeElement | FakeText | string)[]): void;
  remove(): void;
}

function fakeElement(): FakeElement {
  const element: FakeElement = {
    className: '',
    textContent: '',
    style: {},
    children: [],
    nodes: [],
    appendChild: (child) => {
      element.children.push(child);
    },
    append: (...nodes) => {
      for (const node of nodes) {
        element.nodes.push(typeof node === 'string' ? { data: node } : node);
      }
    },
    remove: () => {},
  };
  return element;
}

const fakeDocument = { createElement: () => fakeElement(), createTextNode: (data: string): FakeText => ({ data }) };

/** What a label reads on screen: its text and its shown nodes', in order. */
function shownText(element: FakeElement): string {
  if (element.style.display === 'none') {
    return '';
  }

  let text = element.textContent;
  for (const node of element.nodes) {
    text += 'data' in node ? node.data : shownText(node);
  }

  return text;
}

/** The element a tag's level is drawn in: the one coloured part. */
function levelOf(label: FakeElement): FakeElement {
  const suffix = label.nodes[1] as FakeElement;
  return suffix.nodes[1] as FakeElement;
}

const tag = (name: string): EntityTag => entityTag(name, 0, null);

describe('the hover tag', () => {
  let parent: FakeElement;

  beforeEach(() => {
    vi.stubGlobal('document', fakeDocument);
    parent = fakeElement();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** The selection's tag and the hover's: the two last labels made, after the places. */
  const tags = (): { selection: FakeElement; hover: FakeElement } => {
    const all = parent.children[0].children;
    return { selection: all[all.length - 2], hover: all[all.length - 1] };
  };

  it("is the selection's tag, styled and placed the same way", () => {
    const labels = new Labels(parent as unknown as HTMLElement);
    labels.placeSelection(100, 50, tag('Bazaar'));
    labels.placeHover(100, 50, tag('Bazaar'));
    const { selection, hover } = tags();
    expect(hover.className).toBe(selection.className);
    expect(shownText(hover)).toBe('Bazaar');
    expect(hover.style).toEqual(selection.style);
  });

  it('shows beside the selection, and hides on its own and with every label', () => {
    const labels = new Labels(parent as unknown as HTMLElement);
    labels.placeSelection(10, 10, tag('Player #12'));
    labels.placeHover(300, 200, tag('Mission terminal'));
    const { selection, hover } = tags();
    expect([selection.style.display, hover.style.display]).toEqual(['block', 'block']);
    labels.hideHover();
    expect([selection.style.display, hover.style.display]).toEqual(['block', 'none']);
    labels.placeHover(300, 200, tag('Mission terminal'));
    labels.hideAll();
    expect([selection.style.display, hover.style.display]).toEqual(['none', 'none']);
  });
});

describe("a tag's level", () => {
  let parent: FakeElement;

  beforeEach(() => {
    vi.stubGlobal('document', fakeDocument);
    parent = fakeElement();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const selectionOf = (): FakeElement => {
    const all = parent.children[0].children;
    return all[all.length - 2];
  };

  it('follows the name, and only the level carries the con colour', () => {
    const labels = new Labels(parent as unknown as HTMLElement);
    labels.placeSelection(10, 10, entityTag('Krayt dragon', 23, 20));
    const label = selectionOf();
    expect(shownText(label)).toBe('Krayt dragon (CL 23)');
    expect(levelOf(label).textContent).toBe('CL 23');
    expect(levelOf(label).className).toBe('con-yellow');
    expect(label.className).toBe('label label-selection');
  });

  it('goes when the next tag has none, and is recoloured when the viewer levels', () => {
    const labels = new Labels(parent as unknown as HTMLElement);
    labels.placeSelection(10, 10, entityTag('Kira', 5, 5));
    expect(levelOf(selectionOf()).className).toBe('con-blue');
    labels.placeSelection(10, 10, entityTag('Kira', 5, 9));
    expect(levelOf(selectionOf()).className).toBe('con-green');
    labels.placeSelection(10, 10, entityTag('Citizen #4', 0, 9));
    expect(shownText(selectionOf())).toBe('Citizen #4');
    labels.placeSelection(10, 10, entityTag('Kira', 5, null));
    expect(shownText(selectionOf())).toBe('Kira (CL 5)');
    expect(levelOf(selectionOf()).className).toBe('');
  });
});

describe('the pile over a body', () => {
  it('stacks tag, then health bar, then speech, bottom up, each on the top of the last', () => {
    const stack = new Map<number, number>();
    const frameTop = 300;
    const tag = pileOn(stack, 5, frameTop, TAG_STACK_PX);
    const bar = pileOn(stack, 5, frameTop, BAR_STACK_PX);
    const speech = stack.get(5);
    expect(tag).toBe(frameTop - STACK_GAP_PX);
    expect(bar).toBe(tag - TAG_STACK_PX);
    expect(speech).toBe(bar - BAR_STACK_PX);
  });

  it('starts a body with no tag at its frame, and keeps each body its own pile', () => {
    const stack = new Map<number, number>();
    pileOn(stack, 5, 300, TAG_STACK_PX);
    expect(pileOn(stack, 6, 400, BAR_STACK_PX)).toBe(400 - STACK_GAP_PX);
    expect(stack.get(5)).toBe(300 - STACK_GAP_PX - TAG_STACK_PX);
  });
});

describe('a health bar', () => {
  beforeEach(() => {
    vi.stubGlobal('document', fakeDocument);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const bar = (over: Partial<HealthBar>): HealthBar => ({
    x: 0,
    y: 0,
    z: 0,
    fraction: 0.5,
    shielded: false,
    sx: Number.NaN,
    sy: Number.NaN,
    ...over,
  });

  it('goes where it was stacked on screen when given a place, unprojected', () => {
    const parent = fakeElement();
    const labels = new Labels(parent as unknown as HTMLElement);
    // A matrix that would put a projected bar nowhere near (all zeros: w = 0, behind the camera).
    labels.updateBars(new Array(16).fill(0), 0, 0, 800, 600, [bar({ sx: 120, sy: 80 })], 1);
    const all = parent.children[0].children;
    const shown = all[all.length - 1];
    expect(shown.style.display).toBe('block');
    expect(shown.style.transform).toBe('translate(120px, 80px)');
  });
});
