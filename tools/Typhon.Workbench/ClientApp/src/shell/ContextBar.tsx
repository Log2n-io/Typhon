import { useEffect, useRef, useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useHeartbeat } from '@/hooks/streams/useHeartbeat';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { resolveChain, type SelectionRef } from '@/stores/selectionChain';
import { useEnvTagStore, ENV_TAG_STYLE, type EnvTag } from '@/stores/useEnvTagStore';

const ENV_OPTIONS: EnvTag[] = ['none', 'dev', 'staging', 'prod'];
const KIND_LABEL: Record<string, string> = { open: 'Open', trace: 'Trace', attach: 'Attach', none: '—' };

/** Compact µs→ms readout for the trace/attach time-window scope. */
function fmtMs(us: number): string {
  return `${(us / 1000).toFixed(1)}ms`;
}

/** Best-effort short label for a breadcrumb crumb (primitive ref, or a known rich-ref field). */
function crumbLabel(node: { type: string; ref: unknown }): string {
  const { ref } = node;
  if (typeof ref === 'string' || typeof ref === 'number') {
    return String(ref);
  }
  if (ref !== null && typeof ref === 'object') {
    const r = ref as Record<string, unknown>;
    if ('field' in r) return String(r.field);
    if ('entityId' in r) return String(r.entityId);
    if ('name' in r) return String(r.name);
    if ('localId' in r) return `#${String(r.localId)}`;
  }
  return node.type;
}

/**
 * Global Context Bar (zone B) — the always-on "where am I" strip beneath the menu bar (context-bar.md):
 * session **identity** + environment tag, the global **scope** (revision for Open, time window for
 * Trace/Attach), and a **breadcrumb** mirroring the Inspector containment chain off the unified bus.
 * Each crumb navigates the bus up the chain. New in Stage 1 (#373), zone B.
 */
export default function ContextBar() {
  const kind = useSessionStore((s) => s.kind);
  const filePath = useSessionStore((s) => s.filePath);
  const { status } = useHeartbeat();
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const leaf = useSelectionStore((s) => s.leaf);
  const select = useSelectionStore((s) => s.select);
  const envTag = useEnvTagStore((s) => s.get(filePath));
  const setEnvTag = useEnvTagStore((s) => s.set);

  const connected = kind !== 'none';
  const fileLabel = filePath ? (filePath.split(/[\\/]/).pop() ?? filePath) : kind;
  const isProfiler = kind === 'trace' || kind === 'attach';
  const dotColor = connected && status === 'green' ? 'bg-green-500' : 'bg-muted-foreground';

  // Breadcrumb = containment ancestors (root → parent) then the leaf itself, all clickable.
  const crumbs: SelectionRef[] = leaf
    ? [...resolveChain(leaf, useSelectionStore.getState()), { type: leaf.type, ref: leaf.ref }]
    : [];

  const tone = ENV_TAG_STYLE[envTag];

  return (
    <div
      className={
        'flex h-[26px] shrink-0 items-center gap-2 border-b border-border bg-card px-3 ' +
        'text-fs-base text-muted-foreground ' + tone.barTint
      }
    >
      {/* Identity */}
      <span className={`h-2 w-2 shrink-0 rounded-full ${dotColor}`} aria-hidden="true" />
      <span className="max-w-[28ch] truncate font-medium text-foreground" title={filePath ?? undefined}>
        {fileLabel}
      </span>

      {/* Environment tag */}
      <EnvTagPicker
        value={envTag}
        disabled={!filePath}
        onChange={(t) => filePath && setEnvTag(filePath, t)}
      />

      <span aria-hidden="true">·</span>
      <span>{KIND_LABEL[kind] ?? kind}</span>

      {/* Scope */}
      <span aria-hidden="true">·</span>
      {isProfiler ? (
        viewRange.endUs > viewRange.startUs ? (
          <span className="tabular-nums" title="Time window (global scope)">
            ⟮ {fmtMs(viewRange.startUs)}–{fmtMs(viewRange.endUs)} ⟯
          </span>
        ) : (
          <span title="Full trace (no window selected)">full trace</span>
        )
      ) : (
        <span title="Read revision (HEAD until a revision counter exists)">@HEAD</span>
      )}

      {/* Breadcrumb (drill path) */}
      {crumbs.length > 0 && (
        <nav className="ml-auto flex min-w-0 items-center gap-1" aria-label="Selection breadcrumb">
          {crumbs.map((c, i) => (
            <span key={`${c.type}:${String(c.ref)}`} className="flex min-w-0 items-center gap-1">
              {i > 0 && <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground" />}
              <button
                type="button"
                onClick={() => select(c.type, c.ref)}
                title={`${c.type}: ${crumbLabel(c)}`}
                className={
                  'truncate rounded px-1 hover:bg-muted/60 hover:text-foreground ' +
                  (i === crumbs.length - 1 ? 'text-foreground' : 'text-muted-foreground')
                }
              >
                {crumbLabel(c)}
              </button>
            </span>
          ))}
        </nav>
      )}
    </div>
  );
}

/** Env-tag chip + a small click-to-pick menu (none/dev/staging/prod), persisted per file. */
function EnvTagPicker({
  value,
  disabled,
  onChange,
}: {
  value: EnvTag;
  disabled: boolean;
  onChange: (t: EnvTag) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onClick);
    return () => document.removeEventListener('mousedown', onClick);
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        disabled={disabled}
        onClick={() => setOpen((o) => !o)}
        title={disabled ? 'Open a file to tag its environment' : 'Set environment tag'}
        className={`rounded px-1.5 py-0.5 text-fs-xs font-semibold tracking-wide ${ENV_TAG_STYLE[value].chip} disabled:opacity-50`}
      >
        {ENV_TAG_STYLE[value].label}
      </button>
      {open && (
        <div className="absolute left-0 top-full z-[10000] mt-1 min-w-[7rem] rounded-md border border-border bg-card p-1 shadow-lg">
          {ENV_OPTIONS.map((opt) => (
            <button
              key={opt}
              type="button"
              onClick={() => {
                onChange(opt);
                setOpen(false);
              }}
              className={
                'flex w-full items-center gap-2 rounded px-2 py-1 text-left text-fs-sm hover:bg-muted/60 ' +
                (opt === value ? 'text-foreground' : 'text-muted-foreground')
              }
            >
              <span className={`h-2 w-2 rounded-full ${ENV_TAG_STYLE[opt].chip}`} aria-hidden="true" />
              {opt === 'none' ? 'No tag' : ENV_TAG_STYLE[opt].label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
