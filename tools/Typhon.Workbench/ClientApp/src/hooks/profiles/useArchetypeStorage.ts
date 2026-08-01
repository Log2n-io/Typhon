import { useMemo } from 'react';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useDbMapHealth } from '@/hooks/dbmap/useDbMapHealth';
import { useSessionCapability, useSessionStore } from '@/stores/useSessionStore';
import { rollUpArchetypeStorage, UNRESOLVED_ARCHETYPE_STORAGE, type ArchetypeStorage } from '@/libs/schema/archetypeStorage';

/**
 * What the **database** says about the storage of an archetype a capture recorded a system touching
 * (#619, design §4.2 — the physical lens).
 *
 * The capture knows a system spent 400 µs on `Unit` at tick 800, and nothing about where `Unit` lives. The database
 * knows `Unit` occupies three segments over 1,204 pages at 62 % chunk fill, and nothing about that tick. Together
 * they are the design's sentence; apart, neither half is a diagnosis.
 *
 * ⚠️ **Present tense, and the caller must say so.** The layout returned here is *today's*. Cluster migration,
 * checkpointing and compaction all move pages, and §5.2 marks every trace-side storage address volatile by design.
 * The honest claim is *"this archetype is fragmented **now**"* — never *"fragmentation caused that spike"*. Chronic
 * fragmentation alongside chronic slowness is a real signal, but it is **correlational**, and §4.2 requires the UI
 * to carry that caveat rather than let the adjacency imply causation.
 *
 * Residency and CRC are deliberately **not** rolled up here. Both are per-page, and computing CRC over an
 * archetype's page range would read every page — faulting the non-resident ones in and corrupting the residency
 * figure printed beside it, while breaking the File Map's "never a full-file scan" rule. The map already encodes
 * both per page, and the reveal is what lands the user there.
 */
export function useArchetypeStorage(archetypeName: string | null): ArchetypeStorage {
  // A standalone trace session has no database to ask. Passing a null session id keeps `useDbMapHealth` disabled, so
  // the Data Flow panel in a plain trace session issues no request and renders exactly as it did before this bridge.
  const hasDatabase = useSessionCapability('database');
  const sessionId = useSessionStore((s) => s.sessionId);

  const { data: health } = useDbMapHealth(hasDatabase ? sessionId : null);
  const { list: archetypes } = useArchetypeList();
  const { list: components } = useComponentList();

  // Join on the name, never on an id — see archetypeStorage.ts and design §5.2/§5.3. The archetype id in a capture
  // is a per-process catalog id; the one in the database is a persisted routing id; they are different numbers for
  // the same archetype, and comparing them is the failure mode this epic exists to prevent.
  const archetype = useMemo(
    () => (archetypeName ? archetypes.find((a) => a.name === archetypeName) ?? null : null),
    [archetypeName, archetypes],
  );

  return useMemo(() => {
    if (!hasDatabase || !archetype || !health) {
      return UNRESOLVED_ARCHETYPE_STORAGE;
    }
    // Derive the page size from the file rather than assuming 4 KiB: the value is right there, and an assumed
    // constant is the kind of thing that survives a format change and starts reporting confident wrong byte counts.
    const pageSize = health.dataFilePageCount > 0 ? health.dataFileBytes / health.dataFilePageCount : 0;
    return rollUpArchetypeStorage(archetype, components, health.segments, pageSize);
  }, [hasDatabase, archetype, components, health]);
}
