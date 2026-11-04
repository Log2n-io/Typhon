import { create } from 'zustand';
import { CATEGORY_HISTORY, CHAT_CATEGORIES, type ChatCategory } from '../data/chat-tabs';

/**
 * What has been said within earshot (SWG-09), and what the client tells its own player — each line with its category (client.md C44).
 *
 * <b>Its own store, not the frame stats.</b> Chat arrives as events, at whatever rate the world talks, and the stats
 * snapshot is published on a timer — folding chat into it would drop whatever was said between two publishes. The
 * renderer pushes here as the events land and React reads it directly; there are a handful of lines a second at most,
 * so a re-render per line costs nothing.
 *
 * <b>A history per category.</b> One shared history of 40 lines let the speech of a busy town push a drop out before it was read (Loïc, 2026-11-04): each
 * category keeps its own {@link CATEGORY_HISTORY}, and a tab merges the ones it shows by {@link ChatLine.seq}.
 */

export interface ChatLine {
  /** The server tick the speech happened on. */
  readonly tick: number;
  /** The speaker, as this session knows it; 0 for a speaker it was never shown. */
  readonly speaker: number;
  readonly text: string;
  /** Local time it arrived, for fading it out. */
  readonly atMs: number;
  /** Said by this session's own avatar. */
  readonly own?: boolean;
  /** Not speech: what the client has to tell its own player — a command's answer, a tip given or received. Nobody else sees it. */
  readonly system?: boolean;
  /** A /tell: the other side's name, and whether this player sent it. */
  readonly tell?: { readonly other: string; readonly outgoing: boolean };
  /** What it is about; speech is `local`. */
  readonly category: ChatCategory;
  /** Its order across every category: what a tab merges by. */
  readonly seq: number;
}

/** A line as a caller hands it over: the store gives it its order, and speech its category. */
export type ChatLineIn = Omit<ChatLine, 'seq' | 'category'> & { readonly category?: ChatCategory };

/** How many lines the all-categories view keeps. */
export const CHAT_HISTORY = CATEGORY_HISTORY * 2;

type Histories = Readonly<Record<ChatCategory, readonly ChatLine[]>>;

const EMPTY: Histories = Object.fromEntries(CHAT_CATEGORIES.map((c) => [c, []])) as unknown as Histories;

interface ChatState {
  /** Every line, newest last, at most {@link CHAT_HISTORY}. */
  readonly lines: readonly ChatLine[];
  /** Each category's lines, newest last, at most {@link CATEGORY_HISTORY} each. */
  readonly histories: Histories;
  /** The last line's {@link ChatLine.seq}; 0 before any. */
  readonly seq: number;
  /** Per tab id, the last seq it showed while active: what its unread count starts after. */
  readonly seen: Readonly<Record<string, number>>;
  readonly heard: number;
  readonly say: (line: ChatLineIn) => void;
  readonly clear: () => void;
  /**
   * Sends a line as this session's avatar (`DataSource.say`), already composed by `composeSay`. Installed by the app,
   * which owns the source; the default speaks for nobody.
   *
   * @returns Whether it went: `false` when the session cannot speak or the one-a-second rate is spent.
   */
  readonly speak: (text: string) => boolean;
  readonly setSpeak: (speak: (text: string) => boolean) => void;
  /** Adds a line of the client's own (`system`): a command's answer, a drop, a tip given or received — under its category, `system` by default. */
  readonly notice: (text: string, category?: ChatCategory) => void;
  /** A tab was shown: everything up to now is seen in it. */
  readonly markSeen: (tab: string) => void;
  /**
   * The last ask to open the box with a line already in it — the context menu's "Tip…" — or `null`. `seq` moves with each, so
   * the same text asked twice opens it twice.
   */
  readonly composeRequest: { readonly text: string; readonly seq: number } | null;
  /** Opens the box with `text` in it, the cursor after it. */
  readonly compose: (text: string) => void;
}

function add(s: ChatState, line: ChatLineIn, category: ChatCategory): Pick<ChatState, 'lines' | 'histories' | 'seq'> {
  const seq = s.seq + 1;
  const full: ChatLine = { ...line, category, seq };
  const history = s.histories[category];
  return {
    seq,
    lines: s.lines.length < CHAT_HISTORY ? [...s.lines, full] : [...s.lines.slice(1), full],
    histories: {
      ...s.histories,
      [category]: history.length < CATEGORY_HISTORY ? [...history, full] : [...history.slice(1), full],
    },
  };
}

export const useChat = create<ChatState>()((set) => ({
  lines: [],
  histories: EMPTY,
  seq: 0,
  seen: {},
  heard: 0,
  composeRequest: null,
  speak: () => false,
  setSpeak: (speak) => {
    set({ speak });
  },
  say: (line) => {
    set((s) => ({
      ...add(s, line, line.category ?? (line.tell === undefined ? 'local' : 'tells')),
      heard: s.heard + (line.tell === undefined ? 1 : 0),
    }));
  },
  clear: () => {
    set({ lines: [], histories: EMPTY, heard: 0, seen: {} });
  },
  notice: (text, category = 'system') => {
    set((s) => add(s, { tick: 0, speaker: 0, text, atMs: Date.now(), system: true }, category));
  },
  markSeen: (tab) => {
    set((s) => (s.seen[tab] === s.seq ? s : { seen: { ...s.seen, [tab]: s.seq } }));
  },
  compose: (text) => {
    set((s) => ({ composeRequest: { text, seq: (s.composeRequest?.seq ?? 0) + 1 } }));
  },
}));
