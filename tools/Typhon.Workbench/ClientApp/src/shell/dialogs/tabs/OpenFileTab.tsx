import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import FileBrowser from '@/shell/components/FileBrowser';

interface Props {
 onOpen: (filePath: string, schemaDllPaths: string[]) => void;
 isOpening?: boolean;
}

/**
 * Open-File dialog tab. Two entry paths for the same action:
 *   • Browse — standard file tree, extension-filtered to `.bin` / `.typhon`
 *   • Paste  — input field for an absolute path, wins if non-empty
 *
 * Schema DLLs are resolved server-side via the `*.schema.dll` convention in the database file's
 * directory. No manual picker — the explicit picker was a common source of stale paths after
 * regenerating fixtures, and convention covers the realistic workflow.
 */
export default function OpenFileTab({ onOpen, isOpening }: Props) {
 const [selectedPath, setSelectedPath] = useState<string | null>(null);
 const [pastedPath, setPastedPath] = useState<string>('');

 // Pasted path wins when non-empty (explicit user intent). Otherwise fall back to tree selection.
 // Strip surrounding double quotes — Windows Explorer's "Copy as path" wraps the path in them.
 const pastedTrimmed = pastedPath.trim().replace(/^"(.*)"$/, '$1');
 const effectivePath = pastedTrimmed.length > 0 ? pastedTrimmed : selectedPath;
 const canOpen = !!effectivePath && !isOpening;

 return (
 <div className="flex h-full flex-col gap-3">
 <div className="flex min-h-0 flex-col gap-1">
 <label className="shrink-0 text-fs-lg text-muted-foreground">Database file</label>
 <div className="min-h-0 flex-1">
 <FileBrowser
 extensionFilter={['.typhon']}
 recentKind="db"
 onSelectionChange={(paths) => setSelectedPath(paths[0] ?? null)}
 onActivate={(p) => setSelectedPath(p)}
 />
 </div>
 </div>

 <div className="flex shrink-0 flex-col gap-1">
 <label className="text-fs-lg text-muted-foreground">
 Or paste absolute path
 </label>
 <Input
 placeholder="C:\path\to\database.typhon"
 value={pastedPath}
 onChange={(e) => setPastedPath(e.target.value)}
 spellCheck={false}
 autoComplete="off"
 className="font-mono text-fs-base"
 />
 </div>

 {effectivePath && (
 <p className="shrink-0 truncate text-fs-xs text-muted-foreground" title={effectivePath}>
 Will open: <span className="font-mono text-foreground">{effectivePath}</span>
 </p>
 )}

 <p className="shrink-0 text-fs-xs text-muted-foreground">
 Schema assemblies are recorded inside the database and auto-located next to the file by assembly name.
 </p>

 <div className="flex shrink-0 justify-end gap-2">
 <Button
 onClick={() => effectivePath && onOpen(effectivePath, [])}
 disabled={!canOpen}
 className="text-fs-lg"
 >
 {isOpening ? 'Opening…' : 'Open'}
 </Button>
 </div>
 </div>
 );
}
