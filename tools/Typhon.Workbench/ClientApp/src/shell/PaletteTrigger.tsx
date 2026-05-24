import { Search } from 'lucide-react';

interface PaletteTriggerProps {
 onClick: () => void;
 triggerRef: React.RefObject<HTMLButtonElement | null>;
}

export default function PaletteTrigger({ onClick, triggerRef }: PaletteTriggerProps) {
 return (
 <button
 ref={triggerRef}
 onClick={onClick}
 className="flex w-[clamp(420px,calc(100vw-24rem),560px)] items-center gap-2 rounded
 border border-border bg-muted px-3 py-0 text-fs-xl
 text-muted-foreground transition-colors hover:border-accent hover:text-foreground"
 aria-label="Open command palette (Ctrl+K)"
 title="Search commands (Ctrl+K)"
 >
 <Search className="h-3.5 w-3.5 shrink-0" />
 <span className="flex-1 text-left">Search commands…</span>
 <kbd className="shrink-0 rounded border border-border px-1 text-fs-xs">Ctrl+K</kbd>
 </button>
 );
}
