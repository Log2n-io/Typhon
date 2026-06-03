import { useEffect } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { usePostApiSessionsSessionIdQueryParse } from '@/api/generated/query-console/query-console';

/**
 * Phase-1 DSL editor — a plain `<textarea>` with debounced `/parse` integration. On every keystroke (250 ms after
 * the user stops typing) the text is posted to the server, the spec is updated, and any parse errors surface in the
 * diagnostics list below. Monaco integration is deferred (plan deviation D8) — the textarea covers the Phase-1
 * acceptance criterion (chip + DSL round-trip + share via URL) at zero bundle cost.
 */
export function DslEditor() {
  const dslDraft = useQueryConsoleStore((s) => s.dslDraft);
  const setDslDraft = useQueryConsoleStore((s) => s.setDslDraft);
  const setSpec = useQueryConsoleStore((s) => s.setSpec);
  const setParseErrors = useQueryConsoleStore((s) => s.setParseErrors);
  const parseErrors = useQueryConsoleStore((s) => s.parseErrors);
  const sessionId = useSessionStore((s) => s.sessionId);
  const parseMutation = usePostApiSessionsSessionIdQueryParse();

  // Debounced parse — every keystroke pushes to /parse 250 ms after the user pauses. The spec / parseErrors update
  // through the store so chip mode rebuilds on tab-switch.
  useEffect(() => {
    if (!sessionId) return;
    const handle = setTimeout(() => {
      parseMutation.mutate(
        { sessionId, data: { dsl: dslDraft } },
        {
          onSuccess: (resp) => {
            // Orval wraps the body in `{ data, headers }` — unwrap to reach the QueryParseResponse fields.
            setSpec(resp.data.spec);
            setParseErrors(resp.data.errors ?? []);
          },
        },
      );
    }, 250);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dslDraft, sessionId]);

  return (
    <div className="flex h-full flex-col gap-2 p-2">
      <textarea
        className="min-h-32 w-full flex-1 resize-none rounded border border-border bg-background p-2 font-mono text-sm"
        value={dslDraft}
        onChange={(e) => setDslDraft(e.target.value)}
        placeholder={'FROM <archetype>\nWHERE <component>.<field> >= <value>\nORDER BY <component>.<field> DESC\nTAKE 100'}
        spellCheck={false}
        aria-label="Query DSL editor"
      />
      {parseErrors.length > 0 && (
        // select-text opts back in to copyability — body sets user-select:none by default (see globals.css).
        // Users need to paste error text into Slack / search / GitHub issues.
        <div className="select-text rounded border border-red-500/40 bg-red-500/5 p-2 text-xs">
          <div className="mb-1 font-semibold text-red-400">{parseErrors.length} parse error(s)</div>
          <ul className="space-y-0.5 font-mono text-red-300">
            {parseErrors.map((err, i) => (
              <li key={`${err.line}-${err.column}-${i}`}>
                <span className="text-muted-foreground">line {err.line}:{err.column}</span> — {err.message}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
