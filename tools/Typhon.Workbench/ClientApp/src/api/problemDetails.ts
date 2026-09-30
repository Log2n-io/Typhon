/**
 * Reading the server's RFC 7807 body off a rejected request — in one place, because getting it wrong is silent.
 *
 * **`FetchError` carries the parsed body on `problem`. It is NOT on `data`.** `data` is Orval's *success* envelope
 * (`{ data, status, headers }`) and is always `undefined` on an error. Two call sites read `.data.title` anyway and
 * both failed quietly rather than loudly:
 *
 *  - the Query Console's run handler fell back to `String(err)`, so a deliberate, actionable sentence from the server
 *    arrived under the heading `error` as `FetchError: This database holds several realms…` — a designed answer
 *    rendered as a crash;
 *  - the cost-preview hook's "ignore mid-typing syntax errors" filter never matched, so every keystroke that did not
 *    yet parse logged a server error into the Logs panel.
 *
 * Neither failed a test, because the wrong lookup yields `undefined` and both had a fallback. Hence one module, and
 * tests over it.
 */

/** The ProblemDetails body of a failed request, or `null` when the error did not carry one. */
export function problemOf(err: unknown): { title?: string; detail?: string; status?: number } | null {
  const e = err as { problem?: Record<string, unknown> } | null;
  const problem = e?.problem;
  if (problem == null || typeof problem !== 'object') {
    return null;
  }

  return {
    title: typeof problem.title === 'string' ? problem.title : undefined,
    detail: typeof problem.detail === 'string' ? problem.detail : undefined,
    status: typeof problem.status === 'number' ? problem.status : undefined,
  };
}

/** The server's stable error code, or `null`. Worth showing verbatim: it is what a user pastes into a search. */
export function problemCode(err: unknown): string | null {
  return problemOf(err)?.title ?? null;
}

/**
 * The most useful sentence available for a failed request.
 *
 * Prefers the server's `detail` — the sentence written to be read — over the `Error.message`, and uses the bare
 * message rather than `String(err)` so a fallback never renders the class name (`FetchError: …`) to a user.
 */
export function problemMessage(err: unknown): string {
  const detail = problemOf(err)?.detail;
  if (detail) {
    return detail;
  }

  return err instanceof Error ? err.message : String(err);
}

/** Whether a rejection is the server refusing with this exact code. */
export function isProblemCode(err: unknown, code: string): boolean {
  return problemCode(err) === code;
}
