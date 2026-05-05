import { Command } from 'cmdk';
import { useEffect, useRef, useState } from 'react';
import { buildBaseCommands } from './commands/baseCommands';
import { buildResourceHits, groupHitsByKind } from './commands/resourceCommands';
import { activateResource } from './commands/activateResource';
import { humpFilter, matchHighlight } from './camelHumpFilter';
import { useResourceIndex } from '@/hooks/useResourceIndex';
import { logInfo } from '@/stores/useLogStore';

function HighlightedLabel({ label, query }: { label: string; query: string }) {
  const positions = matchHighlight(label, query);
  if (!positions || positions.length === 0) return <>{label}</>;

  const posSet = new Set(positions);
  const segments: { text: string; hi: boolean }[] = [];
  let cur = { text: '', hi: posSet.has(0) };
  for (let i = 0; i < label.length; i++) {
    const hi = posSet.has(i);
    if (hi !== cur.hi) { segments.push(cur); cur = { text: label[i], hi }; }
    else cur.text += label[i];
  }
  segments.push(cur);

  return (
    <>
      {segments.map((seg, i) =>
        seg.hi
          ? <span key={i} className="font-semibold text-amber-400">{seg.text}</span>
          : <span key={i}>{seg.text}</span>
      )}
    </>
  );
}

interface CommandPaletteProps {
 open: boolean;
 onClose: () => void;
 anchorRef: React.RefObject<HTMLElement | null>;
}

export default function CommandPalette({ open, onClose, anchorRef }: CommandPaletteProps) {
 const commands = buildBaseCommands();
 const containerRef = useRef<HTMLDivElement>(null);
 const [value, setValue] = useState('');
 // Highlighted item value — controlled so arrow keys have an anchor even when cmdk's built-in
 // filter is disabled (shouldFilter={false} in resource mode). Without this, re-renders wipe
 // the internal selection and up/down do nothing.
 const [highlighted, setHighlighted] = useState('');
 const { index } = useResourceIndex();

 const isResourceMode = value.startsWith('#');
 const hits = isResourceMode ? buildResourceHits(index, value) : [];
 const groups = isResourceMode ? groupHitsByKind(hits) : [];

 // Re-anchor the highlight to the first visible item whenever the query or mode changes.
 useEffect(() => {
 if (isResourceMode) {
 setHighlighted(hits[0]?.id ?? '');
 } else {
 setHighlighted(commands[0]?.id ?? '');
 }
 // `commands` comes from buildBaseCommands() each render, but its identity isn't stable; its
 // contents are — keying on hits length + first id is enough to re-anchor across searches.
 // eslint-disable-next-line react-hooks/exhaustive-deps
 }, [isResourceMode, hits.length, hits[0]?.id]);

 // Close on outside click
 useEffect(() => {
 if (!open) return;
 function onClick(e: MouseEvent) {
 if (
 containerRef.current &&
 !containerRef.current.contains(e.target as Node) &&
 !(anchorRef.current?.contains(e.target as Node))
 ) {
 onClose();
 }
 }
 document.addEventListener('mousedown', onClick);
 return () => document.removeEventListener('mousedown', onClick);
 }, [open, onClose, anchorRef]);

 // Reset input on close/reopen
 useEffect(() => {
 if (!open) setValue('');
 }, [open]);

 if (!open) return null;

 return (
 <div
 ref={containerRef}
 className="absolute left-1/2 -top-1.5 z-[10000] w-[clamp(420px,calc(100vw-24rem),560px)] -translate-x-1/2
 rounded-md border border-border bg-card shadow-lg"
 >
 <Command
 className="rounded-md"
 shouldFilter={!isResourceMode}
 filter={humpFilter}
 value={highlighted}
 onValueChange={setHighlighted}
 >
 <Command.Input
 autoFocus
 value={value}
 onValueChange={setValue}
 placeholder={isResourceMode ? 'Search resources…' : 'Search commands… (try #resource)'}
 className="w-full border-b border-border bg-transparent px-3 py-2
 text-density-sm text-foreground outline-none
 placeholder:text-muted-foreground"
 onKeyDown={(e) => e.key === 'Escape' && onClose()}
 />
 <Command.List className="max-h-[320px] overflow-y-auto p-1">
 <Command.Empty className="px-3 py-4 text-center text-density-sm text-muted-foreground">
 No results
 </Command.Empty>

 {isResourceMode
 ? groups.map((group) => (
 <Command.Group
 key={group.kind}
 heading={group.kind}
 className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-[10px] [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-muted-foreground"
 >
 {group.hits.map((hit) => (
 <Command.Item
 key={hit.id}
 value={hit.id}
 onSelect={() => {
 activateResource(hit);
 onClose();
 }}
 className="flex cursor-pointer items-center rounded px-3 py-1.5
 text-density-sm text-foreground
 aria-selected:bg-accent aria-selected:text-accent-foreground"
 >
 <span className="truncate">{hit.name}</span>
 <span className="ml-auto truncate pl-3 text-[10px] text-muted-foreground">
 {hit.path.join(' / ')}
 </span>
 </Command.Item>
 ))}
 </Command.Group>
 ))
 : commands.map((cmd) => (
 <Command.Item
 key={cmd.id}
 value={cmd.id}
 keywords={[cmd.label, cmd.keywords ?? '']}
 onSelect={() => {
 logInfo(`Command: ${cmd.label}`, { id: cmd.id });
 cmd.action();
 onClose();
 }}
 className="flex cursor-pointer items-center rounded px-3 py-1.5
 text-density-sm text-foreground
 aria-selected:bg-accent aria-selected:text-accent-foreground"
 >
 <HighlightedLabel label={cmd.label} query={value} />
 </Command.Item>
 ))}
 </Command.List>
 </Command>
 </div>
 );
}
