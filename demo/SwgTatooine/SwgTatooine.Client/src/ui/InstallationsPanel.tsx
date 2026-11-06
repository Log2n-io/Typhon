import { installationName, locationText, type Installation } from '../data/installations';
import { useInstallations } from '../state/installations-store';
import { useWaypoints } from '../state/waypoints-store';

/**
 * The character window's Structures tab (client.md C42, crafting-build.md § 10; <kbd>L</kbd>): each of the character's structures and where it stands,
 * with a waypoint to get there, set or taken off. Nothing else: what a structure does is its own menu, held on it in the world within reach — the
 * window lists, the world acts. A kit is placed from its own menu in the inventory.
 */
export function StructuresTab() {
  const list = useInstallations((s) => s.list);
  return (
    <div className="installations">
      {list === null ? (
        <div className="hint">Reading your structures…</div>
      ) : list.installations.length === 0 ? (
        <div className="hint">
          No structure yet. The vendor sells an extractor kit; place it from your inventory, out of town.
        </div>
      ) : (
        list.installations.map((i) => <Row key={i.id} item={i} />)
      )}
    </div>
  );
}

function Row({ item }: { readonly item: Installation }) {
  // Its pin (C38): the player's waypoint following it, if it has one.
  const pin = useWaypoints((s) => s.list?.find((w) => w.installation === item.id) ?? null);
  return (
    <div className="installation-row" data-testid={`installation-${item.id}`}>
      <div className="row">
        <span>
          <b>{installationName(item)}</b>
          <span className="hint"> · {locationText(item.x, item.z)}</span>
        </span>
        <button
          type="button"
          title={pin === null ? 'A waypoint on it, in the waypoint pane and on the map' : 'Delete its waypoint'}
          onClick={() => {
            if (pin === null) {
              useWaypoints.getState().create(installationName(item), item.x, item.z, 0, item.id);
            } else {
              useWaypoints.getState().remove(pin.id);
            }
          }}
        >
          {pin === null ? '📌 Waypoint' : '📌 Remove'}
        </button>
      </div>
    </div>
  );
}
