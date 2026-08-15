import { AlertTriangle, Database, FileClock, Pin } from 'lucide-react';
import type { Profile } from '@/hooks/profiles/useProfileList';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * The Inspector card for a profiling capture — what a single click in **Profile sessions** produces.
 *
 * Its reason to exist is provenance. `databaseId` / `databaseName` ride in every capture header (#617 D-2) and were
 * read, typed and mapped all the way into the row model — then displayed nowhere. So a capture recorded against
 * another database rendered dimmed, had its drift figure withheld, and was refused by the wrong-database guard on
 * attach, without anything ever naming the database it *did* come from. The user could see that the Workbench had
 * decided something, and not what.
 */
export function CaptureDetailCard({ profile }: { profile: Profile }): React.JSX.Element {
  const openDatabase = useSessionStore((s) => s.filePath);

  return (
    <div className="flex h-full min-w-0 flex-col overflow-auto bg-background p-3">
      <div className="min-w-0 rounded-md border border-border bg-card p-3 text-fs-base">
        <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
          <FileClock className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <h3 className="truncate text-fs-lg font-semibold text-foreground" title={profile.fileName}>
            {profile.fileName}
          </h3>
          {profile.isPinned && <Pin className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-label="Pinned" />}
          <span className="ml-auto shrink-0 text-fs-sm text-muted-foreground">
            {profile.isActive ? 'open' : profile.profileId ? 'attached' : 'capture'}
          </span>
        </div>

        {/* Provenance first — it is the reason this card exists, and the one thing the list cannot show without a
            column that would be redundant for every capture of the open database. */}
        <Section title="Recorded against">
          <Row label="Database">
            <span className="flex items-center gap-1.5">
              <Database className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              <span className="font-mono">{profile.databaseName || <Unknown />}</span>
            </span>
          </Row>
          <Row label="Database id">
            <span className="font-mono text-fs-sm break-all">{profile.databaseId || <Unknown />}</span>
          </Row>
          <Row label="This database">
            {profile.belongsToDatabase ? (
              <span className="text-foreground">yes</span>
            ) : (
              <span className="flex items-start gap-1.5 text-destructive">
                <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                <span>
                  no — this capture was recorded elsewhere and cannot be attached to{' '}
                  <span className="font-mono">{fileStem(openDatabase)}</span>
                </span>
              </span>
            )}
          </Row>
          {profile.multipleEnginesObserved && (
            <Row label="Engines">
              <span className="text-amber-600 dark:text-amber-500">
                more than one engine wrote to this capture — figures below mix them
              </span>
            </Row>
          )}
        </Section>

        <Section title="Recording">
          <Row label="Started">{formatUtcTicks(profile.createdUtcTicks)}</Row>
          <Row label="Duration">{formatDuration(profile.durationTicks, profile.timestampFrequency)}</Row>
          <Row label="Ticks">{profile.tickCount > 0 ? profile.tickCount.toLocaleString() : <Unknown />}</Row>
          <Row label="Size">{formatSize(profile.sizeBytes)}</Row>
        </Section>

        <Section title="Transactions">
          <Row label="Range">
            {profile.tsnMax > 0 ? (
              <span className="font-mono tabular-nums">
                {profile.tsnMin.toLocaleString()} – {profile.tsnMax.toLocaleString()}
              </span>
            ) : (
              <Unknown />
            )}
          </Row>
          <Row label="Drift">
            {profile.driftTransactions === null ? (
              // Deliberately not "0". A foreign capture's transaction numbers are not comparable to this database's,
              // and an unclosed one has no end to measure from — saying "current" in either case would be a guess.
              <span className="text-muted-foreground">
                not comparable{profile.belongsToDatabase ? ' — the capture has no closing transaction' : ' — another database'}
              </span>
            ) : profile.driftTransactions === 0 ? (
              <span>current</span>
            ) : (
              <span className="tabular-nums">
                {profile.driftTransactions.toLocaleString()} transactions behind
              </span>
            )}
          </Row>
        </Section>

        {!profile.isReadable && (
          <p className="mt-2 rounded border border-destructive/40 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
            This file could not be read as a capture. It may be truncated, still being written, or not a
            <span className="font-mono"> .typhon-trace</span> at all.
          </p>
        )}
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }): React.JSX.Element {
  return (
    <section className="mt-2 first:mt-0">
      <h4 className="mb-1 text-fs-sm font-semibold uppercase tracking-wide text-muted-foreground">{title}</h4>
      <dl className="space-y-1">{children}</dl>
    </section>
  );
}

/**
 * Label above value, not beside it.
 *
 * A fixed label column (this was `w-28`, 112 px) is fine in a wide pane and wrong in the rail this card actually
 * lives in — the Inspector docks at ~260 px and users narrow it further, so every value started at a 112 px offset
 * with a sliver of room left, which reads as right-aligned and crammed. Stacking costs a line per row and is
 * correct at any width, which matters more here than density.
 */
function Row({ label, children }: { label: string; children: React.ReactNode }): React.JSX.Element {
  return (
    <div className="min-w-0 text-fs-base">
      <dt className="text-fs-sm text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words text-foreground">{children}</dd>
    </div>
  );
}

const Unknown = (): React.JSX.Element => <span className="text-muted-foreground">—</span>;

/** `C:\...\world-shard.typhon` → `world-shard.typhon`. Empty string when there is no open database. */
function fileStem(path: string | null): string {
  if (!path) return 'this database';
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

/** .NET UTC ticks → a local, human date. 0 (absent header field) reads as unknown rather than year 1. */
function formatUtcTicks(ticks: number): React.ReactNode {
  if (!ticks) return <Unknown />;
  const epochMs = (ticks - 621_355_968_000_000_000) / 10_000;
  if (!Number.isFinite(epochMs) || epochMs <= 0) return <Unknown />;
  return new Date(epochMs).toLocaleString();
}

function formatDuration(durationTicks: number, frequency: number): React.ReactNode {
  if (!durationTicks || !frequency) return <Unknown />;
  const seconds = durationTicks / frequency;
  if (seconds < 1) return `${(seconds * 1000).toFixed(0)} ms`;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const m = Math.floor(seconds / 60);
  return `${m} m ${(seconds - m * 60).toFixed(0)} s`;
}

function formatSize(bytes: number): React.ReactNode {
  if (!bytes) return <Unknown />;
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}
