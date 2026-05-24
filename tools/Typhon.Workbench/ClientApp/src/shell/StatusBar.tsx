import { useHeartbeat } from '@/hooks/streams/useHeartbeat';
import { useSessionStore } from '@/stores/useSessionStore';
import { useKeyChordStore } from '@/stores/useKeyChordStore';
import { useResourceIndex } from '@/hooks/useResourceIndex';
import { activateResource } from './commands/activateResource';

const STORAGE_ID_CANDIDATES = [
 'Storage/PagedMMF',
 'storage/paged-mmf',
 'Storage',
 'storage',
];

function fmtNumOrDash(v: number | null | undefined, digits = 1): string {
 if (v == null) return '—';
 return v.toFixed(digits);
}

export default function StatusBar() {
 const kind = useSessionStore((s) => s.kind);
 const filePath = useSessionStore((s) => s.filePath);
 const { status, payload } = useHeartbeat();
 const { index } = useResourceIndex();
 const chordArmed = useKeyChordStore((s) => s.armed);
 const connected = kind !== 'none';
 const dotColor = connected && status === 'green' ? 'bg-green-500' : 'bg-muted-foreground';

 const fileLabel = connected
 ? filePath
 ? filePath.split(/[\\/]/).pop() ?? filePath
 : kind
 : 'Disconnected';

 const jumpToStorage = () => {
 if (!index) return;
 for (const id of STORAGE_ID_CANDIDATES) {
 const hit = index.getById(id);
 if (hit) {
 activateResource(hit);
 return;
 }
 }
 const hits = index.search('Storage');
 if (hits.length > 0) activateResource(hits[0]);
 };

 return (
 <footer
 className="flex h-[22px] shrink-0 items-center gap-3 border-t border-border
 bg-card px-3 text-[14px] text-muted-foreground"
 >
 <span className={`h-2 w-2 rounded-full ${dotColor}`} aria-hidden="true" />
 <span>{fileLabel}</span>
 {connected && payload && (
 <>
 <span>·</span>
 <span title="Schema revision">rev {payload.revision}</span>
 <span>·</span>
 <span title={payload.tickRate == null ? 'Tick rate available when runtime hosting lands' : 'Tick rate'}>
 {payload.tickRate ?? '—'} Hz
 </span>
 <span>·</span>
 <button
 type="button"
 onClick={jumpToStorage}
 title="Jump to Storage in Resource Tree"
 className="cursor-pointer underline-offset-2 hover:underline hover:text-foreground"
 >
 {payload.memoryMb} MB
 </button>
 <span>·</span>
 <span title={payload.lastTickDurationMs == null ? 'Available when runtime hosting lands' : 'Last tick duration'}>
 {fmtNumOrDash(payload.lastTickDurationMs)} ms
 </span>
 <span>·</span>
 <span title={payload.activeTransactionCount == null ? 'Available when runtime hosting lands' : 'Active transactions'}>
 tx {payload.activeTransactionCount ?? '—'}
 </span>
 </>
 )}
 {/* Focus-chord hint (AC2.3): shown the instant `g` arms the chord, hidden when the second key lands or the
 window elapses (driven by useKeyChordStore ← createChordHandler.onArmedChange). */}
 {chordArmed && (
 <span className="text-foreground" data-testid="chord-hint">
 waiting for a second key chord… <span className="text-muted-foreground">(c / a / s / d / m)</span>
 </span>
 )}
 </footer>
 );
}
