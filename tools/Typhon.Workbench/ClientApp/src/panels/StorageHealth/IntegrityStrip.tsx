import { ShieldAlert, ShieldCheck } from 'lucide-react';
import { StatusBadge } from '@/components/ui/status-badge';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSpineVerdict } from '@/hooks/integrity/useSpineVerdict';
import { openIntegrityForSession } from '@/shell/commands/openIntegrity';
import { verdictIsDamage, verdictTone } from '@/panels/Integrity/integrityModel';

/**
 * Storage Health's integrity presence: a one-line verdict for the open database, and the way into the full view.
 *
 * This is the compromise that keeps [06 §3](claude/design/Durability/Integrity/06-surfaces.md)'s intent — "Storage
 * Health is the primary host for aggregate integrity" — without making the feature session-captive. The verdict
 * belongs here, next to the other whole-database aggregates. The *view* cannot live here, because Storage Health
 * needs a live engine and the cases integrity exists for are the ones where there isn't one.
 *
 * It runs the `Spine` tier only: bounded by segment count rather than database size, the same tier the engine
 * runs on every open. Cheap enough to be a dashboard metric. It deliberately stops short of pronouncing —
 * the database is live while this runs, so nothing it sees can be `Confirmed`, and the link exists because the
 * real answer needs a deeper scan the operator asks for explicitly.
 */
export default function IntegrityStrip() {
  const filePath = useSessionStore((s) => s.filePath);
  const { data, isLoading, isError } = useSpineVerdict(filePath);

  if (!filePath || isLoading) {
    return null;
  }

  // A failed spine scan is itself worth surfacing — quietly hiding it would leave the strip looking like a
  // clean bill of health when it is actually an absence of information.
  if (isError || !data) {
    return (
      <StripShell>
        <span className="text-fs-sm text-muted-foreground">Integrity: could not read the bundle.</span>
      </StripShell>
    );
  }

  const damaged = verdictIsDamage(data.verdict);

  return (
    <StripShell>
      {damaged ? (
        <ShieldAlert className="h-3.5 w-3.5 text-destructive" />
      ) : (
        <ShieldCheck className="h-3.5 w-3.5 text-emerald-500" />
      )}
      <span className="text-fs-sm text-muted-foreground">Integrity</span>
      <StatusBadge tone={verdictTone(data.verdict)}>{data.verdict}</StatusBadge>
      <span className="text-fs-sm text-muted-foreground">
        {damaged
          ? 'from a shallow scan of a live database — run a full scan to confirm'
          : 'spine only; a full scan checks every page'}
      </span>
      <button
        type="button"
        onClick={openIntegrityForSession}
        data-testid="storage-health-integrity-link"
        className="ml-auto rounded border border-border px-1.5 py-0.5 text-fs-xs text-foreground hover:bg-accent"
      >
        Full scan →
      </button>
    </StripShell>
  );
}

function StripShell({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="flex shrink-0 flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-border px-3 py-1"
      data-testid="storage-health-integrity"
    >
      {children}
    </div>
  );
}
