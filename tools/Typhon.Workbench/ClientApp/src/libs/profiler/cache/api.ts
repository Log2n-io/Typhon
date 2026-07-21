/**
 * Workbench-specific profiler HTTP helpers — session-scoped binary chunk fetcher used by {@link chunkCache}.
 *
 * Shape mirrors the old standalone viewer's `api.ts` so the cache module ports with minimal edits. Only the
 * URL and auth differ:
 *  - Session-scoped URL: `/api/sessions/{sessionId}/profiler/chunks/{chunkIdx}` (vs. old path-query-param shape).
 *  - X-Session-Token + X-Workbench-Token attached via applyWorkbenchAuthHeaders (api/bootstrapToken).
 *    Under the Vite dev-proxy the bootstrap token is injected server-side instead; under `typhon ui` the
 *    helper supplies it from sessionStorage (no proxy exists there).
 *
 * JSON-chunk path (the old `/api/trace/chunk` endpoint) is NOT ported — the Workbench is binary-only. The
 * {@link fetchChunk} stub throws loudly so a stale JSON code path in chunkCache doesn't silently fall back.
 */
import type { ChunkResponse } from '../model/types';
import { useSessionStore } from '@/stores/useSessionStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/** Binary-chunk response — raw LZ4 compressed payload + chunk metadata from response headers. */
export interface BinaryChunkResponse {
  fromTick: number;
  toTick: number;
  eventCount: number;
  uncompressedBytes: number;
  isContinuation: boolean;
  timestampFrequency: number;
  compressed: Uint8Array;
}

/**
 * Fetch one chunk's raw LZ4 bytes from the session-scoped profiler endpoint. Caller is responsible for
 * LZ4-decompressing + record-decoding in the Web Worker (see `chunkDecoder` / `chunkWorker`).
 *
 * @param sessionId — the Trace session Guid. Replaces the old API's `path` parameter.
 */
export async function fetchChunkBinary(
  sessionId: string,
  chunkIdx: number,
  signal?: AbortSignal,
): Promise<BinaryChunkResponse> {
  const url = `/api/sessions/${sessionId}/profiler/chunks/${chunkIdx}`;
  const token = useSessionStore.getState().token;
  const headers = applyWorkbenchAuthHeaders(new Headers(), token);

  const res = await fetch(url, { signal, headers });
  if (!res.ok) {
    throw new Error(`Failed to load binary chunk #${chunkIdx}: ${res.status} ${res.statusText}`);
  }

  const headerInt = (name: string): number => {
    const raw = res.headers.get(name);
    if (raw === null) throw new Error(`Binary chunk response missing required header: ${name}`);
    const n = Number(raw);
    if (!Number.isFinite(n)) throw new Error(`Binary chunk response header ${name} not a number: ${raw}`);
    return n;
  };

  const fromTick = headerInt('X-Chunk-From-Tick');
  const toTick = headerInt('X-Chunk-To-Tick');
  const eventCount = headerInt('X-Chunk-Event-Count');
  const uncompressedBytes = headerInt('X-Chunk-Uncompressed-Bytes');
  const timestampFrequency = headerInt('X-Timestamp-Frequency');
  const isContinuation = res.headers.get('X-Chunk-Is-Continuation') === '1';
  const buffer = await res.arrayBuffer();

  return {
    fromTick,
    toTick,
    eventCount,
    uncompressedBytes,
    isContinuation,
    timestampFrequency,
    compressed: new Uint8Array(buffer),
  };
}

/**
 * JSON-chunk fetch — NOT SUPPORTED in the Workbench. The old standalone viewer had a legacy JSON path
 * (`/api/trace/chunk`) for fallback; the Workbench ships binary-only. If the cache's legacy branch is ever
 * exercised, this throws loudly — that'd indicate the cache module's `useBinary` flag was accidentally
 * disabled.
 */
export function fetchChunk(_sessionId: string, _chunkIdx: number, _signal?: AbortSignal): Promise<ChunkResponse> {
  throw new Error(
    'JSON chunk path is not supported in the Workbench — only binary chunks (fetchChunkBinary) are served.',
  );
}
