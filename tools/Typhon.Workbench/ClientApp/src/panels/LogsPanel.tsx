import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, Info, Trash2, XCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge';
import { useLogStore, type LogEntry, type LogLevel } from '@/stores/useLogStore';

const LEVELS: LogLevel[] = ['info', 'warn', 'error'];

/**
 * Logs panel — Workbench's unified log surface. Today it renders only client-side
 * <c>workbench-ui</c> entries (session open, palette commands, query failures); server-side +
 * attached-app streams plug into the same store in future phases. See
 * <c>claude/design/typhon-workbench/modules/05-logs.md</c>.
 */
export default function LogsPanel() {
  const entries = useLogStore((s) => s.entries);
  const clear = useLogStore((s) => s.clear);

  const [enabledLevels, setEnabledLevels] = useState<Set<LogLevel>>(
    () => new Set(LEVELS),
  );
  const [filterText, setFilterText] = useState('');
  const [followTail, setFollowTail] = useState(true);

  const filtered = useMemo(() => {
    const q = filterText.trim().toLowerCase();
    const matched = entries.filter((e) => {
      if (!enabledLevels.has(e.level)) return false;
      if (q.length === 0) return true;
      return e.message.toLowerCase().includes(q) || e.source.includes(q);
    });
    // Newest first: the store appends chronologically, reverse for display so the latest entry
    // sits at the top where the eye lands. Follow-tail below scrolls to top (the new edge).
    return matched.slice().reverse();
  }, [entries, enabledLevels, filterText]);

  const listRef = useRef<HTMLDivElement | null>(null);

  // Auto-scroll to top when new entries arrive and follow mode is on. With newest-first order,
  // the "tail" is at the top of the scroll container.
  useEffect(() => {
    if (!followTail || !listRef.current) return;
    listRef.current.scrollTop = 0;
  }, [filtered, followTail]);

  const toggleLevel = (level: LogLevel) => {
    setEnabledLevels((prev) => {
      const next = new Set(prev);
      if (next.has(level)) {
        next.delete(level);
      } else {
        next.add(level);
      }
      return next;
    });
  };

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="wb-pane-header flex items-center gap-1.5 border-b border-border px-2 py-1.5">
        {LEVELS.map((level) => (
          <LevelChip
            key={level}
            level={level}
            active={enabledLevels.has(level)}
            count={entries.filter((e) => e.level === level).length}
            onClick={() => toggleLevel(level)}
          />
        ))}
        <Input
          placeholder="Filter… (message or source)"
          value={filterText}
          onChange={(e) => setFilterText(e.target.value)}
          className="h-6 flex-1 text-fs-sm"
        />
        <Button
          size="sm"
          variant="ghost"
          className={`h-6 px-2 text-fs-xs ${followTail ? 'text-foreground' : 'text-muted-foreground'}`}
          onClick={() => setFollowTail((v) => !v)}
          title={followTail ? 'Following tail' : 'Paused'}
        >
          {followTail ? 'Follow' : 'Paused'}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          className="h-6 w-6 p-0"
          onClick={clear}
          title="Clear logs"
          disabled={entries.length === 0}
        >
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </div>

      {/* select-text re-enables text selection that the app-level select-none disables — users
           need to copy log lines (timestamps, sources, stacktrace details) for reporting. */}
      <div ref={listRef} className="flex-1 select-text overflow-auto font-mono text-fs-sm">
        {filtered.length === 0 && (
          <p className="p-3 text-muted-foreground">
            {entries.length === 0 ? 'No log entries yet.' : 'No entries match the filter.'}
          </p>
        )}
        {filtered.map((e) => (
          <LogRow key={e.id} entry={e} />
        ))}
      </div>

      <div className="flex items-center justify-between border-t border-border px-2 py-1 text-fs-xs text-muted-foreground">
        <span>
          {filtered.length} shown · {entries.length} total
        </span>
      </div>
    </div>
  );
}

function LevelChip({
  level,
  active,
  count,
  onClick,
}: {
  level: LogLevel;
  active: boolean;
  count: number;
  onClick: () => void;
}) {
  const Icon = level === 'info' ? Info : level === 'warn' ? AlertTriangle : XCircle;
  const tone: StatusTone = level === 'error' ? 'error' : level === 'warn' ? 'warn' : 'neutral';
  return (
    <StatusBadge tone={tone} muted={!active} onClick={onClick}>
      <Icon className="mr-1 h-3 w-3" />
      {level}
      <span className="ml-1 tabular-nums">{count}</span>
    </StatusBadge>
  );
}

function LogRow({ entry }: { entry: LogEntry }) {
  const [expanded, setExpanded] = useState(false);
  const ts = formatTime(entry.timestamp);
  const levelColor =
    entry.level === 'error'
      ? 'text-destructive'
      : entry.level === 'warn'
        ? 'text-amber-400'
        : 'text-sky-400';
  const hasDetails = entry.details !== undefined && entry.details !== null;

  return (
    <div className="border-b border-border/40 px-2 py-0.5 hover:bg-accent/30">
      <div
        className={`flex items-baseline gap-2 ${hasDetails ? 'cursor-pointer' : ''}`}
        onClick={() => hasDetails && setExpanded((v) => !v)}
      >
        <span className="shrink-0 text-muted-foreground">{ts}</span>
        <span className={`shrink-0 w-12 uppercase ${levelColor}`}>{entry.level}</span>
        <span className="shrink-0 text-muted-foreground">[{entry.source}]</span>
        <span className="min-w-0 flex-1 whitespace-pre-wrap break-words text-foreground">
          {entry.message}
        </span>
        {hasDetails && (
          <span className="shrink-0 text-muted-foreground">
            {expanded ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
          </span>
        )}
      </div>
      {hasDetails && expanded && (
        <pre className="mt-1 ml-20 overflow-auto rounded bg-muted/20 p-1.5 text-fs-xs text-muted-foreground">
          {formatDetails(entry.details)}
        </pre>
      )}
    </div>
  );
}

function formatTime(ts: number): string {
  const d = new Date(ts);
  const hh = d.getHours().toString().padStart(2, '0');
  const mm = d.getMinutes().toString().padStart(2, '0');
  const ss = d.getSeconds().toString().padStart(2, '0');
  const ms = d.getMilliseconds().toString().padStart(3, '0');
  return `${hh}:${mm}:${ss}.${ms}`;
}

function formatDetails(details: unknown): string {
  try {
    return JSON.stringify(details, null, 2);
  } catch {
    return String(details);
  }
}
