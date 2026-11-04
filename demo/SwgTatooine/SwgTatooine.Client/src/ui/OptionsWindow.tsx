import { useWindows } from '../state/windows-store';
import { AccountSection, PlayerRadius, TerrainSection } from './Settings';
import { WindowFrame } from './WindowFrame';

/**
 * The Options window (client.md C36), toggled by <kbd>O</kbd> and the game toolbar's ⚙: the account and its way out, the served radius, the
 * terrain's detail — what a player needs of the world toolbar, which is the debug layer's — and the interface itself: the bars' lock, the debug
 * layer, and the layout put back as it was on a first visit.
 */
export function OptionsWindow() {
  const open = useWindows((s) => s.optionsOpen);
  return open ? <Options /> : null;
}

function Options() {
  const close = useWindows((s) => s.closeOptions);
  const locked = useWindows((s) => s.layout.locked);
  const debug = useWindows((s) => s.layout.debug);
  const windows = useWindows.getState();
  return (
    <WindowFrame
      id="options"
      title="Options"
      onClose={close}
      ariaLabel="Options"
      className="options"
      minW={280}
      initial={(vw) => ({ x: Math.round((vw - 362) / 2), y: 90, w: 362, h: 0 })}
    >
      <AccountSection />
      <section>
        <h3>Interface</h3>
        <label className="check">
          <input
            type="checkbox"
            checked={!locked}
            onChange={(e) => {
              windows.setLocked(!e.target.checked);
            }}
          />{' '}
          Move the bars (action bar, chat, toolbar, health)
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={debug}
            onChange={(e) => {
              windows.setDebug(e.target.checked);
            }}
          />{' '}
          Debug windows (also <code>/debug</code>)
        </label>
        <div className="row">
          <button
            type="button"
            onClick={() => {
              windows.resetLayout();
            }}
            title="Every window back where it first opened, every bar where it is drawn"
          >
            Reset layout
          </button>
        </div>
      </section>
      <section>
        <h3>View</h3>
        <PlayerRadius />
      </section>
      <TerrainSection />
    </WindowFrame>
  );
}
