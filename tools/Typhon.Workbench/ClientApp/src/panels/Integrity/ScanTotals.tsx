import { formatBytes } from '@/libs/formatBytes';
import type { Totals } from './integrityModel';

/**
 * What the scan actually looked at.
 *
 * This exists so a green verdict is falsifiable. "Sound" from a scan that walked 4 segments and read 12
 * pages means something very different from "Sound" after 4,318 pages and 82 segments, and without these
 * numbers the two are indistinguishable on screen. A reader who suspects the scan did nothing should be
 * able to confirm or dismiss that here rather than by taking the verdict on faith.
 */
export default function ScanTotals({ totals }: { totals: Totals }) {
  return (
    <div
      className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-0.5 border-b border-border px-3 py-1 text-fs-sm text-muted-foreground"
      data-testid="integrity-totals"
    >
      <Metric label="pages scanned" value={totals.pagesScanned.toLocaleString()} />
      <Metric label="allocated" value={totals.pagesAllocated.toLocaleString()} />
      <Metric label="segments" value={totals.segmentsWalked.toLocaleString()} />
      <Metric
        label="sector-verified"
        value={totals.pagesWithSectorFooters.toLocaleString()}
        title="Pages carrying per-sector CRCs (format v6+). Older pages verify whole-page only."
      />
      <Metric
        label="checksum failures"
        value={totals.checksumFailures.toLocaleString()}
        tone={totals.checksumFailures > 0 ? 'bad' : undefined}
      />
      <Metric
        label="sector failures"
        value={totals.sectorFailures.toLocaleString()}
        tone={totals.sectorFailures > 0 ? 'bad' : undefined}
      />
      {totals.bytesLeaked > 0 && (
        <Metric label="leaked" value={formatBytes(totals.bytesLeaked)} title="Allocated but unreachable — reclaimable, not damage." />
      )}
    </div>
  );
}

function Metric({ label, value, title, tone }: { label: string; value: string; title?: string; tone?: 'bad' }) {
  return (
    <span title={title}>
      <span className={`tabular-nums ${tone === 'bad' ? 'text-destructive' : 'text-foreground'}`}>{value}</span>{' '}
      {label}
    </span>
  );
}
