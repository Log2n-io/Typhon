import { create } from 'zustand';

/**
 * Whether this page's client is older than the one the server serves now (client.md C41): a redeploy replaces the build's hashed chunks, so a page
 * opened before it fails to fetch the next part it loads on demand — the glTF lights a room's lamps need were the first seen (2026-11-03: a room never
 * placed, the body in an empty box). The page cannot recover by itself; the player is asked to reload.
 */
export interface BuildStore {
  readonly stale: boolean;
  readonly markStale: () => void;
}

export const useBuild = create<BuildStore>((set) => ({
  stale: false,
  markStale: () => {
    set({ stale: true });
  },
}));

/** Whether an error is a chunk of this build that the server no longer has: the browsers' wordings of a failed dynamic import. */
export function isStaleChunkError(error: unknown): boolean {
  const cause = error instanceof Error && error.cause instanceof Error ? error.cause.message : '';
  const text = error instanceof Error ? `${error.message} ${cause}` : typeof error === 'string' ? error : '';
  return /Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module|Unable to preload CSS/i.test(
    text,
  );
}
