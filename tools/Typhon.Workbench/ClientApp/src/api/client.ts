import { useSessionStore } from '@/stores/useSessionStore';

// Orval 8 calls the mutator as `customFetch<T>(url, init)` and expects the returned value to match
// the generated response envelope `{ data, status, headers }` — not the raw JSON body. This is the
// API contract shift from Orval 7. The generated client forwards queries inside the URL via its own
// URL builders (e.g. `getGetApiFsListUrl({path})`), so no params handling is needed here.
export const customFetch = async <T>(url: string, init?: RequestInit): Promise<T> => {
  const token = useSessionStore.getState().token;

  const headers = new Headers(init?.headers);
  if (!headers.has('Content-Type') && init?.body != null) {
    headers.set('Content-Type', 'application/json');
  }
  if (token && !headers.has('X-Session-Token')) {
    headers.set('X-Session-Token', token);
  }
  headers.set('X-Workbench-Api', '1');

  const response = await fetch(url, { ...init, headers });

  if (!response.ok) {
    // Prefer RFC 7807 ProblemDetails (JSON); fall back to a bare status on non-JSON errors.
    const error = (await response.json().catch(() => ({ status: response.status }))) as Record<string, unknown>;
    throw error;
  }

  const data = response.status === 204 ? undefined : await response.json();
  return { data, status: response.status, headers: response.headers } as T;
};
