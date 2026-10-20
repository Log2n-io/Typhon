import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { formatBytes, type Drill, type Point } from './backupsModel';

interface PointDetailProps {
  point: Point;
  /** False when a quick verify says it cannot be restored; null when unknown. */
  restorable: boolean | null;
  drill: Drill | undefined;
  scratch: string;
  busy: boolean;
  onScratchChange: (scratch: string) => void;
  onVerify: () => void;
  onDrill: () => void;
  onCompact: () => void;
  onRestore: () => void;
}

/**
 * A point's detail, in the panel (#1417 WB-11: the view also lives with no session, where there is no Inspector — Integrity's precedent): every field the
 * CLI's `list --verbose` prints, and the acts on this point. Compact states its cost before it runs: it reads about the chain's bytes and writes one full
 * pack; it deletes nothing (05 §2), so it needs no consent, only the operator's knowing what it costs.
 */
export default function PointDetail(props: PointDetailProps) {
  const { point: p, restorable, drill, scratch, busy } = props;
  const [confirmCompact, setConfirmCompact] = useState(false);
  const base = p.kind === 'Full' || p.kind === 'Compacted';

  const rows: [string, string][] = [
    ['Kind', p.kind + (p.offline ? ' · offline' : '') + (p.suspect ? ' · suspect' : '')],
    ['Chain', p.chainId],
    ['Parent / base', `${p.parentId === 0 ? '—' : `#${p.parentId}`} / #${p.baseId}`],
    ['C (replay starts)', p.checkpointLsn.toLocaleString()],
    ['P (restored through)', p.pointLsn.toLocaleString()],
    ['Frozen', p.freezeUtc ? new Date(p.freezeUtc).toLocaleString() : '—'],
    ['Tail copied', p.tailEndUtc ? new Date(p.tailEndUtc).toLocaleString() : '—'],
    ['Image', `${p.imagePages.toLocaleString()} pages`],
    ['Pack', `${p.packFile} · ${p.pageCount.toLocaleString()} pages · ${formatBytes(p.packBytes)}`],
    ['WAL tail', `${p.tailSegments} segment(s)`],
    ['Timeline', p.timelineId],
    ['Format revision', String(p.formatRevision)],
  ];

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-point-detail">
      <div className="mb-1 flex items-center gap-2">
        <span className="text-fs-base font-medium">Point #{p.backupId}</span>
        {restorable === false && (
          <span className="text-fs-sm text-destructive">not restorable — verify it to see what breaks it</span>
        )}
        {drill && (
          <span className={drill.outcome === 'Failed' ? 'text-fs-sm text-destructive' : 'text-fs-sm text-emerald-600'}>
            last drill {drill.outcome}
            {drill.utc ? ` · ${new Date(drill.utc).toLocaleString()}` : ''}
            {drill.error ? ` — ${drill.error}` : ''}
          </span>
        )}
      </div>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5 text-fs-sm">
        {rows.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-muted-foreground">{k}</dt>
            <dd className="font-mono">{v}</dd>
          </div>
        ))}
      </dl>

      <div className="mt-2 flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          disabled={busy}
          onClick={props.onVerify}
          className="h-7 px-2 text-fs-sm"
          data-testid="backups-point-verify"
        >
          Verify this point
        </Button>
        {restorable !== false && (
          <Button
            variant="outline"
            disabled={busy}
            onClick={props.onRestore}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-point-restore"
          >
            Restore…
          </Button>
        )}
        {!base &&
          (confirmCompact ? (
            <span className="flex items-center gap-1 text-fs-sm">
              Reads about its chain’s bytes, writes one full pack; deletes nothing.
              <Button
                disabled={busy}
                onClick={() => {
                  setConfirmCompact(false);
                  props.onCompact();
                }}
                className="h-7 px-2 text-fs-sm"
                data-testid="backups-point-compact-confirm"
              >
                Compact
              </Button>
              <Button variant="ghost" onClick={() => setConfirmCompact(false)} className="h-7 px-2 text-fs-sm">
                Cancel
              </Button>
            </span>
          ) : (
            <Button
              variant="outline"
              disabled={busy}
              onClick={() => setConfirmCompact(true)}
              className="h-7 px-2 text-fs-sm"
              data-testid="backups-point-compact"
            >
              Compact…
            </Button>
          ))}
      </div>

      <div className="mt-2 flex flex-wrap items-center gap-2">
        <span className="text-fs-sm text-muted-foreground">Drill into</span>
        <Input
          value={scratch}
          onChange={(e) => props.onScratchChange(e.target.value)}
          spellCheck={false}
          className="h-7 min-w-[16rem] flex-1 font-mono text-fs-sm"
          data-testid="backups-point-scratch"
          placeholder="scratch directory (needs the whole image's space)"
        />
        <Button
          variant="outline"
          disabled={busy}
          onClick={props.onDrill}
          className="h-7 px-2 text-fs-sm"
          data-testid="backups-point-drill"
        >
          Drill
        </Button>
      </div>
      <p className="mt-0.5 text-fs-xs text-muted-foreground">
        A drill restores the point into the scratch directory in a separate process, opens it (its WAL tail replays) and
        checks it deep, then deletes it. Never inside the store. Space needed: about {formatBytes(p.imagePages * 8192)}.
      </p>
    </div>
  );
}
