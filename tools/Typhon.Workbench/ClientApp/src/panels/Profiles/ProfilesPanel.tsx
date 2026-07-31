import type { IDockviewPanelProps } from 'dockview-react';
import { AlertTriangle, FileQuestion, Pin, RefreshCw } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useProfileList, type Profile } from '@/hooks/profiles/useProfileList';
import { useSessionCapability } from '@/stores/useSessionStore';

/** Ticks → a short human duration. Trace durations are Stopwatch ticks, so the frequency comes from the header. */
function formatDuration(ticks: number, frequency: number): string {
  if (ticks <= 0 || frequency <= 0) return '—';
  const seconds = ticks / frequency;
  if (seconds < 1) return `${Math.round(seconds * 1000)} ms`;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const m = Math.floor(seconds / 60);
  return `${m}m ${Math.round(seconds - m * 60)}s`;
}

/** .NET UTC ticks → a local date-time string. */
function formatRecorded(utcTicks: number): string {
  if (utcTicks <= 0) return '—';
  const ms = utcTicks / 10_000 - 62_135_596_800_000; // .NET epoch (0001-01-01) → Unix epoch
  return new Date(ms).toLocaleString();
}

function formatSize(bytes: number): string {
  if (bytes <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = bytes;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  return `${v < 10 && u > 0 ? v.toFixed(1) : Math.round(v)} ${units[u]}`;
}

const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });

/** Row tooltip — says what clicking will do, or why it will not work, before the click rather than after it. */
function rowTitle(profile: Profile): string {
  if (!profile.isReadable) return `${profile.fileName} — could not be read`;
  if (!profile.belongsToDatabase) {
    const recorded = profile.databaseName || profile.databaseId || 'another database';
    return `Recorded against ${recorded}, not the database this session has open. Open it as a standalone trace instead.`;
  }
  return profile.profileId ? 'Close this profile' : 'Open this profile';
}

/**
 * Drift, in words. This is the readout §4.6 argues is often the actual answer someone wants: not "this capture is old"
 * but how far the database has moved since — measured in transactions, which is exact rather than a guess from
 * timestamps.
 *
 * A foreign capture gets no number at all. Its transaction numbers belong to a different database's sequence, so any
 * subtraction against this one would render as an authoritative "290 txns behind" that means nothing — worse than a
 * dash, because it looks like an answer.
 */
function formatDrift(profile: Profile): string {
  // Unreadable first: such a row also reports belongsToDatabase false — the server cannot vouch for a file it could not
  // parse — but "other database" would be a claim, and a truncated capture of THIS database is the likelier explanation.
  if (!profile.isReadable) return '—';
  if (!profile.belongsToDatabase) return 'other database';
  if (profile.driftTransactions === null) return '—';
  if (profile.driftTransactions === 0) return 'current';
  return `${compact.format(profile.driftTransactions)} txns behind`;
}

/**
 * The **Profiles** panel — the captures recorded against the database this session has open (#617, design D-10 + D-5).
 *
 * Every row is rendered from the capture's own header: nothing here builds a sidecar cache, which is what makes opening
 * a database with thirty captures cheap. Clicking a row attaches it as the session's active profile, at which point the
 * profiler panels become available — the session acquires the capability, rather than changing what kind of session it
 * is.
 */
