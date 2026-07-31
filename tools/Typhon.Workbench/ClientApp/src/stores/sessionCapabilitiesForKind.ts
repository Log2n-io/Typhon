import type { SessionCapability, SessionKind } from './useSessionStore';

/**
 * The capabilities the server reports for a plain session of each kind.
 *
 * The server is the authority — this exists so **tests** can seed a realistic store without hand-listing capabilities at
 * every call site, and so the kind→capability mapping is written down once rather than assumed in a dozen fixtures.
 *
 * Note the deliberate gap it cannot express: an open database *with a capture attached* also has `profiler`, which is
 * the entire point of #617 and precisely why production code must read `capabilities` from the session rather than
 * deriving them from `kind` through a table like this one.
 */
export function sessionCapabilitiesForKind(kind: SessionKind): SessionCapability[] {
  switch (kind) {
    case 'open':
      return ['database'];
    case 'trace':
    case 'attach':
      return ['profiler'];
    default:
      return [];
  }
}
