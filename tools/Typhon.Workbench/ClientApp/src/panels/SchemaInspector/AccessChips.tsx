import { useMemo } from 'react';
import { useTopology } from '@/hooks/data/useTopology';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  captureComponentIdentities,
  classifyComponentTraceRelation,
  declaresComponent,
  traceIdentityOf,
  type ComponentIdentityLike,
} from '@/libs/schema/componentIdentity';

interface AccessChipsProps {
  /** The focused component, carrying both names. The join runs on `fullName` — see {@link traceIdentityOf}. */
  component: ComponentIdentityLike;
}

/**
 * Inline access-declaration summary for a focused component (RFC 07 §Q1; the database bridge is #618 §4.1).
 *
 * Pulls the declarations from the cached topology — single source of truth, no extra round-trip. With a capture
 * attached to an open database this is the readout that gives a database file its **system dimension**: a database on
 * disk has none of its own.
 *
 * **The join runs on the CLR full name, not the display name.** Before #618 this compared the panel's display name —
 * a bare leaf in a trace session, the `[Component("…")]` schema name in an open database — against declarations
 * holding `Type.FullName`, so it matched nothing in either session kind and rendered `null` silently. See
 * `componentIdentity.ts` for the full identifier table.
 *
 * It also distinguishes **"the capture never saw this component"** from **"no system touches it"**. Collapsing the two
 * into one empty state is the silent-wrongness §5.7 forbids: the first says nothing can be known, the second is a real
 * finding about the recorded run.
 */
export default function AccessChips({ component }: AccessChipsProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology } = useTopology(sessionId);
  const identity = traceIdentityOf(component);

  const buckets = useMemo(() => {
    const writes: string[] = [];
    const sideWrites: string[] = [];
    const readsFresh: string[] = [];
    const readsSnapshot: string[] = [];
    const reads: string[] = [];

    for (const s of topology?.systems ?? []) {
      const name = s.name ?? '<unnamed>';
      if (declaresComponent(s.writes, identity)) writes.push(name);
      if (declaresComponent(s.sideWrites, identity)) sideWrites.push(name);
      if (declaresComponent(s.readsFresh, identity)) readsFresh.push(name);
      if (declaresComponent(s.readsSnapshot, identity)) readsSnapshot.push(name);
      if (declaresComponent(s.reads, identity) || declaresComponent(s.additionalReads, identity)) {
        reads.push(name);
      }
    }
    return { writes, sideWrites, readsFresh, readsSnapshot, reads };
  }, [topology, identity]);

  const declaredAnywhere =
    buckets.writes.length +
      buckets.sideWrites.length +
      buckets.readsFresh.length +
      buckets.readsSnapshot.length +
      buckets.reads.length >
    0;

  // Still loading: say nothing rather than flashing "not in this capture" and then correcting itself.
  if (!topology) {
    return null;
  }

  const relation = classifyComponentTraceRelation(
    identity,
    captureComponentIdentities(topology.componentTypes),
    declaredAnywhere,
  );

  if (relation !== 'declared') {
    return (
      <div className="border-b border-border bg-muted/10 px-3 py-2" data-testid="access-chips-empty" data-relation={relation}>
        <div className="text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">Access (RFC 07)</div>
        <p className="mt-1 text-fs-sm text-muted-foreground">
          {relation === 'absent' ? (
            <>
              This capture has no record of <span className="font-mono">{identity || 'this component'}</span> — it was added, removed or renamed since. Which
              systems touch it cannot be answered from this profile.
            </>
          ) : (
            <>No system declared access to this component in the recorded run.</>
          )}
        </p>
      </div>
    );
  }

  return (
    <div className="border-b border-border bg-muted/10 px-3 py-2" data-testid="access-chips">
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
