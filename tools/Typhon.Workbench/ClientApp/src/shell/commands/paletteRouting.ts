/**
 * Command-palette prefix routing (IA §4, command-palette.md). One palette, prefix-scoped:
 *   ''  → commands (recent-first), '>' → run action, '@' → go to object in current session,
 *   '#' → go to object workspace-wide, ':' → jump to time/page, '?' → prefix help.
 *
 * Pure functions so the routing + jump parsing are unit-tested (conformance suite C) without rendering cmdk.
 */
export type PaletteMode = 'command' | 'action' | 'object-session' | 'object-global' | 'jump' | 'help';

export interface PaletteRoute {
  readonly mode: PaletteMode;
  /** The query with its prefix stripped + trimmed. */
  readonly query: string;
}

export function parsePaletteMode(value: string): PaletteRoute {
  if (value.startsWith('>')) return { mode: 'action', query: value.slice(1).trim() };
  if (value.startsWith('@')) return { mode: 'object-session', query: value.slice(1).trim() };
  if (value.startsWith('#')) return { mode: 'object-global', query: value.slice(1).trim() };
  if (value.startsWith(':')) return { mode: 'jump', query: value.slice(1).trim() };
  if (value.startsWith('?')) return { mode: 'help', query: value.slice(1).trim() };
  return { mode: 'command', query: value.trim() };
}

export const PALETTE_PREFIX_HELP: ReadonlyArray<{ prefix: string; meaning: string }> = [
  { prefix: '(type)', meaning: 'Run a command' },
  { prefix: '>', meaning: 'Run a command (explicit)' },
  { prefix: '@', meaning: 'Go to an object in this session' },
  { prefix: '#', meaning: 'Go to an object workspace-wide' },
  { prefix: ':', meaning: 'Jump to a tick or page (e.g. ":tick 8412", ":page 1024")' },
  { prefix: '?', meaning: 'Show this help' },
];

export type JumpTarget =
  | { kind: 'tick'; value: number }
  | { kind: 'page'; value: number }
  | null;

/** Parse a `:` jump query — `tick N` (trace/attach) or `page N` (open). Returns null when unrecognised. */
export function parseJump(query: string): JumpTarget {
  const m = query.match(/^(tick|page)\s+(\d+)$/i);
  if (!m) return null;
  const kind = m[1].toLowerCase() as 'tick' | 'page';
  return { kind, value: Number(m[2]) };
}
