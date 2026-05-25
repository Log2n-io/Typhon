/**
 * Pulls a human-readable reason out of a failed-request error for the Connect dialog's error pill. `customFetch`
 * throws a `FetchError` carrying the parsed RFC 7807 ProblemDetails on `.problem` (and its `detail`/`title` in
 * `.message`), so we read that first; a plain `{ detail }` object and a bare `Error.message` are handled as fallbacks.
 * Without the `.problem` branch the pill showed only the static prefix (e.g. "Failed to open trace.") and dropped the
 * server's actionable reason — e.g. "Unsupported trace file version: 9 … Re-record against a current build."
 */
export function extractDetail(err: unknown): string {
  if (!err || typeof err !== 'object') {
    return '';
  }
  const problem = (err as { problem?: Record<string, unknown> }).problem;
  if (problem) {
    if (typeof problem.detail === 'string') return problem.detail;
    if (typeof problem.title === 'string') return problem.title;
  }
  const d = (err as Record<string, unknown>).detail;
  if (typeof d === 'string') return d;
  if (err instanceof Error && err.message) return err.message;
  return '';
}
