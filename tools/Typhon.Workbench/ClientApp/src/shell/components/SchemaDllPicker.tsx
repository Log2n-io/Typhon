import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import FileBrowser from './FileBrowser';

interface Props {
 paths: string[];
 onChange: (paths: string[]) => void;
 /** Optional directory to open the browser in (e.g., directory of selected .typhon file). */
 initialPath?: string;
 /** Called when the user clicks "Auto-detect" — typically wipes manual selection so convention kicks in server-side. */
 onAutoDetect?: () => void;
}

export default function SchemaDllPicker({ paths, onChange, initialPath, onAutoDetect }: Props) {
 const remove = (p: string) => onChange(paths.filter((x) => x !== p));

 return (
 <div className="flex h-full flex-col gap-2">
 <div className="flex shrink-0 items-center justify-between">
 <span className="text-fs-lg text-muted-foreground">
 Schema DLLs {paths.length > 0 && <span className="opacity-70">({paths.length})</span>}
 </span>
 {onAutoDetect && (
 <Button
 variant="outline"
 size="sm"
 className="h-6 text-fs-sm"
 onClick={onAutoDetect}
 >
 Auto-detect
 </Button>
 )}
 </div>

 {paths.length > 0 && (
 <div className="flex shrink-0 flex-wrap gap-1">
 {paths.map((p) => {
 const name = p.split(/[\\/]/).pop() ?? p;
 return (
 <span
 key={p}
 className="inline-flex items-center gap-1 rounded border border-border bg-muted
 px-2 py-0.5 text-fs-xs"
 title={p}
 >
 {name}
 <button
 onClick={() => remove(p)}
 className="text-muted-foreground hover:text-foreground"
 aria-label={`Remove ${name}`}
 >
 <X className="h-3 w-3" />
 </button>
 </span>
 );
 })}
 </div>
 )}

 <div className="min-h-0 flex-1">
 <FileBrowser
 extensionFilter={['.schema.dll']}
 recentKind="db"
 multiSelect
 initialPath={initialPath}
 onSelectionChange={onChange}
 />
 </div>
 </div>
 );
}
