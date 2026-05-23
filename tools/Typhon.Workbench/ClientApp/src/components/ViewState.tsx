import type { ReactNode } from 'react';
import { FetchError } from '@/api/client';
import type { ViewPhase } from '@/hooks/use202Query';

/**
 * The one PC-2 state-set renderer (platform-conventions §PC-2, conformance suite D). Every shell/view
 * surface routes its loading / building(202) / empty / no-selection / error states through this so they
 * read uniformly: skeleton while loading, a sentence (never a blank panel) when empty, a
 * `ProblemDetails` title + Retry on error (never a raw status code), an inline picker when nothing is
 * selected. `ready` renders the children.
 */
export interface ViewStateProps {
  phase: ViewPhase;
  error?: unknown;
  onRetry?: () => void;
  /** Sentence shown in the empty state. */
  emptyMessage?: string;
  /** Content shown in the no-selection state (e.g. an inline picker). */
  noSelection?: ReactNode;
  buildingMessage?: string;
  children: ReactNode;
}

/** Extract a human title from a thrown error — RFC 7807 title/detail, never a bare status code. */
function errorTitle(error: unknown): string {
  if (error instanceof FetchError) return error.message;
  if (error instanceof Error) return error.message;
  return 'Something went wrong.';
}

function Centered({ children, tone }: { children: ReactNode; tone?: 'error' }) {
  return (
    <div className="flex h-full items-center justify-center bg-background p-4">
      <div className={'text-center text-density-sm ' + (tone === 'error' ? 'text-destructive' : 'text-muted-foreground')}>
        {children}
      </div>
    </div>
  );
}

export default function ViewState({
  phase,
  error,
  onRetry,
  emptyMessage = 'Nothing to show here.',
  noSelection,
  buildingMessage = 'Building…',
  children,
}: ViewStateProps) {
  switch (phase) {
    case 'loading':
      return (
        <div className="flex h-full flex-col gap-2 bg-background p-3" aria-busy="true" data-testid="view-state-loading">
          <div className="h-4 w-1/3 animate-pulse rounded bg-muted" />
          <div className="h-4 w-2/3 animate-pulse rounded bg-muted" />
          <div className="h-4 w-1/2 animate-pulse rounded bg-muted" />
        </div>
      );
    case 'building':
      return <Centered>{buildingMessage}</Centered>;
    case 'empty':
      return <Centered>{emptyMessage}</Centered>;
    case 'no-selection':
      return <Centered>{noSelection ?? 'Select something to inspect it.'}</Centered>;
    case 'error':
      return (
        <Centered tone="error">
          <p>{errorTitle(error)}</p>
          {onRetry && (
            <button
              type="button"
              onClick={onRetry}
              className="mt-2 rounded border border-border px-2 py-1 text-foreground hover:bg-muted/60"
            >
              Retry
            </button>
          )}
        </Centered>
      );
    case 'ready':
      return <>{children}</>;
  }
}
