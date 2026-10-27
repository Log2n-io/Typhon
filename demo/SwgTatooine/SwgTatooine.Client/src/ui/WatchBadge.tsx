import { watchLabel } from '../app/watch-link';
import { useUi } from '../state/ui-store';

/** "Watching bot #12 (netId 3456)" over the god view while the dashboard's watch is on (`watch-link.ts`). */
export function WatchBadge() {
  const label = useUi((s) => watchLabel(s.watch, s.selectedNetId));
  const clear = useUi((s) => s.setWatch);
  if (label === null) {
    return null;
  }

  return (
    <div className="watch-badge">
      <span>{label}</span>
      <button
        className="link"
        title="Stop watching"
        onClick={() => {
          clear(null);
        }}
      >
        ×
      </button>
    </div>
  );
}
