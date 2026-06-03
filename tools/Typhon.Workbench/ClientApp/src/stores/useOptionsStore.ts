import { create } from 'zustand';

/**
 * Mirror of the C# `WorkbenchOptions` schema. Fields stay in sync with
 * `tools/Typhon.Workbench/Hosting/WorkbenchOptions.cs`. Adding a new category here means
 * adding the matching record + controller patch endpoint on the server.
 */
export type EditorKind = 'vsCode' | 'cursor' | 'rider' | 'visualStudio' | 'custom';

export interface EditorOptions {
  kind: EditorKind;
  customCommand: string;
}

export interface ProfilerOptions {
  workspaceRoot: string;
  /**
   * Debounce window (ms) between TimeArea pan/zoom and cross-panel consumer re-aggregation.
   * See `useProfilerViewStore.setTransientViewRange`. Clamped to [0, 5000] server-side; `0` = sync.
   */
  viewRangeDebounceMs: number;
}

export interface SchemaOptions {
  /**
   * Absolute directories searched (at priority 2, above the Workbench's own bundled binaries) for a
   * database's recorded schema assemblies on open. ADR-055 Phase 2. The server normalizes to absolute,
   * de-duplicated paths; a non-existent entry is skipped at resolution time.
   */
  directories: string[];
}

export interface WorkbenchOptions {
  editor: EditorOptions;
  profiler: ProfilerOptions;
  schema: SchemaOptions;
}

const DEFAULT_OPTIONS: WorkbenchOptions = {
  editor: { kind: 'vsCode', customCommand: '' },
  profiler: { workspaceRoot: '', viewRangeDebounceMs: 150 },
  schema: { directories: [] },
};

interface OptionsState {
  options: WorkbenchOptions;
  loaded: boolean;
  /** Operating system from `/api/system/os` — drives UI affordances (e.g., disabling VS on macOS). */
  os: 'windows' | 'macos' | 'linux' | 'other';

  /** Fetch the full options document from the server. Replaces local state. */
  fetch: () => Promise<void>;
  /** Patch the editor category. Optimistic update with rollback on HTTP error. */
  setEditor: (editor: EditorOptions) => Promise<void>;
  /** Patch the profiler category. Optimistic with rollback. */
  setProfiler: (profiler: ProfilerOptions) => Promise<void>;
  /** Patch the schema category (registered schema directories). Optimistic with rollback. */
  setSchema: (schema: SchemaOptions) => Promise<void>;
  /** Trigger an editor-launch via the server. Returns the structured result. */
  openInEditor: (file: string, line: number, column?: number) => Promise<OpenInEditorResult>;
}

export interface OpenInEditorResult {
  ok: boolean;
  error: string;
  hint: string;
}

/**
 * Module-level guard so we open at most one SSE connection per page lifetime even if `fetch()` is
 * called multiple times (e.g., a re-mount). EventSource is browser-only; in test environments
 * (`typeof EventSource === 'undefined'`) the subscription is silently skipped.
 */
let _optionsStreamHandle: EventSource | null = null;

function ensureOptionsStreamSubscription(set: (partial: Partial<OptionsState>) => void): void {
  if (typeof EventSource === 'undefined') return;
  if (_optionsStreamHandle) return;
  const es = new EventSource('/api/options/stream');
  // Server emits typed `options-changed` SSE events (#308); listen by name rather than the default
  // `message` channel so the wire format matches the rest of the Workbench's typed-event streams.
  es.addEventListener('options-changed', (event: MessageEvent) => {
    try {
      const next = JSON.parse(event.data) as WorkbenchOptions;
      set({ options: next });
    } catch {
      // Malformed frame — ignore. Server-side serializer is the source of truth; a parse failure
      // here is a developer-time bug, not a runtime user concern.
    }
  });
  es.onerror = () => {
    // Browser auto-reconnects on transient failures; reset the handle on permanent close so a
    // future fetch() can re-subscribe.
    if (es.readyState === EventSource.CLOSED) {
      _optionsStreamHandle = null;
    }
  };
  _optionsStreamHandle = es;
}

export const useOptionsStore = create<OptionsState>()((set, get) => ({
  options: DEFAULT_OPTIONS,
  loaded: false,
  os: 'other',

  fetch: async () => {
    const [optsResp, osResp] = await Promise.all([
      fetch('/api/options'),
      fetch('/api/system/os'),
    ]);
    if (optsResp.ok) {
      const opts = (await optsResp.json()) as WorkbenchOptions;
      set({ options: opts, loaded: true });
    }
    if (osResp.ok) {
      const osInfo = (await osResp.json()) as { os: 'windows' | 'macos' | 'linux' | 'other' };
      set({ os: osInfo.os });
    }
    // Subscribe to out-of-band changes (file edited by hand, another Workbench window PATCHing).
    // EventSource lifetime tied to the page; the SSE handler closes server-side on disconnect.
    ensureOptionsStreamSubscription(set);
  },

  setEditor: async (editor) => {
    const prev = get().options;
    set({ options: { ...prev, editor } });
    try {
      const resp = await fetch('/api/options/editor', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(editor),
      });
      if (!resp.ok) {
        set({ options: prev });
        throw new Error(`PATCH /api/options/editor failed: ${resp.status}`);
      }
      const updated = (await resp.json()) as WorkbenchOptions;
      set({ options: updated });
    } catch (err) {
      set({ options: prev });
      throw err;
    }
  },

  setProfiler: async (profiler) => {
    const prev = get().options;
    set({ options: { ...prev, profiler } });
    try {
      const resp = await fetch('/api/options/profiler', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(profiler),
      });
      if (!resp.ok) {
        set({ options: prev });
        throw new Error(`PATCH /api/options/profiler failed: ${resp.status}`);
      }
      const updated = (await resp.json()) as WorkbenchOptions;
      set({ options: updated });
    } catch (err) {
      set({ options: prev });
      throw err;
    }
  },

  setSchema: async (schema) => {
    const prev = get().options;
    set({ options: { ...prev, schema } });
    try {
      const resp = await fetch('/api/options/schema', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(schema),
      });
      if (!resp.ok) {
        set({ options: prev });
        throw new Error(`PATCH /api/options/schema failed: ${resp.status}`);
      }
      // Server normalizes (absolute + de-duplicated) — adopt its canonical list rather than our optimistic one.
      const updated = (await resp.json()) as WorkbenchOptions;
      set({ options: updated });
    } catch (err) {
      set({ options: prev });
      throw err;
    }
  },

  openInEditor: async (file, line, column) => {
    const resp = await fetch('/api/profiler/open-in-editor', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ file, line, column: column ?? null }),
    });
    if (!resp.ok) {
      return {
        ok: false,
        error: `HTTP ${resp.status}: ${resp.statusText}`,
        hint: '',
      };
    }
    return (await resp.json()) as OpenInEditorResult;
  },
}));
