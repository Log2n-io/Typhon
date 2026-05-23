import {
 Activity,
 BarChart2,
 Clock,
 Cpu,
 Database,
 FileBox,
 FileText,
 FolderOpen,
 Gauge,
 HardDrive,
 Layers,
 Pin,
 RefreshCw,
 ScrollText,
 Search,
 Table2,
} from 'lucide-react';
import { createContext, useCallback, useContext, useDeferredValue, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Tree, type NodeApi, type NodeRendererProps, type TreeApi } from 'react-arborist';
import { useDensityRowHeight } from '@/hooks/useDensityRowHeight';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import type { ResourceNodeDto } from '@/api/generated/model';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectedResourceStore } from '@/stores/useSelectedResourceStore';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { useResourceIndex } from '@/hooks/useResourceIndex';
import { useResourceGraphStream } from '@/hooks/streams/useResourceGraphStream';
import { activateResource } from '@/shell/commands/activateResource';
import ResourceTreeContextMenu from './ResourceTreeContextMenu';

interface ResourceNode {
 // Synthetic path-based uid — guaranteed unique even when the engine reports duplicate natural
 // ids (e.g., multiple StagingBufferPool entries). react-arborist requires globally unique ids.
 id: string;
 // Natural id from the engine, surfaced in the Detail panel.
 naturalId: string;
 name: string;
 type: string;
 entityCount?: number;
 children?: ResourceNode[];
}

// Maps engine ResourceType values (PascalCase) to Lucide icons. Unknown types fall back to FolderOpen.
const TYPE_ICONS: Record<string, React.ElementType> = {
 // Engine (PascalCase from ResourceType enum)
 Node: FolderOpen,
 Service: Cpu,
 Engine: Database,
 TransactionPool: Activity,
 Transaction: Activity,
 ChangeSet: Activity,
 ComponentTable: Table2,
 Segment: FileBox,
 Index: Search,
 Cache: HardDrive,
 File: FileText,
 Memory: HardDrive,
 Bitmap: Layers,
 Schema: Layers,
 Allocator: HardDrive,
 Synchronization: Clock,
 WAL: FileText,
 Checkpoint: BarChart2,
 Backup: ScrollText,
 // Legacy lowercase kinds (kept for any remaining fixture consumers)
 database: Database,
 group: FolderOpen,
 'component-table': Table2,
 archetype: Layers,
 index: Search,
 storage: HardDrive,
 segment: FileBox,
 wal: FileText,
 transactions: Activity,
 'unit-of-work': Activity,
 scheduler: Clock,
 system: Cpu,
 diagnostics: HardDrive,
 logs: ScrollText,
 profiler: BarChart2,
 gauges: Gauge,
};

function toResourceNode(dto: ResourceNodeDto, parentUid = '', siblingIndex = 0): ResourceNode {
 const natural = dto.id ?? '';
 const uid = `${parentUid}/${siblingIndex}:${natural}`;
 // depth=all on the server always returns children as an array — empty for leaves. react-arborist
 // treats any non-undefined children as a folder and renders a toggle chevron, so collapse empty
 // arrays back to undefined to mark true leaves.
 const kids = dto.children as ResourceNodeDto[] | null | undefined;
 const children = kids && kids.length > 0 ? kids.map((c, i) => toResourceNode(c, uid, i)) : undefined;
 return {
 id: uid,
 naturalId: natural,
 name: dto.name ?? '',
 type: dto.type ?? 'Node',
 entityCount: dto.entityCount != null ? Number(dto.entityCount) : undefined,
 children,
 };
}

function countNodes(node: ResourceNode): number {
 return 1 + (node.children?.reduce((acc, c) => acc + countNodes(c), 0) ?? 0);
}

