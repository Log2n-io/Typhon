import { useSessionStore } from '@/stores/useSessionStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/**
 * Error raised by {@link customFetch} for any non-2xx response. Carries the parsed RFC 7807
 * ProblemDetails body (or a `{ status }` synthetic when the body wasn't JSON) so callers and
 * the global query-cache logger can surface `title`/`detail` instead of `[object Object]`.
 */
export class FetchError extends Error {
  readonly status: number;
  readonly problem: Record<string, unknown>;

  constructor(status: number, problem: Record<string, unknown>) {
    const title = typeof problem.title === 'string' ? problem.title : undefined;
    const detail = typeof problem.detail === 'string' ? problem.detail : undefined;
    const message = detail ?? title ?? `HTTP ${status}`;
    super(message);
    this.name = 'FetchError';
    this.status = status;
    this.problem = problem;
  }
}

// Orval 8 calls the mutator as `customFetch<T>(url, init)` and expects the returned value to match
// the generated response envelope `{ data, status, headers }` — not the raw JSON body. This is the
// API contract shift from Orval 7. The generated client forwards queries inside the URL via its own
// URL builders (e.g. `getGetApiFsListUrl({path})`), so no params handling is needed here.
//
// 202 Accepted is treated as a "not ready yet" signal: the envelope is returned with `data: undefined`
// and the status preserved so call sites can opt into polling via `refetchInterval`. This mirrors the
// existing pattern in `useProfilerMetadata` for the trace-build-in-progress case (#332). Without this
// branch the Orval client would attempt `response.json()` on an empty 202 body and throw a SyntaxError.
export const customFetch = async <T>(url: string, init?: RequestInit): Promise<T> => {
  const token = useSessionStore.getState().token;

  const headers = new Headers(init?.headers);
  if (!headers.has('Content-Type') && init?.body != null) {
    headers.set('Content-Type', 'application/json');
  }
  // Session token + bootstrap token (#429) + api marker. Bootstrap token is present only under the `typhon ui`
  // host (captured from the launch-URL fragment); under the Vite dev-proxy it is null and the proxy injects the
  // header server-side instead. Shared with the hand-rolled profiler fetches via applyWorkbenchAuthHeaders.
  applyWorkbenchAuthHeaders(headers, token);

  const response = await fetch(url, { ...init, headers });

  if (!response.ok) {
    // Prefer RFC 7807 ProblemDetails (JSON); fall back to a bare status on non-JSON errors.
    const problem = (await response.json().catch(() => ({ status: response.status }))) as Record<string, unknown>;
    throw new FetchError(response.status, problem);
  }

  if (response.status === 204 || response.status === 202) {
    return { data: undefined, status: response.status, headers: response.headers } as T;
  }
  const data = await response.json();
  return { data, status: response.status, headers: response.headers } as T;
};
