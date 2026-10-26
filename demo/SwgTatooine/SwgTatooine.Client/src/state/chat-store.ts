import { create } from 'zustand';

/**
 * What has been said within earshot (SWG-09).
 *
 * <b>Its own store, not the frame stats.</b> Chat arrives as events, at whatever rate the world talks, and the stats
 * snapshot is published on a timer — folding chat into it would drop whatever was said between two publishes. The
 * renderer pushes here as the events land and React reads it directly; there are a handful of lines a second at most,
 * so a re-render per line costs nothing.
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
}

/** How many lines are kept. Older ones fall off the top. */
export const CHAT_HISTORY = 40;

interface ChatState {
  readonly lines: readonly ChatLine[];
  readonly heard: number;
  readonly say: (line: ChatLine) => void;
  readonly clear: () => void;
  /**
   * Sends a line as this session's avatar (`DataSource.say`), already composed by `composeSay`. Installed by the app,
   * which owns the source; the default speaks for nobody.
   *
   * @returns Whether it went: `false` when the session cannot speak or the one-a-second rate is spent.
   */
  readonly speak: (text: string) => boolean;
  readonly setSpeak: (speak: (text: string) => boolean) => void;
  /** Adds a line of the client's own (`system`): a command's answer, a tip given or received. */
  readonly notice: (text: string) => void;
  /**
   * The last ask to open the box with a line already in it — the context menu's "Tip…" — or `null`. `seq` moves with each, so
   * the same text asked twice opens it twice.
   */
  readonly composeRequest: { readonly text: string; readonly seq: number } | null;
  /** Opens the box with `text` in it, the cursor after it. */
  readonly compose: (text: string) => void;
}

export const useChat = create<ChatState>()((set) => ({
  lines: [],
  heard: 0,
  composeRequest: null,
  speak: () => false,
  setSpeak: (speak) => {
    set({ speak });
  },
  say: (line) => {
    set((s) => ({
      lines: s.lines.length < CHAT_HISTORY ? [...s.lines, line] : [...s.lines.slice(1), line],
      heard: s.heard + 1,
    }));
  },
  clear: () => {
    set({ lines: [], heard: 0 });
  },
  notice: (text) => {
    const line: ChatLine = { tick: 0, speaker: 0, text, atMs: Date.now(), system: true };
    set((s) => ({ lines: s.lines.length < CHAT_HISTORY ? [...s.lines, line] : [...s.lines.slice(1), line] }));
  },
  compose: (text) => {
    set((s) => ({ composeRequest: { text, seq: (s.composeRequest?.seq ?? 0) + 1 } }));
  },
}));
