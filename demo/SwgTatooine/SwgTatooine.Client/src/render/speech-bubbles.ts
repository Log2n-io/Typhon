/**
 * What is said, drawn where it is said: a white bubble over the speaker's head, for every `Chat` this session hears
 * (Loïc, 2026-10-25: "the text should be displayed in a white bubble above my character's head and other player should
 * be able to see it").
 *
 * <b>From the server's `Chat`, the speaker's own line included.</b> A session hears its own speech — the server routes
 * it to everyone within 50 m of the speaker, and the speaker is 0 m from itself — so the bubble over one's own head
 * comes from the same event as everyone else's, and shows what the server actually accepted. A local echo would show a
 * line the rate limit then dropped.
 *
 * <b>DOM over the canvas, like the labels</b>: a handful of bubbles at most, and the browser's own text layout wraps
 * them, which is the part a canvas would have to reimplement. A bubble's style is written only when it moves by a pixel,
 * appears, hides or starts to fade.
 */

/** The shortest time a bubble stays up, milliseconds: long enough to read "Hi". */
export const BUBBLE_MIN_MS = 4000;

/** The longest, however much was said. */
export const BUBBLE_MAX_MS = 12000;

/** How much longer each character keeps it up: about a slow reader's pace. */
export const BUBBLE_MS_PER_CHAR = 70;

/** How long it takes to fade out at the end of its life. */
export const BUBBLE_FADE_MS = 400;

/** Past this many metres from the camera a bubble is not drawn. Speech is heard within 50 m; this is that, and some. */
export const BUBBLE_RANGE_M = 70;

/** How long `text` is shown for. */
export function bubbleLifetimeMs(text: string): number {
  return Math.min(BUBBLE_MAX_MS, BUBBLE_MIN_MS + text.length * BUBBLE_MS_PER_CHAR);
}

/** One speaker's current line. */
export interface Speech {
  readonly netId: number;
  readonly text: string;
  readonly startMs: number;
  readonly endMs: number;
}

/**
 * The lines on screen: one per speaker — a new line replaces the speaker's previous one, as it would in a conversation —
 * expiring on its own clock. Pure, so it is what the tests read.
 */
export class SpeechLog {
  private readonly bySpeaker = new Map<number, Speech>();

  /** The live lines, in no particular order. */
  get active(): IterableIterator<Speech> {
    return this.bySpeaker.values();
  }

  get size(): number {
    return this.bySpeaker.size;
  }

  /** Whether `netId` has a line up. */
  has(netId: number): boolean {
    return this.bySpeaker.has(netId);
  }

  /** A line heard now. A speaker of 0 — one this session was never shown — has no head to put it over. */
  say(netId: number, text: string, nowMs: number): void {
    if (netId === 0 || text.length === 0) {
      return;
    }

    this.bySpeaker.set(netId, { netId, text, startMs: nowMs, endMs: nowMs + bubbleLifetimeMs(text) });
  }

  /** Drops every line whose time is up. */
  expire(nowMs: number): void {
    for (const [netId, speech] of this.bySpeaker) {
      if (nowMs >= speech.endMs) {
        this.bySpeaker.delete(netId);
      }
    }
  }

  /** How opaque a line is now: 1, then down to 0 over its last {@link BUBBLE_FADE_MS}. */
  static opacityAt(speech: Speech, nowMs: number): number {
    return Math.max(0, Math.min(1, (speech.endMs - nowMs) / BUBBLE_FADE_MS));
  }

  /** Forgets everything: a `RESET` renumbers the world, and a netId kept across it is another body. */
  clear(): void {
    this.bySpeaker.clear();
  }
}

/** Where a bubble stands on screen: the top centre of what its speaker was DRAWN as, CSS pixels, and how deep it is. */
export interface BubbleAnchor {
  x: number;
  y: number;
  /** View depth, metres: what {@link BUBBLE_RANGE_M} is measured against. */
  depth: number;
}

/**
 * Where a speaker stands on screen this frame; false when it was not drawn.
 *
 * <b>The drawn body's top, never a height above its feet.</b> A head computed as the feet plus a character's height put
 * the bubble most of a metre over every head, for the reason the selection tag once floated: a constant height is right
 * for no outfit, and the server's altitude is not where the mesh stands.
 */
export type AnchorOf = (netId: number, out: BubbleAnchor) => boolean;

interface Bubble {
  readonly element: HTMLDivElement;
  text: string;
  shown: boolean;
  px: number;
  py: number;
  /** The opacity last written, in hundredths. */
  alpha: number;
}

/** The bubbles' DOM: one element per speaker on screen, made on first use and kept for the next line. */
export class SpeechBubbles {
  readonly log = new SpeechLog();
  private readonly root: HTMLDivElement;
  private readonly bubbles = new Map<number, Bubble>();
  private readonly anchor: BubbleAnchor = { x: 0, y: 0, depth: 0 };

  constructor(parent: HTMLElement) {
    this.root = document.createElement('div');
    this.root.className = 'bubbles';
    parent.appendChild(this.root);
  }

  /** A line heard: see {@link SpeechLog.say}. */
  say(netId: number, text: string, nowMs: number): void {
    this.log.say(netId, text, nowMs);
  }

  /** Places every live bubble over its speaker, in a CSS viewport. */
  update(width: number, height: number, nowMs: number, anchorOf: AnchorOf): void {
    this.log.expire(nowMs);
    for (const [netId, bubble] of this.bubbles) {
      if (!this.log.has(netId)) {
        bubble.element.remove();
        this.bubbles.delete(netId);
      }
    }

    for (const speech of this.log.active) {
      let bubble = this.bubbles.get(speech.netId);
      if (bubble === undefined) {
        const element = document.createElement('div');
        element.className = 'bubble';
        element.style.display = 'none';
        this.root.appendChild(element);
        bubble = { element, text: '', shown: false, px: Number.NaN, py: Number.NaN, alpha: -1 };
        this.bubbles.set(speech.netId, bubble);
      }

      if (bubble.text !== speech.text) {
        bubble.text = speech.text;
        bubble.element.textContent = speech.text;
      }

      const a = this.anchor;
      // Not drawn, too far to read, or well off screen: hidden rather than placed for nothing.
      const shown =
        anchorOf(speech.netId, a) &&
        a.depth > 0 &&
        a.depth < BUBBLE_RANGE_M &&
        a.x > -200 &&
        a.x < width + 200 &&
        a.y > -100 &&
        a.y < height + 300;
      if (shown) {
        this.place(bubble, a.x, a.y, SpeechLog.opacityAt(speech, nowMs));
      }

      if (shown !== bubble.shown) {
        bubble.shown = shown;
        bubble.element.style.display = shown ? 'block' : 'none';
      }
    }
  }

  /** Forgets every line and its bubble. */
  clear(): void {
    this.log.clear();
    for (const bubble of this.bubbles.values()) {
      bubble.element.remove();
    }

    this.bubbles.clear();
  }

  dispose(): void {
    this.root.remove();
  }

  private place(bubble: Bubble, x: number, y: number, opacity: number): void {
    const px = Math.round(x);
    const py = Math.round(y);
    if (px !== bubble.px || py !== bubble.py) {
      bubble.px = px;
      bubble.py = py;
      bubble.element.style.transform = `translate(${String(px)}px, ${String(py)}px)`;
    }

    const alpha = Math.round(opacity * 100);
    if (alpha !== bubble.alpha) {
      bubble.alpha = alpha;
      bubble.element.style.opacity = String(alpha / 100);
    }
  }
}
