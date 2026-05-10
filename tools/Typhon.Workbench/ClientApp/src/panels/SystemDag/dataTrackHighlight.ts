import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { DataTrackSelection } from '@/stores/useSelectionStore';

/**
 * Resolve the set of system names that touch a given Data Flow track. Drives the SystemDag highlight when
 * `useSelectionStore.dataTrack` is set: every system whose declared access intersects the track's data scope
 * gets a halo. Pure, allocation-bounded, called on every selection change.
 *
 * Resolution rules (mirror `barBuilding.ts` fan-out logic so the highlight matches what the Timeline shows):
 * - <b>component / archetype-component</b> — systems whose `reads/writes/...` arrays contain the component name
 *   (or, for archetype-component, the system additionally needs to be one that touches the archetype — but
 *   that's a runtime fact not encoded in the topology, so we conservatively highlight every system reading
 *   the component).
 * - <b>component-family</b> — systems touching any component classified into the family by
 *   `topology.componentFamilies.componentToFamily`.
 * - <b>queue / queue-domain</b> — systems with any `readsEvents`/`writesEvents` declaration.
 * - <b>resource / resource-domain</b> — systems with any `readsResources`/`writesResources` declaration.
 * - <b>component-domain</b> — every system with at least one component access (basically all of them).
 *
 * Returns an empty Set when:
 * - topology is null
 * - dataTrack is null
 * - the track scope can't be resolved (e.g. unknown family)
 */
export function resolveSystemsForDataTrack(
  topology: TopologyDto | null,
  dataTrack: DataTrackSelection | null,
): Set<string> {
  if (!topology || !dataTrack) return new Set();

  const systems = topology.systems ?? [];

  switch (dataTrack.kind) {
    case 'component':
    case 'archetype-component': {
      const name = dataTrack.componentName;
      if (!name) return new Set();
      return collectByComponentAccess(systems, (s) => systemTouchesComponent(s, name));
    }

    case 'component-family': {
      const family = dataTrack.familyName;
      if (!family) return new Set();
      const map = topology.componentFamilies?.componentToFamily ?? {};
      const componentsInFamily = new Set(Object.keys(map).filter((c) => map[c] === family));
      if (componentsInFamily.size === 0) return new Set();
      return collectByComponentAccess(systems, (s) => {
        for (const c of componentsInFamily) {
          if (systemTouchesComponent(s, c)) return true;
        }
        return false;
      });
    }

    case 'component-domain':
      return collectByComponentAccess(systems, (s) =>
        hasAny(s.reads) || hasAny(s.readsFresh) || hasAny(s.readsSnapshot) || hasAny(s.additionalReads)
          || hasAny(s.writes) || hasAny(s.sideWrites),
      );

    case 'queue':
    case 'queue-domain':
      return collectByComponentAccess(systems, (s) => hasAny(s.readsEvents) || hasAny(s.writesEvents));

    case 'resource':
    case 'resource-domain':
      return collectByComponentAccess(systems, (s) => hasAny(s.readsResources) || hasAny(s.writesResources));
  }
}

/**
 * Whether a single system declares any access on the given component name. Walks all six access kinds
 * (write, side-write, reads, reads-fresh, reads-snapshot, additional-reads) so we catch every form.
 */
function systemTouchesComponent(s: SystemDefinitionDto, componentName: string): boolean {
  return contains(s.writes, componentName)
    || contains(s.sideWrites, componentName)
    || contains(s.reads, componentName)
    || contains(s.readsFresh, componentName)
    || contains(s.readsSnapshot, componentName)
    || contains(s.additionalReads, componentName);
}

function collectByComponentAccess(
  systems: readonly SystemDefinitionDto[],
  predicate: (s: SystemDefinitionDto) => boolean,
): Set<string> {
  const out = new Set<string>();
  for (const s of systems) {
    if (!s.name) continue;
    if (predicate(s)) out.add(s.name);
  }
  return out;
}

function contains(arr: readonly string[] | null | undefined, target: string): boolean {
  if (!arr) return false;
  for (let i = 0; i < arr.length; i++) {
    if (arr[i] === target) return true;
  }
  return false;
}

function hasAny(arr: readonly string[] | null | undefined): boolean {
  return !!arr && arr.length > 0;
}
