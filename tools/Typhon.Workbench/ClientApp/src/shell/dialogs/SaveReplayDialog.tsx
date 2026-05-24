import { useEffect, useMemo, useState } from 'react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import FileBrowser from '@/shell/components/FileBrowser';
import { usePostApiSessionsSessionIdProfilerSaveReplay } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import { logError, logInfo } from '@/stores/useLogStore';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const REPLAY_EXT = '.typhon-replay';

/**
 * Save-replay dialog — appears via File → Save Session as .typhon-replay… on a live attach session. Browses the filesystem
 * via the same FileBrowser component used elsewhere; the user picks a target directory + types a filename. The save
 * endpoint serializes the live session into a self-contained `.typhon-replay` file at that path.
 *
 * Auto-appends the `.typhon-replay` extension to the filename if absent so the user doesn't have to type it (and it never
 * appears as a directory in the resulting file dialog filter).
 */
export default function SaveReplayDialog({ open, onOpenChange }: Props) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);

  const [currentDir, setCurrentDir] = useState<string | null>(null);
  const [filename, setFilename] = useState<string>('session.typhon-replay');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const saveReplay = usePostApiSessionsSessionIdProfilerSaveReplay();

  // Reset transient state every time the dialog opens. Keeps an old error pill or stale filename from haunting the next save.
  useEffect(() => {
    if (open) {
      setErrorMessage(null);
      setFilename('session.typhon-replay');
    }
  }, [open]);

  const targetPath = useMemo(() => {
    if (!currentDir || filename.trim().length === 0) return null;
    const trimmedName = filename.trim();
    const withExt = trimmedName.toLowerCase().endsWith(REPLAY_EXT) ? trimmedName : trimmedName + REPLAY_EXT;
    // Path separator is platform-dependent. Use the same separator the current dir already uses (heuristic: backslash on Windows,
    // forward slash on POSIX). The backend re-normalises via Path.GetFullPath either way, so this is purely cosmetic.
    const sep = currentDir.includes('\\') ? '\\' : '/';
    const dir = currentDir.endsWith(sep) ? currentDir.slice(0, -1) : currentDir;
    return `${dir}${sep}${withExt}`;
  }, [currentDir, filename]);

  const canSave = sessionId !== null && sessionKind === 'attach' && targetPath !== null && !saveReplay.isPending;

  const handleSave = async () => {
    if (!sessionId || !targetPath) return;
    setErrorMessage(null);
    try {
      const response = await saveReplay.mutateAsync({ sessionId, data: { path: targetPath } });
      const dto = response.data;
      logInfo(`Saved live session to ${dto.path}`, { sessionId, path: dto.path, bytesWritten: dto.bytesWritten });
      onOpenChange(false);
    } catch (err) {
      const detail = err && typeof err === 'object' && 'detail' in err
        ? String((err as Record<string, unknown>).detail ?? '')
        : '';
      const msg = detail || String(err);
      setErrorMessage(msg);
      logError(`Failed to save replay`, { sessionId, path: targetPath, error: msg });
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex h-[640px] max-w-3xl flex-col gap-3">
        <DialogHeader>
          <DialogTitle>Save Session as .typhon-replay…</DialogTitle>
          <DialogDescription className="text-fs-lg">
            Snapshot the current live attach session into a self-contained replay file. Reopens directly with no companion
            <code className="mx-1">.typhon-trace</code> required.
          </DialogDescription>
        </DialogHeader>

        <div className="flex min-h-0 flex-col gap-1">
          <label className="shrink-0 text-fs-lg text-muted-foreground">Target directory</label>
          <div className="min-h-0 flex-1">
            <FileBrowser
              extensionFilter={[REPLAY_EXT]}
              onPathChange={setCurrentDir}
              onSelectionChange={(paths) => {
                // Clicking an existing .typhon-replay populates the filename so "save over" / "save next to" is one click.
                if (paths.length === 1) {
                  const last = paths[0];
                  const sepIdx = Math.max(last.lastIndexOf('\\'), last.lastIndexOf('/'));
                  if (sepIdx >= 0) setFilename(last.slice(sepIdx + 1));
                }
              }}
            />
          </div>
        </div>

        <div className="flex shrink-0 flex-col gap-1">
          <label className="text-fs-lg text-muted-foreground">Filename</label>
          <Input
            value={filename}
            onChange={(e) => setFilename(e.target.value)}
            spellCheck={false}
            autoComplete="off"
            className="font-mono text-fs-base"
            placeholder="session.typhon-replay"
          />
        </div>

        {targetPath && (
          <p className="shrink-0 truncate text-fs-xs text-muted-foreground" title={targetPath}>
            Will save: <span className="font-mono text-foreground">{targetPath}</span>
          </p>
        )}

        {errorMessage && (
          <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
            {errorMessage}
          </p>
        )}

        <DialogFooter className="shrink-0">
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={saveReplay.isPending}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={!canSave}>
            {saveReplay.isPending ? 'Saving…' : 'Save'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
