import { ActionBar, HamBar } from './ActionBar';
import { BottomBar } from './BottomBar';
import { DownNotice } from './DownNotice';
import { Chat } from './Chat';
import { Hud } from './Hud';
import { Inspector } from './Inspector';
import { InventoryWindow } from './InventoryWindow';
import { BazaarPanel } from './BazaarPanel';
import { CraftPanel } from './CraftPanel';
import { NavPane } from './NavPane';
import { Kicked } from './Kicked';
import { Login } from './Login';
import { MissionJournalPanel } from './MissionJournal';
import { MissionTerminal } from './MissionTerminal';
import { Toolbar } from './Toolbar';
import { ShuttlePrompt } from './ShuttlePrompt';
import { ContextMenu } from './ContextMenu';
import { CharacterWindow } from './CharacterWindow';
import { UpdateNotice } from './UpdateNotice';
import { PlanetMap } from './PlanetMap';
import { needsAccount, Viewport } from './Viewport';
import { useEffect, type ReactNode } from 'react';
import { isTyping } from '../camera/camera-input';
import { useSessionStore } from '../state/session-store';
import { useUi } from '../state/ui-store';
import { useWindows } from '../state/windows-store';
import { OptionsWindow } from './OptionsWindow';
import { WaypointsWindow } from './WaypointsWindow';
import { useBar } from './WindowFrame';

/**
 * The app is either a login screen or a world, and nothing in between (A8).
 *
 * <b>The gate is the grant, and the world is UNMOUNTED without one.</b> Not hidden: `Viewport` owns the Babylon engine
 * and the session, so leaving it mounted behind a login overlay would hold a WebGL context and a socket open for
 * somebody who is not logged in. Unmounting is also what makes logging out close the session — there is no separate
 * disconnect path to keep in step with the token.
 *
 * <b>Unless this run has no account to gate on</b> — see {@link needsAccount}. The mock source and the god camera are
 * deliberately reachable with nothing signed up, and asking a login screen for a password the server will not check is
 * worse than no gate at all.
 */
export function App() {
  const loggedIn = useSessionStore((s) => s.grant !== null);
  const account = useSessionStore((s) => s.grant?.name ?? null);
  const debug = useWindows((s) => s.layout.debug);

  // Read once, outside the store: it is the URL this page was opened with and cannot change without a reload.
  const gated = needsAccount(window.location.search);

  // The account's layout (client.md C36), adopted at each login: a player's first session shows no debug window, the god camera's does.
  useEffect(() => {
    useWindows.getState().load(account, gated && account !== null);
  }, [account, gated]);

  // Escape closes the front game window — not while typing, and not while a context menu is open, which Escape closes first.
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (e.key !== 'Escape' || e.repeat || isTyping(e) || useUi.getState().contextMenu !== null) {
        return;
      }

      if (useWindows.getState().closeTop()) {
        e.preventDefault();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, []);

  if (gated && !loggedIn) {
    return (
      <div className="app">
        <Login />
      </div>
    );
  }

  return (
    <div className={debug ? 'app' : 'app no-debug'}>
      <Viewport />
      {/* The debug layer (C36): the world toolbar and the HUD, off for a player until /debug or Options shows them. */}
      {debug ? <Toolbar /> : null}
      {debug ? <Hud /> : null}
      <Chat />
      <ActionBar />
      <HamBar />
      <DownNotice />
      <ShuttlePrompt />
      <ContextMenu />
      {/* One column at the bottom right, top to bottom: each sits above the next whatever its height — the waypoint pane grows
          by a row per mission held, and fixed offsets let the inspector cover it. */}
      <RightStack>
        <Inspector />
        <NavPane />
        <BottomBar />
      </RightStack>
      <MissionTerminal />
      <MissionJournalPanel />
      <InventoryWindow />
      <BazaarPanel />
      <CraftPanel />
      <CharacterWindow />
      <UpdateNotice />
      <PlanetMap />
      <OptionsWindow />
      <WaypointsWindow />
      {/* The active waypoint's arrow at the screen's edge, placed and turned by the frame loop (ClientApp.paintWaypoint, C38). */}
      <div className="waypoint-edge" aria-hidden="true">
        ▲
      </div>
      <Kicked />
    </div>
  );
}

/** The bottom-right column, a bar (C36): moved as one while the interface is unlocked. */
function RightStack({ children }: { readonly children: ReactNode }) {
  const bar = useBar('right');
  return (
    <div className="right-stack" style={bar.style}>
      {bar.grip}
      {children}
    </div>
  );
}
