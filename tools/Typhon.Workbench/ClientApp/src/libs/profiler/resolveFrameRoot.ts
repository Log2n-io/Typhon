import type { CpuFrameSymbol } from '@/stores/useCpuFrameStore';
import type { ResolvedSourceLocation } from '@/stores/useSourceLocationStore';

/**
 * Resolve a span / chunk source site to the CPU-sample frame of the **method that encloses it** — so a
 * "View in Call Tree" command can open the tree re-rooted at that method (#351 §8.2).
 *
 * The match is by **file + line containment**, never by free-text name: among the sampled frames in the
 * same file, the enclosing method is the one whose method-entry line (`CpuFrameSymbol.line`, §7) is the
 * greatest line `≤` the site's line. This reuses the `(file, line)` identity scheme the frame parser and
 * the #302 source-location manifest already share — so it is robust to overloads, generics and the
 * span-name ≠ method-name cases that pure name matching trips over.
 *
 * Returns `null` when nothing qualifies — the site has no resolved source, or the enclosing method was
 * never sampled (so it carries no frame). The caller then falls back to a plain time-window scope.
 */
export function resolveFrameRootForSite(
  site: ResolvedSourceLocation | null,
  frames: Map<number, CpuFrameSymbol>,
): number | null {
  if (!site || site.line <= 0) {
    return null;
  }
  let best: CpuFrameSymbol | null = null;
  for (const frame of frames.values()) {
    // Same file, a real source line, and the method starts at or before the site.
    if (frame.file !== site.file || frame.line <= 0 || frame.line > site.line) {
      continue;
    }
    // The enclosing method is the deepest one starting at or before the site.
    if (best === null || frame.line > best.line) {
      best = frame;
    }
  }
  return best === null ? null : best.frameId;
}
