/**
 * Which of the HUD's sections are open (Loïc, 2026-11-02): Client, Server and This session, each folded by its title, all closed by default.
 *
 * Kept in `localStorage` — a display preference of this browser, nothing a script could misuse, so the reasons `data/auth.ts` keeps the token in
 * `sessionStorage` do not apply — as the open sections' names, comma-separated. Storage can be missing or refuse (a private window, site data
 * blocked): then nothing is kept and every section starts closed.
 */

/** A HUD section, by the name it is stored under. */
export type HudSection = 'client' | 'server' | 'session';

/** Every section, in the HUD's order. */
export const HUD_SECTIONS: readonly HudSection[] = ['client', 'server', 'session'];

/** The storage key. */
export const HUD_OPEN_KEY = 'swg.hud.open';

/** The open sections a stored value names; anything that is not a section is ignored. */
export function parseOpen(raw: string | null): Set<HudSection> {
  const open = new Set<HudSection>();
  for (const name of (raw ?? '').split(',')) {
    const section = HUD_SECTIONS.find((s) => s === name.trim());
    if (section !== undefined) {
      open.add(section);
    }
  }

  return open;
}

/** The value stored for the open sections, in the HUD's order. */
export function serializeOpen(open: ReadonlySet<HudSection>): string {
  return HUD_SECTIONS.filter((s) => open.has(s)).join(',');
}

/** The sections this browser left open; none when storage cannot be read. */
export function loadOpen(storage: Pick<Storage, 'getItem'> | null = localStorageOrNull()): Set<HudSection> {
  try {
    return parseOpen(storage?.getItem(HUD_OPEN_KEY) ?? null);
  } catch {
    return new Set();
  }
}

/** Keeps the open sections for the next load; nothing when storage refuses. */
export function saveOpen(
  open: ReadonlySet<HudSection>,
  storage: Pick<Storage, 'setItem'> | null = localStorageOrNull(),
): void {
  try {
    storage?.setItem(HUD_OPEN_KEY, serializeOpen(open));
  } catch {
    // Not kept: the HUD works the same, closed on the next load.
  }
}

// Reading `localStorage` itself throws where site data is blocked.
function localStorageOrNull(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}