// Manual filter: keep any node whose name matches, plus every ancestor of a match.
// react-arborist's built-in searchTerm/searchMatch leaves too many unrelated siblings visible
// in practice — a pre-filter on the data is simpler and predictable.
function filterTree(node: ResourceNode, term: string): ResourceNode | null {
 const t = term.toLowerCase();
 const self = node.name.toLowerCase().includes(t);
 // If the node itself matches, preserve its full subtree so the user can explore below.
 if (self) return node;
 // Otherwise, keep only the descendants that are paths to matches.
 const kept = (node.children ?? [])
 .map((c) => filterTree(c, term))
 .filter((c): c is ResourceNode => c !== null);
 if (kept.length > 0) return { ...node, children: kept };
 return null;
}

/** Depth-first search for the node with the given exact name; returns it plus its root→node name path. */
function findNodeByName(
 node: ResourceNode,
 name: string,
 path: string[] = [],
): { node: ResourceNode; path: string[] } | null {
 const here = [...path, node.name];
 if (node.name === name) {
  return { node, path: here };
 }
 for (const child of node.children ?? []) {
  const hit = findNodeByName(child, name, here);
  if (hit) {
   return hit;
  }
 }
 return null;
}

function activateFromTreeNode(node: NodeApi<ResourceNode>) {
 activateResource({
 id: node.data.id,
 name: node.data.name,
 kind: node.data.type,
 path: buildPath(node),
 raw: node.data as unknown as ResourceNodeDto,
 score: 0,
 });
}

function buildPath(node: NodeApi<ResourceNode>): string[] {
 const parts: string[] = [];
 let cur: NodeApi<ResourceNode> | null = node;
 while (cur) {
 parts.unshift(cur.data.name);
 cur = cur.parent;
 }
 return parts;
}

interface NodeRowExtras {
 onReveal: (node: NodeApi<ResourceNode>) => void;
 onRefreshSubtree: () => void;
}

const NodeExtrasContext = createContext<NodeRowExtras | null>(null);

function NodeRow({ node, style, dragHandle }: NodeRendererProps<ResourceNode>) {
 const Icon = TYPE_ICONS[node.data.type] ?? FolderOpen;
 const indent = node.level * 12;
 const navSelectedId = useSelectedResourceStore((s) => s.selected?.resourceId);
 const isNavSelected = navSelectedId === node.data.id;
 const filePath = useSessionStore((s) => s.filePath);
 const pins = useRecentFilesStore((s) => (filePath ? s.getPins(filePath) : []));
 const isPinned = pins.includes(node.data.id);

 const extras = useContext(NodeExtrasContext);

 return (
 <ResourceTreeContextMenu
 resourceId={node.data.id}
 naturalId={node.data.naturalId}
 name={node.data.name}
 kind={node.data.type}
 path={(() => {
 const parts: string[] = [];
 let cur: NodeApi<ResourceNode> | null = node;
 while (cur) { parts.unshift(cur.data.name); cur = cur.parent; }
 return parts;
 })()}
 onReveal={() => extras?.onReveal(node)}
 onRefreshSubtree={() => extras?.onRefreshSubtree()}
 >
 <div
 style={{ ...style, paddingLeft: indent + 4 }}
 ref={(el) => dragHandle?.(el)}
 // Native title attr — desktop-style hover tooltip with name + kind. Cheaper than a Radix
 // Tooltip per row and doesn't compete with the context menu.
 title={`${node.data.name} — ${node.data.type}`}
 className={`relative isolate flex cursor-pointer items-center gap-1.5 px-1 text-[11px] leading-none hover:bg-primary/20
 ${isNavSelected
 ? 'text-foreground before:pointer-events-none before:absolute before:inset-x-0 before:-top-px before:-bottom-px before:-z-10 before:border-l-2 before:border-accent before:bg-primary/15'
 : 'text-foreground'}`}
 onClick={() => {
 node.select();
 activateFromTreeNode(node);
 if (node.isInternal) node.toggle();
 }}
 >
 {node.isInternal ? (
 <span className="w-3.5 shrink-0 text-[13px] leading-none text-muted-foreground">{node.isOpen ? '▾' : '▸'}</span>
 ) : (
 <span className="w-3.5 shrink-0" />
 )}
 <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
 <span className="min-w-0 flex-1 truncate">{node.data.name}</span>
 {isPinned && (
 <Pin className="h-3 w-3 shrink-0 fill-primary text-primary" aria-label="Pinned" />
 )}
 {node.data.entityCount != null && (
 <Badge
 variant="secondary"
 className="h-4 shrink-0 px-1 text-[10px] leading-none"
 >
 {node.data.entityCount.toLocaleString()}
 </Badge>
 )}
 </div>
 </ResourceTreeContextMenu>
 );
}

