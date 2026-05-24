import { useMemo } from 'react';
import { useTopology } from '@/hooks/data/useTopology';
import { useSessionStore } from '@/stores/useSessionStore';

interface AccessChipsProps {
  componentTypeName: string;
}

/**
 * Inline access-declaration summary for a focused component (RFC 07 §Q1 — Unit 6 surfacing).
 * Pulls the declarations directly from the cached topology — single source of truth, no extra
 * round-trip. When the topology is still loading or every system surfaces empty arrays (legacy
 * v5 trace, or a session without phase declarations), the section renders nothing so it doesn't
 * push other content around.
 */
export default function AccessChips({ componentTypeName }: AccessChipsProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology } = useTopology(sessionId);

  const buckets = useMemo(() => {
    const writes: string[] = [];
    const sideWrites: string[] = [];
    const readsFresh: string[] = [];
    const readsSnapshot: string[] = [];
    const reads: string[] = [];

    for (const s of topology?.systems ?? []) {
      const name = s.name ?? '<unnamed>';
      if (s.writes?.includes(componentTypeName)) writes.push(name);
      if (s.sideWrites?.includes(componentTypeName)) sideWrites.push(name);
      if (s.readsFresh?.includes(componentTypeName)) readsFresh.push(name);
      if (s.readsSnapshot?.includes(componentTypeName)) readsSnapshot.push(name);
      if (s.reads?.includes(componentTypeName) || s.additionalReads?.includes(componentTypeName)) {
        reads.push(name);
      }
    }
    return { writes, sideWrites, readsFresh, readsSnapshot, reads };
  }, [topology, componentTypeName]);

  const hasAny =
    buckets.writes.length +
      buckets.sideWrites.length +
      buckets.readsFresh.length +
      buckets.readsSnapshot.length +
      buckets.reads.length >
    0;

  if (!hasAny) {
    return null;
  }

  return (
    <div className="border-b border-border bg-muted/10 px-3 py-2">
      <div className="text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">Access (RFC 07)</div>
      <div className="mt-1 flex flex-col gap-1.5">
        <ChipRow label="writes" tone="write" names={buckets.writes} />
        <ChipRow label="side-writes" tone="side-write" names={buckets.sideWrites} />
        <ChipRow label="reads fresh" tone="fresh" names={buckets.readsFresh} />
        <ChipRow label="reads snapshot" tone="snapshot" names={buckets.readsSnapshot} />
        <ChipRow label="reads" tone="read" names={buckets.reads} />
      </div>
    </div>
  );
}

interface ChipRowProps {
  label: string;
  tone: 'write' | 'side-write' | 'fresh' | 'snapshot' | 'read';
  names: string[];
}

function ChipRow({ label, tone, names }: ChipRowProps) {
  if (names.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <span className="font-mono text-fs-xs text-muted-foreground">{label}:</span>
      {names.map((n) => (
        <span key={n} className={`rounded border px-1.5 py-0.5 font-mono text-fs-xs ${toneClasses(tone)}`}>
          {n}
        </span>
      ))}
    </div>
  );
}

function toneClasses(tone: ChipRowProps['tone']): string {
  switch (tone) {
    case 'write':
      return 'border-rose-700/50 bg-rose-950/40 text-rose-200';
    case 'side-write':
      return 'border-orange-700/50 bg-orange-950/40 text-orange-200';
    case 'fresh':
      return 'border-emerald-700/50 bg-emerald-950/40 text-emerald-200';
    case 'snapshot':
      return 'border-sky-700/50 bg-sky-950/40 text-sky-200';
    case 'read':
      return 'border-slate-600/50 bg-slate-900/40 text-slate-200';
  }
}
