import { Button } from '@/components/ui/button';
import type { VerifyLevel } from '@/stores/useBackupsStore';
import type { Status } from './backupsModel';

const LEVELS: readonly VerifyLevel[] = ['quick', 'full', 'deep'];

const LEVEL_BLURB: Readonly<Record<VerifyLevel, string>> = {
  quick: 'headers, footers, indexes, tails and chains',
  full: 'every byte’s CRC, every page decoded',
  deep: 'full, plus an integrity scan of the newest restorable image',
};

interface ActionsBarProps {
  status: Status;
  busy: boolean;
  verifyLevel: VerifyLevel;
  onVerifyLevel: (level: VerifyLevel) => void;
  onBackUp: (full: boolean) => void;
  onVerify: () => void;
  onRetention: () => void;
  onRestore: () => void;
}

/**
 * The store's acts (#1417 §2). "Back up now" follows the route the server decided (WB-4) and is absent — with the reason — when no route reaches the
 * bundle; verify, retention and restore are absent from a store with no point (PC-6: never a dead button). One job at a time per store: while one
 * runs, the others wait (disabled for that moment only).
 */
export default function ActionsBar(props: ActionsBarProps) {
  const { status, busy, verifyLevel } = props;
  const route = status.route;
  const hasPoints = (status.storeStatus?.points ?? 0) > 0;

  return (
    <div
      className="flex shrink-0 flex-wrap items-center gap-2 border-b border-border px-3 py-1.5"
      data-testid="backups-actions"
    >
      {route.kind !== 'unavailable' ? (
        <>
          <Button
            disabled={busy}
            onClick={() => props.onBackUp(false)}
            className="h-7 px-3 text-fs-sm"
            data-testid="backups-backup-now"
            title={route.reason}
          >
            Back up now
          </Button>
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => props.onBackUp(true)}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-backup-full"
          >
            Full
          </Button>
        </>
      ) : (
        <span className="max-w-xl text-fs-sm text-muted-foreground" data-testid="backups-backup-unavailable">
          Back up now is not available: {route.reason}
        </span>
      )}

      {hasPoints && (
        <>
          <span className="mx-1 h-4 w-px bg-border" />

          <div
            className="flex overflow-hidden rounded border border-border"
            role="radiogroup"
            aria-label="Verify level"
          >
            {LEVELS.map((l) => (
              <button
                key={l}
                type="button"
                role="radio"
                aria-checked={verifyLevel === l}
                title={LEVEL_BLURB[l]}
                onClick={() => props.onVerifyLevel(l)}
                data-testid={`backups-verify-level-${l}`}
                className={`px-2 py-0.5 text-fs-sm capitalize ${verifyLevel === l ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-muted/60'}`}
              >
                {l}
              </button>
            ))}
          </div>
          <Button
            variant="outline"
            disabled={busy}
            onClick={props.onVerify}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-verify"
          >
            Verify store
          </Button>

          <span className="mx-1 h-4 w-px bg-border" />

          <Button
            variant="outline"
            disabled={busy}
            onClick={props.onRetention}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-retention"
          >
            Retention…
          </Button>
          <Button
            variant="outline"
            disabled={busy}
            onClick={props.onRestore}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-restore"
          >
            Restore…
          </Button>
        </>
      )}
    </div>
  );
}