export default function ProfilesPanel(_props: IDockviewPanelProps) {
  const { profiles, profilingsDirectory, isLoading, isError, isFetching, refetch, attach, detach } = useProfileList();
  const hasDatabase = useSessionCapability('database');

  const busy = attach.isPending || detach.isPending;

  // A rejected attach is the interesting failure, not an edge case: the wrong-database guard (AC8) is exactly what fires
  // when a capture was copied into this bundle from elsewhere, and D-1's whole point is that co-location does not prove
  // provenance. Without this the click would look like it did nothing at all.
  const actionError = attach.error ?? detach.error;

  const onRowClick = (profile: Profile) => {
    if (busy) return;
    attach.reset();
    detach.reset();
    if (profile.profileId) {
      detach.mutate(profile.profileId);
    } else if (profile.isReadable) {
      attach.mutate(profile.fileName);
    }
  };

  return (
    <div className="h-full w-full overflow-hidden flex flex-col text-xs">
      <div className="flex items-center gap-2 px-2 py-1 border-b border-border shrink-0">
        <span className="font-medium">Profiles</span>
        <span className="text-muted-foreground">{profiles.length}</span>
        <div className="flex-1" />
        <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => void refetch()} title="Refresh">
          <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      {actionError && (
        <div
          role="alert"
          className="shrink-0 flex items-start gap-1.5 px-2 py-1 border-b border-border bg-destructive/10 text-destructive"
        >
          <AlertTriangle className="h-3.5 w-3.5 shrink-0 mt-px" />
          <span className="min-w-0">{actionError.message}</span>
        </div>
      )}

      <div className="flex-1 overflow-auto">
        {!hasDatabase ? (
          <Empty>Profiles live with a database. Open a <code>.typhon</code> database to see its captures.</Empty>
        ) : isLoading ? (
          <Empty>Loading captures…</Empty>
        ) : isError ? (
          <Empty>
            Could not read this database&apos;s <code>profilings/</code> directory.
          </Empty>
        ) : profiles.length === 0 ? (
          <Empty>
            No captures recorded against this database yet. They appear here once something profiles it — captures are
            written to <code className="break-all">{profilingsDirectory}</code>.
          </Empty>
        ) : (
          <table className="w-full border-collapse">
            <thead className="sticky top-0 bg-background">
              <tr className="text-muted-foreground text-left">
                <Th>Recorded</Th>
                <Th>Duration</Th>
                <Th className="text-right">Ticks</Th>
                <Th>Drift</Th>
                <Th className="text-right">Size</Th>
                <Th />
              </tr>
            </thead>
            <tbody>
              {profiles.map((p) => (
                <tr
                  key={p.fileName}
                  onClick={() => onRowClick(p)}
                  title={rowTitle(p)}
                  className={[
                    'h-[22px] cursor-pointer border-b border-border/40',
                    p.isActive ? 'bg-primary/10 font-medium' : 'hover:bg-muted/50',
                    p.isReadable && p.belongsToDatabase ? '' : 'text-muted-foreground italic',
                    busy ? 'pointer-events-none opacity-60' : '',
                  ].join(' ')}
                >
                  <Td>
                    <span className="flex items-center gap-1">
                      {p.isPinned && <Pin className="h-3 w-3 shrink-0" aria-label="Pinned" />}
                      {p.multipleEnginesObserved && (
                        <AlertTriangle
                          className="h-3 w-3 shrink-0 text-amber-500"
                          aria-label="Recorded with more than one engine live — archetype correlation is name-based"
                        />
                      )}
                      {p.isReadable && !p.belongsToDatabase && (
                        <FileQuestion
                          className="h-3 w-3 shrink-0 text-amber-500"
                          aria-label="Recorded against a different database — it is in this bundle but does not belong to it"
                        />
                      )}
                      {p.isReadable ? formatRecorded(p.createdUtcTicks) : `${p.fileName} (unreadable)`}
                    </span>
                  </Td>
                  <Td>{formatDuration(p.durationTicks, p.timestampFrequency)}</Td>
                  <Td className="text-right tabular-nums">{p.tickCount > 0 ? compact.format(p.tickCount) : '—'}</Td>
                  <Td className="text-muted-foreground">{formatDrift(p)}</Td>
                  <Td className="text-right tabular-nums">{formatSize(p.sizeBytes)}</Td>
                  <Td className="text-right text-muted-foreground">{p.isActive ? 'open' : ''}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

function Empty({ children }: { children: React.ReactNode }) {
  return <div className="p-3 text-muted-foreground max-w-prose">{children}</div>;
}

function Th({ children, className = '' }: { children?: React.ReactNode; className?: string }) {
  return <th className={`px-2 py-1 font-normal ${className}`}>{children}</th>;
}

function Td({ children, className = '' }: { children?: React.ReactNode; className?: string }) {
  return <td className={`px-2 truncate ${className}`}>{children}</td>;
}
