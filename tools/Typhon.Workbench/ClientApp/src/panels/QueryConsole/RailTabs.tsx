import { useState } from 'react';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';

/**
 * Combined left rail: saved queries (persisted via safeStorage) + recent run history (in-memory bounded ring,
 * design §4.8 / AC-15 + AC-16). Tab switcher at the top; lists below. Per the design's anti-pattern guard, loading
 * a history entry pre-fills the DSL but doesn't auto-run — the user must click Re-run.
 */
export function RailTabs() {
  const [tab, setTab] = useState<'saved' | 'history'>('saved');
  return (
    <div className="flex h-full flex-col border-r border-border">
      <div className="flex border-b border-border text-xs">
        <button
          className={`flex-1 px-2 py-1 ${tab === 'saved' ? 'bg-muted font-semibold' : 'text-muted-foreground'}`}
          onClick={() => setTab('saved')}
        >
          Saved
        </button>
        <button
          className={`flex-1 px-2 py-1 ${tab === 'history' ? 'bg-muted font-semibold' : 'text-muted-foreground'}`}
          onClick={() => setTab('history')}
        >
          History
        </button>
      </div>
      <div className="flex-1 overflow-auto">{tab === 'saved' ? <SavedList /> : <HistoryList />}</div>
    </div>
  );
}

function SavedList() {
  const savedQueries = useQueryConsoleStore((s) => s.savedQueries);
  const loadSavedQuery = useQueryConsoleStore((s) => s.loadSavedQuery);
  const deleteSavedQuery = useQueryConsoleStore((s) => s.deleteSavedQuery);
  if (!savedQueries.length) {
    return <Empty hint="Click Save to keep the current query." />;
  }
  return (
    <ul className="text-xs">
      {savedQueries.map((q) => (
        <li key={q.id} className="group flex items-center justify-between border-b border-border/50 px-2 py-1 hover:bg-muted/30">
          <button className="flex-1 truncate text-left" onClick={() => loadSavedQuery(q.id)} title={q.dsl}>
            {q.name}
          </button>
          <button
            className="invisible ml-2 text-muted-foreground hover:text-foreground group-hover:visible"
            onClick={() => deleteSavedQuery(q.id)}
            title="Delete"
          >
            ×
          </button>
        </li>
      ))}
    </ul>
  );
}

function HistoryList() {
  const history = useQueryConsoleStore((s) => s.history);
  const setDslDraft = useQueryConsoleStore((s) => s.setDslDraft);
  if (!history.length) {
    return <Empty hint="Recent runs will appear here." />;
  }
  return (
    <ul className="text-xs">
      {history.map((entry, i) => (
        <li key={`${entry.ranAt}-${i}`} className="border-b border-border/50 px-2 py-1 hover:bg-muted/30">
          <button
            className="block w-full truncate text-left"
            onClick={() => setDslDraft(entry.dsl)}
            title={entry.dsl}
          >
            <div className="truncate font-mono">{entry.dsl.split('\n')[0]}</div>
            <div className="text-muted-foreground">
              {entry.errorCode ? (
                // select-text so users can copy the failing code (the rest of the history-entry row stays in
                // the panel's default no-select for drag-friendliness).
                <span className="select-text text-red-400">{entry.errorCode}</span>
              ) : (
                <>
                  {entry.rowCount} row{entry.rowCount === 1 ? '' : 's'} · {(entry.elapsedNs / 1_000_000).toFixed(1)} ms
                </>
              )}
            </div>
          </button>
        </li>
      ))}
    </ul>
  );
}

function Empty({ hint }: { hint: string }) {
  return <div className="p-3 text-xs text-muted-foreground">{hint}</div>;
}

// useSaveCurrent moved to ./useSaveCurrent.ts so this file only exports components (react-refresh constraint).
