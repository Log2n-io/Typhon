import { useState } from 'react';
import { useOptionsStore, type EditorKind } from '@/stores/useOptionsStore';

/**
 * Editor preferences form (issue #293, Phase 4b). Dropdown of editor kinds, custom-command argv
 * template (visible only when Custom is selected), and a Test button that opens a dummy file via
 * `/api/profiler/open-in-editor` to verify the launch works.
 *
 * Visual Studio is greyed out on macOS — VS for Mac was discontinued in Aug 2024.
 */
const EDITOR_KINDS: Array<{ kind: EditorKind; label: string; macOnly?: boolean; windowsOnly?: boolean }> = [
  { kind: 'vsCode', label: 'VS Code' },
  { kind: 'cursor', label: 'Cursor' },
  { kind: 'rider', label: 'Rider' },
  { kind: 'visualStudio', label: 'Visual Studio', windowsOnly: true },
  { kind: 'custom', label: 'Custom' },
];

export function EditorForm(): React.JSX.Element {
  const editor = useOptionsStore((s) => s.options.editor);
  const setEditor = useOptionsStore((s) => s.setEditor);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const os = useOptionsStore((s) => s.os);

  const [pendingKind, setPendingKind] = useState<EditorKind>(editor.kind);
  const [pendingCmd, setPendingCmd] = useState<string>(editor.customCommand);
  const [testStatus, setTestStatus] = useState<{ ok: boolean; msg: string } | null>(null);
  const dirty = pendingKind !== editor.kind || pendingCmd !== editor.customCommand;

  async function handleSave(): Promise<void> {
    setTestStatus(null);
    try {
      await setEditor({ kind: pendingKind, customCommand: pendingCmd });
    } catch (err) {
      setTestStatus({ ok: false, msg: (err as Error).message });
    }
  }

  async function handleTest(): Promise<void> {
    // Save first so the launcher uses the user's intended config.
    if (dirty) {
      await handleSave();
    }
    // Use a sentinel file the user is likely to have — README at workspace root, line 1.
    const result = await openInEditor('/_/README.md', 1);
    setTestStatus(
      result.ok
        ? { ok: true, msg: 'Editor launched. Check that the file opened.' }
        : { ok: false, msg: result.error + (result.hint ? ` — ${result.hint}` : '') },
    );
  }

  return (
    <section className="max-w-xl space-y-4">
      <header>
        <h2 className="text-[14px] font-semibold text-foreground">Editor</h2>
        <p className="mt-1 text-[12px] text-muted-foreground">
          Used by the &quot;Open in editor&quot; button on profiler spans (Source row).
        </p>
      </header>

      <label className="block">
        <span className="block text-[12px] font-medium text-foreground">Editor</span>
        <select
          value={pendingKind}
          onChange={(e) => setPendingKind(e.target.value as EditorKind)}
          className="mt-1 w-full rounded border border-border bg-background px-2 py-1 text-[12px]"
        >
          {EDITOR_KINDS.map((opt) => {
            const disabled = (opt.windowsOnly === true && os !== 'windows')
              || (opt.macOnly === true && os !== 'macos');
            const suffix = opt.windowsOnly === true && os !== 'windows'
              ? ' (Windows only — Visual Studio for Mac was discontinued)'
              : '';
            return (
              <option key={opt.kind} value={opt.kind} disabled={disabled}>
                {opt.label}{suffix}
              </option>
            );
          })}
        </select>
      </label>

      {pendingKind === 'custom' && (
        <label className="block">
          <span className="block text-[12px] font-medium text-foreground">Custom command</span>
          <input
            type="text"
            value={pendingCmd}
            onChange={(e) => setPendingCmd(e.target.value)}
            placeholder="nvim-qt --remote +{line} {file}"
            className="mt-1 w-full rounded border border-border bg-background px-2 py-1 font-mono text-[12px]"
          />
          <span className="mt-1 block text-[11px] text-muted-foreground">
            Tokens: <code>{'{file}'}</code>, <code>{'{line}'}</code>, <code>{'{column}'}</code>. Each token becomes
            a single argv element — never executed via a shell, so no injection.
          </span>
        </label>
      )}

      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={handleSave}
          disabled={!dirty}
          className="rounded border border-border bg-primary px-3 py-1 text-[12px] text-primary-foreground hover:opacity-90 disabled:opacity-50"
        >
          Save
        </button>
        <button
          type="button"
          onClick={handleTest}
          className="rounded border border-border bg-background px-3 py-1 text-[12px] hover:bg-accent"
        >
          Test
        </button>
        {testStatus && (
          <span className={`text-[12px] ${testStatus.ok ? 'text-foreground' : 'text-destructive'}`}>
            {testStatus.msg}
          </span>
        )}
      </div>
    </section>
  );
}
