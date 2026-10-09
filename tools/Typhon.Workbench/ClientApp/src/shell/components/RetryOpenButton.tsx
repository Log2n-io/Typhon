import { Button } from '@/components/ui/button';
import { useRetryDatabaseOpen } from '@/hooks/useRetryDatabaseOpen';

/**
 * "Reopen this database", wherever a schema fix has just been made.
 *
 * **Mount it only behind `useCanRetryDatabaseOpen()`.** It holds the reopen mutation, so a caller that renders it
 * unconditionally acquires a TanStack dependency for a button it may never show — which is how the blocked-state
 * banners ended up needing a `QueryClientProvider` to assert their own wording, and how the Query Console's SPATIAL
 * chip once did. The predicate is cheap and store-only for exactly this reason; the mutation lives here.
 */
export default function RetryOpenButton({
  label = 'Retry',
  busyLabel = 'Reopening…',
  title,
  variant = 'outline',
  className = 'h-6 text-fs-sm',
}: {
  label?: string;
  busyLabel?: string;
  title?: string;
  variant?: 'default' | 'outline';
  className?: string;
}) {
  const { isRetrying, error, retry } = useRetryDatabaseOpen();

  return (
    <>
      <Button
        variant={variant}
        size="sm"
        className={className}
        onClick={() => void retry()}
        disabled={isRetrying}
        title={title ?? 'Close and reopen this database, resolving its schema against the registered directories'}
      >
        {isRetrying ? busyLabel : label}
      </Button>
      {error && <p className="text-fs-sm font-semibold text-destructive">Reopen failed: {error}</p>}
    </>
  );
}
