import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSourceLocationStore } from '@/stores/useSourceLocationStore';

interface SourceLocationManifestDto {
  files: Array<{ fileId: number; path: string }>;
  entries: Array<{ id: number; fileId: number; line: number; kind: number; method: string }>;
}

/**
 * Fetch the compile-time source-location manifest for a profiler session and hydrate it into
 * {@link useSourceLocationStore}. The manifest enables the Source row in `ProfilerDetail` to
 * resolve span `siteId`s into `file:line · method` and offer the "Open in editor" handoff
 * (issue #293, Phase 3 / 4b).
 *
 * The endpoint returns an empty manifest for trace-mode sessions today (the trace file's trailer
 * is the authoritative source for that path; Workbench wiring deferred). Live-attach sessions
 * return whatever the engine sent in its init handshake.
 */
export function useProfilerSourceLocations(sessionId: string | null): void {
  const token = useSessionStore((s) => s.token);
  const setManifest = useSourceLocationStore((s) => s.setManifest);

  const query = useQuery<SourceLocationManifestDto | null, Error>({
    queryKey: ['profiler', 'source-locations', sessionId],
    enabled: !!sessionId,
    retry: false,
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = new Headers();
      if (token) headers.set('X-Session-Token', token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/source-locations`, {
        signal,
        headers,
      });
      if (!res.ok) return null;
      return (await res.json()) as SourceLocationManifestDto;
    },
  });

  useEffect(() => {
    if (!query.data) return;
    const entries = query.data.entries.map((e) => ({
      id: e.id,
      fileId: e.fileId,
      line: e.line,
      kind: e.kind,
      method: e.method,
    }));
    setManifest(entries, query.data.files);
  }, [query.data, setManifest]);
}