export default function ResourceTreePanel() {
 const filter = useResourceGraphStore((s) => s.filter);
 const setFilter = useResourceGraphStore((s) => s.setFilter);
 const setSelected = useResourceGraphStore((s) => s.setSelected);
 const revealRequest = useResourceGraphStore((s) => s.revealRequest);
 const clearRevealRequest = useResourceGraphStore((s) => s.clearRevealRequest);
 const navSelectedId = useSelectedResourceStore((s) => s.selected?.resourceId);
 const sessionId = useSessionStore((s) => s.sessionId);
 const rowHeight = useDensityRowHeight();
 const containerRef = useRef<HTMLDivElement>(null);
 const treeScrollRef = useRef<HTMLDivElement>(null);
 const filterInputRef = useRef<HTMLInputElement>(null);
 const [treeHeight, setTreeHeight] = useState(400);
 const queryClient = useQueryClient();

 const { root, isLoading, isError, refresh, isFetching } = useResourceIndex();
 // Subscribe to the engine's graph-mutation SSE stream; on event the hook invalidates the
 // resource-root query and the tree + index re-fetch. Server coalesces to ≤10/sec per session.
 useResourceGraphStream();
 const rawRoot = root ? toResourceNode(root) : null;
 // useDeferredValue gives React permission to skip intermediate filter values under load —
 // effectively a debounce without a timer. For fast-typed queries it lets the UI stay fluid.
 const deferredFilter = useDeferredValue(filter);
 const filterTerm = deferredFilter.trim();
 const rootNode = rawRoot
 ? filterTerm
 ? filterTree(rawRoot, filterTerm)
 : rawRoot
 : null;
 const totalNodes = rootNode ? countNodes(rootNode) : 0;
 const treeRef = useRef<TreeApi<ResourceNode> | null>(null);

 const onReveal = useCallback((node: NodeApi<ResourceNode>) => {
 setFilter('');
 // Scroll the revealed row into view on the next tick so filtered data has re-reconciled.
 requestAnimationFrame(() => treeRef.current?.scrollTo(node.id));
 }, [setFilter]);

 const onRefreshSubtree = useCallback(() => {
 // Per-subtree invalidation is a future optimisation; today we invalidate the whole graph.
 if (!sessionId) return;
 queryClient.invalidateQueries({
 queryKey: [`/api/sessions/${sessionId}/resources/root`],
 });
 }, [queryClient, sessionId]);

 // Cross-link reveal (§7.3) — a "Reveal in Resource Explorer" request resolves the resource node by name,
 // selects it (drives the Detail panel + nav history) and scrolls it into view. It never filters the tree.
 useEffect(() => {
 if (!revealRequest || !rawRoot) return;
 const hit = findNodeByName(rawRoot, revealRequest);
 clearRevealRequest();
 if (!hit) return;
 activateResource({
 id: hit.node.id,
 name: hit.node.name,
 kind: hit.node.type,
 path: hit.path,
 raw: hit.node as unknown as ResourceNodeDto,
 score: 0,
 });
 // Open the node's ancestors so its row exists, then scroll to it — next frame, after reconciliation.
 requestAnimationFrame(() => {
 const api = treeRef.current;
 if (!api) return;
 api.get(hit.node.id)?.openParents();
 api.scrollTo(hit.node.id);
 });
 }, [revealRequest, rawRoot, clearRevealRequest]);

 // Tree-scoped keyboard shortcuts: `/` and Ctrl+F focus the filter; Esc clears + returns focus
 // to the tree container so arrow-nav keeps working.
 useEffect(() => {
 const host = containerRef.current;
 if (!host) return;
 function onKeyDown(e: KeyboardEvent) {
 const activeInsideTree = host!.contains(document.activeElement);
 if (!activeInsideTree) return;
 if ((e.key === '/' && !e.ctrlKey && !e.altKey) || (e.key === 'f' && e.ctrlKey && !e.altKey && !e.shiftKey)) {
 e.preventDefault();
 filterInputRef.current?.focus();
 filterInputRef.current?.select();
 return;
 }
 if (e.key === 'Escape' && document.activeElement === filterInputRef.current) {
 e.preventDefault();
 setFilter('');
 treeScrollRef.current?.focus();
 return;
 }
 }
 host.addEventListener('keydown', onKeyDown);
 return () => host.removeEventListener('keydown', onKeyDown);
 }, [setFilter]);

 // When filter is active, open every visible branch so matches aren't buried under a manually-
 // collapsed parent. Imperative (ref.openAll) rather than remount — a key-swap remount double-
 // registers react-dnd's HTML5 backend and crashes.
 // rAF-deferred so it fires AFTER react-arborist has reconciled the new filtered data;
 // otherwise openAll() operates on the pre-filter tree and the post-filter render is stale.
 useEffect(() => {
 if (!filterTerm) return;
 const id = requestAnimationFrame(() => treeRef.current?.openAll());
 return () => cancelAnimationFrame(id);
 }, [filterTerm, rootNode]);

 useEffect(() => {
 const el = containerRef.current;
 if (!el) return;
 const obs = new ResizeObserver(([entry]) => setTreeHeight(entry.contentRect.height));
 obs.observe(el);
 return () => obs.disconnect();
 }, []);

 return (
 <div className="flex h-full flex-col bg-background">
 {/* Filter bar + refresh */}
 <div className="wb-pane-header flex shrink-0 items-center gap-1 border-b border-border px-2 py-1">
 <Input
 ref={filterInputRef}
 placeholder="Filter resources… ( / )"
 value={filter}
 onChange={(e) => setFilter(e.target.value)}
 className="h-6 flex-1 border-0 bg-transparent text-[11px] shadow-none
 focus-visible:ring-0 focus-visible:ring-offset-0 placeholder:text-muted-foreground"
 />
 <button
 type="button"
 onClick={refresh}
 disabled={isFetching || !sessionId}
 title="Refresh resource graph"
 className="flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground
 hover:bg-muted hover:text-foreground disabled:opacity-40"
 >
 <RefreshCw className={`h-3 w-3 ${isFetching ? 'animate-spin' : ''}`} />
 </button>
 </div>

 {/* Tree */}
 <div ref={containerRef} className="min-h-0 flex-1 overflow-hidden" tabIndex={-1}>
 {isLoading && (
 <p className="px-3 py-2 text-[11px] text-muted-foreground">Loading…</p>
 )}
 {isError && (
 <p className="px-3 py-2 text-[11px] text-destructive">Failed to load resources</p>
 )}
 {rootNode && (
 <NodeExtrasContext.Provider value={{ onReveal, onRefreshSubtree }}>
 <div ref={treeScrollRef} tabIndex={-1} className="h-full outline-none">
 <Tree
 ref={treeRef}
 data={[rootNode]}
 rowHeight={rowHeight}
 openByDefault
 selection={navSelectedId}
 onSelect={(nodes) => {
 const n = nodes[0];
 setSelected(n?.id ?? null);
 // activateResource() short-circuits if this id is already active, so the
 // round-trip from a controlled `selection` prop (triggered by Alt+←) does not
 // re-push onto nav history.
 if (n) activateFromTreeNode(n);
 }}
 width="100%"
 height={treeHeight}
 indent={0}
 >
 {NodeRow}
 </Tree>
 </div>
 </NodeExtrasContext.Provider>
 )}
 </div>

 {/* Footer */}
 <div className="shrink-0 border-t border-border px-3 py-0.5 text-[10px] text-muted-foreground">
 {totalNodes} resources
 </div>
 </div>
 );
}
