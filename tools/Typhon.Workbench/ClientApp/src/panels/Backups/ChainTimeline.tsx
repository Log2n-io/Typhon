import { HelpCircle } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';
import { buildRibbons, formatBytes, type Listing, type RibbonPoint } from './backupsModel';

/**
 * The timeline (#1417 §2, AC-W2): one ribbon per chain, base → head. A point's chip shows its id and kind; its marks: suspect, offline, drill ✓/✗,
 * not restorable (red, with what breaks it in the detail). Unreadable packs and temps are listed below — they are part of what the store holds.
 */
export default function ChainTimeline({
  listing,
  selectedId,
  onSelect,
}: {
  listing: Listing;
  selectedId: number | null;
  onSelect: (id: number) => void;
}) {
  const ribbons = buildRibbons(listing);

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-timeline">
      <div className="mb-1 flex items-center gap-1.5">
        <span className="text-fs-sm font-medium text-foreground">Points</span>
        <span className="text-fs-sm text-muted-foreground">
          {listing.points.length === 0
            ? 'none yet'
            : `${new Set(listing.points.map((p) => p.backupId)).size} in ${ribbons.length} chain(s)`}
        </span>
        <TimelineLegend />
      </div>

      {ribbons.length === 0 && (
        <p className="text-fs-sm text-muted-foreground">
          This store holds no point yet. “Back up now” takes the first one (a full point).
        </p>
      )}

      <div className="flex flex-col gap-1.5">
        {ribbons.map((ribbon) => (
          <div key={ribbon.chainId} className="flex items-center gap-1 overflow-x-auto" data-testid="backups-ribbon">
            <span
              className="w-24 shrink-0 truncate font-mono text-fs-xs text-muted-foreground"
              title={`chain ${ribbon.chainId}`}
            >
              chain {ribbon.chainId.slice(0, 8)}
            </span>
            {ribbon.points.map((rp, i) => (
              <div key={rp.point.backupId} className="flex items-center">
                {i > 0 && <span className="h-px w-3 bg-border" />}
                <PointChip rp={rp} selected={rp.point.backupId === selectedId} onSelect={onSelect} />
              </div>
            ))}
          </div>
        ))}
      </div>

      {(listing.unreadable.length > 0 || listing.temporaryFiles.length > 0) && (
        <ul className="mt-1.5 text-fs-sm" data-testid="backups-unreadable">
          {listing.unreadable.map((u) => (
            <li key={u.file} className="text-destructive">
              <span className="font-mono">{u.file}</span> — unreadable: {u.reason}
            </li>
          ))}
          {listing.temporaryFiles.map((t) => (
            <li key={t} className="text-muted-foreground">
              <span className="font-mono">{t}</span> — temporary file of an interrupted backup; the next backup deletes
              it
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function PointChip({ rp, selected, onSelect }: { rp: RibbonPoint; selected: boolean; onSelect: (id: number) => void }) {
  const p = rp.point;
  const base = p.kind === 'Full' || p.kind === 'Compacted';
  const title = [
    `#${p.backupId} ${p.kind}${rp.compacted && p.kind !== 'Compacted' ? ' (compacted)' : ''}`,
    `P ${p.pointLsn.toLocaleString()} · C ${p.checkpointLsn.toLocaleString()}`,
    p.freezeUtc ? new Date(p.freezeUtc).toLocaleString() : '',
    `${p.pageCount.toLocaleString()} pages · ${formatBytes(p.packBytes)}`,
    p.suspect ? 'suspect: the live database had damaged pages at capture' : '',
    p.offline ? 'offline: captured from a closed database' : '',
    rp.restorable === false ? 'NOT restorable' : '',
  ]
    .filter(Boolean)
    .join('\n');

  return (
    <button
      type="button"
      onClick={() => onSelect(p.backupId)}
      title={title}
      data-testid={`backups-point-${p.backupId}`}
      aria-pressed={selected}
      className={cn(
        'flex shrink-0 items-center gap-1 rounded border px-1.5 py-0.5 font-mono text-fs-xs',
        base ? 'border-sky-500/70 bg-sky-500/10' : 'border-border bg-muted/40',
        rp.restorable === false && 'border-red-500 bg-red-500/15 text-red-600',
        selected && 'ring-2 ring-ring',
      )}
    >
      <span>#{p.backupId}</span>
      <span className="text-muted-foreground">{p.kind === 'Compacted' ? 'C' : p.kind === 'Full' ? 'F' : 'I'}</span>
      {p.suspect && (
        <span className="text-amber-500" aria-label="suspect">
          !
        </span>
      )}
      {p.offline && (
        <span className="text-muted-foreground" aria-label="offline">
          ○
        </span>
      )}
      {rp.drill === true && (
        <span className="text-emerald-500" aria-label="drill passed">
          ✓
        </span>
      )}
      {rp.drill === false && (
        <span className="text-red-500" aria-label="drill failed">
          ✗
        </span>
      )}
    </button>
  );
}

/** PC-10: what the ribbon's glyphs mean. */
function TimelineLegend() {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label="Timeline legend"
          data-testid="backups-legend"
          className="text-muted-foreground hover:text-foreground"
        >
          <HelpCircle className="h-3.5 w-3.5" />
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-80 text-fs-sm">
        <ul className="flex flex-col gap-1">
          <li>
            <b>F</b> full point · <b>I</b> incremental (its parent’s pages + its own) · <b>C</b> compacted base (05 §2):
            a point with a compacted pack shows once, as the pack a restore reads.
          </li>
          <li>
            <span className="text-amber-500">!</span> suspect — the live database had damaged pages at capture; the
            point keeps them faithfully.
          </li>
          <li>○ offline — captured from a closed database.</li>
          <li>
            <span className="text-emerald-500">✓</span>/<span className="text-red-500">✗</span> its newest drill passed
            / failed (a drill that could not run is not recorded).
          </li>
          <li>
            <span className="text-red-600">red</span> — not restorable by a quick verify; a verify of the point names
            what breaks it.
          </li>
          <li>
            Coverage bar: archived WAL in green, gaps in red (lighter when a later point covers them), ticks at each
            point’s P.
          </li>
        </ul>
      </PopoverContent>
    </Popover>
  );
}
