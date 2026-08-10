import { Info } from 'lucide-react';
import type { Limits } from './integrityModel';

/**
 * What the scan could not have detected.
 *
 * **This component has no collapse control, no `defaultOpen`, and no caller-supplied way to hide it, and
 * that is deliberate** ([04 §6](claude/design/Durability/Integrity/04-report-and-loss.md)). It renders on a
 * fully green report exactly as it renders on a catastrophic one.
 *
 * The reasoning is worth stating because the product instinct is the opposite. A scan verifies that a
 * database is *internally consistent*. It cannot verify that the database matches what was committed —
 * committed updates lost in a prior recovery, or entities a prior recovery resurrected, leave a perfectly
 * self-consistent file behind. A green verdict therefore means "nothing here contradicts anything else
 * here", which is a narrower claim than the word *Sound* suggests. The moment that gap is most likely to
 * mislead is precisely when everything passed and the operator stops reading — so that is the moment the
 * block must not be suppressible.
 */
export default function LimitsBlock({ limits }: { limits: Limits }) {
  const hasDetail = limits.checksSkipped.length > 0 || limits.caveats.length > 0;

  return (
    <div
      className="shrink-0 border-t border-border bg-muted/30 px-3 py-2 text-fs-sm text-muted-foreground"
      data-testid="integrity-limits"
    >
      <div className="flex items-start gap-2">
        <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
        <div className="min-w-0">
          <p className="font-medium text-foreground">Limits of this scan</p>
          <p className="mt-0.5">{limits.structural}</p>

          {hasDetail && (
            <div className="mt-1.5 flex flex-col gap-1">
              {limits.checksSkipped.length > 0 && (
                <div>
                  <span className="text-foreground">Checks not run at this depth: </span>
                  <span data-testid="integrity-limits-skipped">{limits.checksSkipped.join(' · ')}</span>
                </div>
              )}
              {limits.caveats.length > 0 && (
                <ul className="list-inside list-disc" data-testid="integrity-limits-caveats">
                  {limits.caveats.map((c) => (
                    <li key={c}>{c}</li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
