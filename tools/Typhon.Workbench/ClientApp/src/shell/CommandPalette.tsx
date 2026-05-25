import { Command } from 'cmdk';
import { useEffect, useRef, useState } from 'react';
import { buildBaseCommands } from './commands/baseCommands';
import { buildResourceHits, groupHitsByKind } from './commands/resourceCommands';
import { activateResource } from './commands/activateResource';
import { parsePaletteMode, parseJump, PALETTE_PREFIX_HELP } from './commands/paletteRouting';
import { buildObjectHits, type ObjectHit } from './commands/objectHits';
import { humpFilter, matchHighlight } from './camelHumpFilter';
import { useResourceIndex } from '@/hooks/useResourceIndex';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useQueryDefinitions } from '@/panels/QueryAnalyzer/useQueryDefinitions';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
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

const itemClass =
  'flex cursor-pointer items-center rounded px-3 py-1.5 text-fs-lg text-foreground ' +
  'aria-selected:bg-accent aria-selected:text-accent-foreground';
const groupClass =
  '[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-fs-xs ' +
  '[&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-muted-foreground';

export default function CommandPalette({ open, onClose, anchorRef }: CommandPaletteProps) {
 const commands = buildBaseCommands();
 const containerRef = useRef<HTMLDivElement>(null);
 const [value, setValue] = useState('');
 const [highlighted, setHighlighted] = useState('');

 const { index } = useResourceIndex();
 // Object-resolution sources. The hooks are gated internally by session id/kind; buildObjectHits then
 // filters by the current kind so inapplicable groups never surface (e.g. no Systems in an Open session).
 const { list: components } = useComponentList();
 const { list: archetypes } = useArchetypeList();
 const { definitions: queries } = useQueryDefinitions();
 const systems = useProfilerSessionStore((s) => s.metadata?.systems) ?? [];
 const select = useSelectionStore((s) => s.select);
 const kind = useSessionStore((s) => s.kind);

 const route = parsePaletteMode(value);
 const isObjectMode = route.mode === 'object-session' || route.mode === 'object-global';

 const resourceHits = isObjectMode ? buildResourceHits(index, route.query) : [];
 const resourceGroups = groupHitsByKind(resourceHits);
 const objectHits: ObjectHit[] = isObjectMode
   ? buildObjectHits(
       route.query,
       {
         components: components.map((c) => ({ typeName: c.typeName })),
         archetypes: archetypes.map((a) => ({ archetypeId: a.archetypeId, componentTypes: a.componentTypes })),
         systems,
         queries,
       },
       kind,
     )
   : [];
 const objectGroups = groupObjectHits(objectHits);
 const jump = route.mode === 'jump' ? parseJump(route.query) : null;

 // Re-anchor the highlight to the first visible item whenever the query/mode changes.
 const firstId = isObjectMode
   ? (resourceHits[0]?.id ?? objectHits[0]?.id ?? '')
   : (commands[0]?.id ?? '');
 useEffect(() => {
   setHighlighted(firstId);
   // eslint-disable-next-line react-hooks/exhaustive-deps
 }, [route.mode, firstId]);

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

 const selectObject = (hit: ObjectHit) => {
   select(hit.type, hit.ref);
   logInfo(`Go to ${hit.type}: ${hit.label}`, { id: hit.id });
   onClose();
 };

 const placeholder =
   route.mode === 'help'
     ? 'Prefixes: > action · @/# object · : jump'
     : isObjectMode
       ? 'Go to an object…'
       : route.mode === 'jump'
         ? 'Jump… (:tick 8412 · :page 1024)'
         : 'Search commands… (try > @ # : ?)';

 return (
 <div
 ref={containerRef}
 className="absolute left-1/2 -top-1.5 z-[10000] w-[clamp(420px,calc(100vw-24rem),560px)] -translate-x-1/2
 rounded-md border border-border bg-card shadow-lg"
 >
 <Command
 className="rounded-md"
 shouldFilter={route.mode === 'command' || route.mode === 'action'}
 // cmdk filters against the raw input value, which still carries the mode prefix (e.g. ">open"); match
 // on the prefix-stripped `route.query` instead, so the explicit ">" action mode filters commands the
 // same as the bare command mode. (Without this, ">open" matches nothing — the ">" breaks every score.)
 filter={(_value, _search, keywords) => humpFilter(_value, route.query, keywords)}
 value={highlighted}
 onValueChange={setHighlighted}
 >
 <Command.Input
 autoFocus
 value={value}
 onValueChange={setValue}
 placeholder={placeholder}
 className="w-full border-b border-border bg-transparent px-3 py-2
 text-fs-lg text-foreground outline-none
 placeholder:text-muted-foreground"
 onKeyDown={(e) => e.key === 'Escape' && onClose()}
 />
 <Command.List className="max-h-[320px] overflow-y-auto p-1">
 <Command.Empty className="px-3 py-4 text-center text-fs-lg text-muted-foreground">
 No results
 </Command.Empty>

 {route.mode === 'help' && (
   <div className="px-3 py-2 text-fs-lg">
     {PALETTE_PREFIX_HELP.map((h) => (
       <div key={h.prefix} className="flex gap-3 py-0.5">
         <code className="w-16 shrink-0 text-amber-400">{h.prefix}</code>
         <span className="text-muted-foreground">{h.meaning}</span>
       </div>
     ))}
   </div>
 )}

 {route.mode === 'jump' && jump && (
   <Command.Item
     value="jump-target"
     onSelect={() => {
       if (jump.kind === 'tick') select('tick', jump.value);
       else select('page', { kind: 'page', pageIndex: jump.value });
       onClose();
     }}
     className={itemClass}
   >
     Jump to {jump.kind} {jump.value}
   </Command.Item>
 )}

 {isObjectMode && resourceGroups.map((group) => (
   <Command.Group key={`res:${group.kind}`} heading={group.kind} className={groupClass}>
     {group.hits.map((hit) => (
       <Command.Item
         key={hit.id}
         value={hit.id}
         onSelect={() => { activateResource(hit); onClose(); }}
         className={itemClass}
       >
         <span className="truncate">{hit.name}</span>
         <span className="ml-auto truncate pl-3 text-fs-xs text-muted-foreground">{hit.path.join(' / ')}</span>
       </Command.Item>
     ))}
   </Command.Group>
 ))}

 {isObjectMode && objectGroups.map((group) => (
   <Command.Group key={`obj:${group.group}`} heading={group.group} className={groupClass}>
     {group.hits.map((hit) => (
       <Command.Item key={hit.id} value={hit.id} onSelect={() => selectObject(hit)} className={itemClass}>
         <span className="truncate">{hit.label}</span>
         {hit.sublabel && <span className="ml-auto truncate pl-3 text-fs-xs text-muted-foreground">{hit.sublabel}</span>}
       </Command.Item>
     ))}
   </Command.Group>
 ))}

 {(route.mode === 'command' || route.mode === 'action') &&
   commands.map((cmd) => (
   <Command.Item
   key={cmd.id}
   value={cmd.id}
   keywords={[cmd.label, cmd.keywords ?? '']}
   onSelect={() => {
   logInfo(`Command: ${cmd.label}`, { id: cmd.id });
   cmd.action();
   onClose();
   }}
   className={itemClass}
   >
   <HighlightedLabel label={cmd.label} query={route.query} />
   </Command.Item>
   ))}
 </Command.List>
 </Command>
 </div>
 );
}

interface ObjectGroupRender {
  group: ObjectHit['group'];
  hits: ObjectHit[];
}

/** Group object hits by their display group, preserving the order buildObjectHits emitted. */
function groupObjectHits(hits: ObjectHit[]): ObjectGroupRender[] {
  const out: ObjectGroupRender[] = [];
  for (const hit of hits) {
    const last = out[out.length - 1];
    if (last && last.group === hit.group) last.hits.push(hit);
    else out.push({ group: hit.group, hits: [hit] });
  }
  return out;
}
