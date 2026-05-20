import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider, QueryCache, MutationCache } from '@tanstack/react-query';
import App from './App';
import { logError } from './stores/useLogStore';
import { shouldSilence } from './lib/silenceErrors';
// Load dockview's own stylesheet BEFORE globals.css so our theme-variable overrides in
// globals.css come later in the cascade and win on equal-specificity selectors like
// `.dockview-theme-dark`. Without this, the theme appears on the shell but dockview panels
// keep their vendor defaults.
import 'dockview-react/dist/styles/dockview.css';
import './globals.css';

// TanStack Query client is hoisted out of React so hot-module-reloads during dev don't reset the cache and drop every in-flight
// request. Defaults are conservative: no automatic refetch on window focus (the Workbench's data is engine-driven, not time-driven
// — a focus event doesn't imply stale data) and a single retry (the server and client are on the same machine; repeat failures
// almost always mean the engine went down, not a transient network glitch).
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
      staleTime: 30_000,
    },
  },
  // Log failures globally into the Workbench Logs panel. Individual call sites can still handle the
  // error their own way via try/catch — this is the floor, not a replacement. Queries can opt out
  // of the global log via `meta.silenceErrors`:
  //   - `true` — always silence (e.g. fs/stat on a moved/deleted recent file: every 404 is expected).
  //   - `(error) => boolean` — predicate (e.g. typeName schema hooks: silence 404s when the URL
  //     restored a selection that doesn't exist in the new session, but still log 500s).
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (shouldSilence(error, query.meta)) return;
      logError(`Query failed: ${query.queryKey.join(' / ')}`, {
        error: error instanceof Error ? error.message : String(error),
        queryKey: query.queryKey,
      });
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _vars, _ctx, mutation) => {
      if (shouldSilence(error, mutation.meta)) return;
      const key = mutation.options.mutationKey?.join(' / ') ?? '(anonymous mutation)';
      logError(`Mutation failed: ${key}`, {
        error: error instanceof Error ? error.message : String(error),
      });
    },
  }),
});

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element #root is missing from index.html — cannot mount Workbench.');
}

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
);
