// Shared raw-fetch + base64 SoA decode helpers for the Database File Map hooks (Module 15). The hooks fetch
// raw (the useTrack.ts pattern) rather than via Orval — the map is a small, stable shape.

/** Fetches a JSON endpoint with the Workbench session-token header, throwing a useful message on failure. */
export async function fetchJson<T>(url: string, token: string | null, signal: AbortSignal): Promise<T> {
  const headers = new Headers();
  if (token) {
    headers.set('X-Session-Token', token);
  }
  headers.set('X-Workbench-Api', '1');
  const res = await fetch(url, { signal, headers });
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const problem = (await res.json()) as { detail?: string; title?: string };
      detail = problem?.detail ?? problem?.title ?? detail;
    } catch {
      // Non-JSON body — keep the status-text fallback.
    }
    throw new Error(detail);
  }
  return (await res.json()) as T;
}

/** Decodes a base64 string into raw bytes. */
export function decodeBase64(b64: string): Uint8Array {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/** Decodes a base64 SoA buffer into an `Int32Array` (little-endian, as the server writes it). */
export function decodeInt32(b64: string): Int32Array {
  const bytes = decodeBase64(b64);
  return new Int32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >> 2);
}

/** Decodes a base64 SoA buffer into a `Uint16Array`. */
export function decodeUint16(b64: string): Uint16Array {
  const bytes = decodeBase64(b64);
  return new Uint16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >> 1);
}
