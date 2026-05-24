import { useEffect, useState } from 'react';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useInspectorTargetStore, inspectorTargetKey } from '@/stores/useInspectorTargetStore';
import { resolveInspectorTarget, commitInspectorTarget, type TargetCandidate } from './inspectorTarget';

/**
 * Drives a self-addressing deep inspector's target (PC-9). Returns the target the panel should render plus
 * whether it was auto-resolved (chip) and a `pick` to switch. The cold-open target is derived *synchronously*
 * (no effect) from the candidate list + the PC-1 recorded choice, so there is no first-frame empty flash and
 * auto-target never publishes to the bus — only a deliberate `pick` does (it's a user selection).
 */

interface Options {
  type: 'archetype' | 'component';
  /** Every loaded target, with id + entity count — the auto-pick candidates and the switcher source. */
  candidates: TargetCandidate[];
  /** True while the candidate list is still loading (gates auto-target until it lands). */
  loading: boolean;
}

interface Result {
  /** The id to render, or null → show Loading (candidates pending) or Empty (none exist). */
  targetId: string | null;
  /** True when the rendered target was auto-resolved (heuristic/first) → show the "(auto)" chip. */
  auto: boolean;
  /** Commit a deliberate switch: publishes the bus slot + records the choice (PC-1). */
  pick: (id: string) => void;
}

export function useInspectorTarget({ type, candidates, loading }: Options): Result {
  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  const filePath = useSessionStore((s) => s.filePath);
  const sessionId = useSessionStore((s) => s.sessionId);
  const kind = useSessionStore((s) => s.kind);
  const savedByKey = useInspectorTargetStore((s) => s.byKey);
  const savePref = useInspectorTargetStore((s) => s.save);

  const prefKey = inspectorTargetKey(filePath, sessionId, kind);
  const recordedId =
    type === 'archetype' ? savedByKey[prefKey]?.archetypeId ?? null : savedByKey[prefKey]?.componentType ?? null;

  // The user/bus-pinned target — set when a real selection of our type lands; null means "show the default".
  const [pinnedId, setPinnedId] = useState<string | null>(leaf?.type === type ? (leaf.ref as string) : null);
  useEffect(() => {
    if (leaf?.type === type) setPinnedId(leaf.ref as string);
  }, [leaf, type]);

  // Cold-open default, derived synchronously (no effect → no empty flash, no bus write).
  const autoResolved = !loading && candidates.length > 0 ? resolveInspectorTarget(candidates, recordedId) : null;
  const targetId = pinnedId ?? autoResolved?.id ?? null;
  const auto = pinnedId == null && (autoResolved?.auto ?? false);

  const pick = (id: string) => {
    setPinnedId(id);
    commitInspectorTarget({ type, id, prefKey, select, savePref });
  };

  return { targetId, auto, pick };
}
