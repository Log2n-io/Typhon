import { lazy, Suspense, useEffect, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Inline source-preview panel (issue #293, Phase 5). Lazy-loads `react-syntax-highlighter` so the
 * Prism bundle isn't paid by the rest of the Workbench. Fetches a window of source lines around
 * the emission line via `GET /api/profiler/source` and renders with the emission line highlighted.
 *
 * Panel params: `{ path: string, line: number }`. The Source row in `ProfilerDetail` opens this
 * panel via dockview, passing the resolved path + line.
 */
interface SourcePreviewParams {
  path: string;
  line: number;
}

interface SourceWindow {
  file: string;
  line: number;
  startLine: number;
  endLine: number;
  lines: string[];
}

// Lazy-load Prism via the top-level export (typed). Bundle ~50 KB gzipped, paid only when this
// panel mounts. The default export registers all common languages including C# — lighter targeted
// imports exist (`prism-light` + per-language registration) but their typings are weak; the
// top-level Prism export keeps types clean and the bundle still split.
const SyntaxHighlighter = lazy(() =>
  import('react-syntax-highlighter').then((mod) => ({ default: mod.Prism })),
);

export default function SourcePreviewPanel(props: IDockviewPanelProps): React.JSX.Element {
  const params = props.params as unknown as SourcePreviewParams | undefined;
  const token = useSessionStore((s) => s.token);
  const [data, setData] = useState<SourceWindow | null>(null);
  const [error, setError] = useState<string | null>(null);

  const path = params?.path;
  const line = params?.line ?? 1;

  useEffect(() => {
    if (!path) return;
    const ctrl = new AbortController();
    setError(null);
    setData(null);
    (async () => {
      try {
        const headers = new Headers();
        if (token) headers.set('X-Session-Token', token);
        const url = `/api/profiler/source?path=${encodeURIComponent(path)}&line=${line}&context=20`;
        const res = await fetch(url, { signal: ctrl.signal, headers });
        if (!res.ok) {
          let detail = `${res.status} ${res.statusText}`;
          try {
            const body = (await res.json()) as { error?: string };
            if (body?.error) detail = body.error;
          } catch { /* non-JSON body */ }
          setError(detail);
          return;
        }
        setData((await res.json()) as SourceWindow);
      } catch (err) {
        if ((err as Error).name !== 'AbortError') {
          setError((err as Error).message);
        }
      }
    })();
    return () => ctrl.abort();
  }, [path, line, token]);

  if (!path) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-fs-base text-muted-foreground">
        No source location selected.
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex h-full w-full flex-col items-start justify-start gap-2 overflow-auto bg-background p-3 text-fs-base">
        <header className="select-text font-mono text-muted-foreground">{path}:{line}</header>
        <p className="text-destructive">{error}</p>
      </div>
    );
  }

  if (!data) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-fs-base text-muted-foreground">
        Loading source…
      </div>
    );
  }

  const code = data.lines.join('\n');
  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <header className="wb-pane-header select-text border-b border-border px-3 py-2 font-mono text-fs-base text-muted-foreground">
        {data.file}<span className="text-foreground">:{data.line}</span>
        <span className="ml-2 text-fs-sm">(lines {data.startLine}–{data.endLine})</span>
      </header>
      <div className="flex-1 overflow-auto">
        <Suspense fallback={<pre className="p-3 font-mono text-fs-base text-muted-foreground">Loading highlighter…</pre>}>
          <SyntaxHighlighter
            language="csharp"
            showLineNumbers
            startingLineNumber={data.startLine}
            wrapLines
            lineProps={(lineNumber: number) =>
              lineNumber === data.line
                ? { style: { backgroundColor: 'rgba(255, 200, 0, 0.18)', display: 'block' } }
                : { style: { display: 'block' } }
            }
            customStyle={{ margin: 0, padding: '8px 12px', background: 'transparent', fontSize: '12px' }}
          >
            {code}
          </SyntaxHighlighter>
        </Suspense>
      </div>
    </div>
  );
}
