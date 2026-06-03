// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useOptionsStore } from '@/stores/useOptionsStore';

/**
 * Covers `setSchema` (ADR-055 Phase 2): optimistic apply, adoption of the server-normalized list on
 * success, and rollback on HTTP error. The store talks to `/api/options/schema` via the global `fetch`.
 */
describe('useOptionsStore.setSchema', () => {
  const original = useOptionsStore.getState().options;

  beforeEach(() => {
    useOptionsStore.setState({ options: original });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('adopts the server-normalized directory list on success', async () => {
    const normalized = { editor: original.editor, profiler: original.profiler, schema: { directories: ['C:\\Norm\\bin'] } };
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(normalized), { status: 200 })));

    await useOptionsStore.getState().setSchema({ directories: ['c:/norm/bin'] });

    expect(useOptionsStore.getState().options.schema.directories).toEqual(['C:\\Norm\\bin']);
    expect(fetch).toHaveBeenCalledWith('/api/options/schema', expect.objectContaining({ method: 'PATCH' }));
  });

  it('rolls back to the previous options on a non-OK response', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('nope', { status: 500 })));

    await expect(useOptionsStore.getState().setSchema({ directories: ['C:\\x'] })).rejects.toThrow();
    expect(useOptionsStore.getState().options.schema.directories).toEqual(original.schema.directories);
  });
});
