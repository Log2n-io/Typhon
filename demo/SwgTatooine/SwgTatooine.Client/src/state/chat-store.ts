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
}

/** How many lines are kept. Older ones fall off the top. */
export const CHAT_HISTORY = 40;

interface ChatState {
  readonly lines: readonly ChatLine[];
  readonly heard: number;
  readonly say: (line: ChatLine) => void;
  readonly clear: () => void;
}

export const useChat = create<ChatState>()((set) => ({
  lines: [],
  heard: 0,
  say: (line) => {
    set((s) => ({
      lines: s.lines.length < CHAT_HISTORY ? [...s.lines, line] : [...s.lines.slice(1), line],
      heard: s.heard + 1,
    }));
  },
  clear: () => {
    set({ lines: [], heard: 0 });
  },
}));
