import { useMemo } from 'react';
import { useQueries } from '@tanstack/react-query';
import type { ComponentSchemaDto } from '@/api/generated/model';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeSchema, type ComponentSchema } from '@/hooks/schema/types';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/**
 * Fetches the layout schema for several component types at once (one query each, cached + deduped by TanStack Query), so the
 * Component Cards panel can join each decoded field value to its field name + indexed marker. Reuses the existing
 * `/schema/components/{typeName}` endpoint and `normalizeSchema`.
 */
export function useComponentSchemas(typeNames: string[]): Map<string, ComponentSchema> {
  const sessionId = useSessionStore((s) => s.sessionId);

  const results = useQueries({
    queries: typeNames.map((typeName) => ({
      queryKey: ['schema', 'component', sessionId, typeName],
      enabled: !!sessionId && !!typeName,
      staleTime: 30_000,
      queryFn: () =>
        customFetch<Envelope<ComponentSchemaDto>>(
          `/api/sessions/${sessionId}/schema/components/${typeName}`,
          { method: 'GET' },
        ),
    })),
  });

  return useMemo(() => {
    const map = new Map<string, ComponentSchema>();
    results.forEach((r, i) => {
      if (r.data?.data) {
        map.set(typeNames[i], normalizeSchema(r.data.data));
      }
    });
    return map;
    // results identity changes each render; key off the underlying data + names.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [JSON.stringify(typeNames), results.map((r) => r.dataUpdatedAt).join(',')]);
}
