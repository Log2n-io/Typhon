/**
 * Where a capture lives relative to its database (#616 D-1, consumed by #621).
 *
 * A capture is stored at `{name}.typhon/profilings/{timestamp}.typhon-trace`, so its database is derivable from its
 * path by construction — two levels up. That is the whole point of co-locating them: correlation is *structural*, not
 * inferred from a fingerprint or a sidecar index, and every inference step is a place to be subtly wrong later.
 *
 * Kept as pure string functions so they are testable without a filesystem and usable from the launch path, where there
 * is no session yet to ask.
 */

const PROFILINGS_DIR = 'profilings';

/** Splits a path on either separator, so a Windows path handed to a POSIX build (or vice versa) still resolves. */
function segments(path: string): string[] {
  return path.split(/[\\/]/).filter((s) => s.length > 0);
}

/** The separator this path already uses, so a derived path looks like the one it came from. */
function separatorOf(path: string): string {
  return path.includes('\\') ? '\\' : '/';
}

/** The capture's file name — what the attach API takes, since it resolves names against the session's own bundle. */
export function captureFileName(capturePath: string): string {
  const parts = segments(capturePath);
  return parts.length > 0 ? parts[parts.length - 1] : '';
}

/**
 * The database bundle directory a capture belongs to, or `null` when the path is not inside a `profilings/` directory.
 *
 * Null is a real answer, not a failure to try: a `.typhon-trace` sitting anywhere else — a replay saved from a live
 * attach, a file a user copied to their desktop — genuinely has no database to open, and guessing one would produce
 * exactly the confident-wrong-answer this design forbids.
 */
export function bundleOfCapture(capturePath: string): string | null {
  const parts = segments(capturePath);
  if (parts.length < 3) {
    return null;
  }
  // …/{name}.typhon/profilings/{file}
  if (parts[parts.length - 2].toLowerCase() !== PROFILINGS_DIR) {
    return null;
  }

  const sep = separatorOf(capturePath);
  const bundleParts = parts.slice(0, parts.length - 2);
  // A rooted POSIX path loses its leading separator to the filter above; put it back.
  const prefix = capturePath.startsWith('/') ? '/' : '';
  return prefix + bundleParts.join(sep);
}
