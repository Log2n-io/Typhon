import { ActionBar, HamBar } from './ActionBar';
import { DownNotice } from './DownNotice';
import { Chat } from './Chat';
import { Hud } from './Hud';
import { Inspector } from './Inspector';
import { NavPane } from './NavPane';
import { Kicked } from './Kicked';
import { Login } from './Login';
import { Toolbar } from './Toolbar';
import { ShuttlePrompt } from './ShuttlePrompt';
import { ContextMenu } from './ContextMenu';
import { needsAccount, Viewport } from './Viewport';
import { useSessionStore } from '../state/session-store';

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

  // Read once, outside the store: it is the URL this page was opened with and cannot change without a reload.
  const gated = needsAccount(window.location.search);

  if (gated && !loggedIn) {
    return (
      <div className="app">
        <Login />
      </div>
    );
  }

  return (
    <div className="app">
      <Viewport />
      <Toolbar />
      <Hud />
      <Chat />
      <ActionBar />
      <HamBar />
      <DownNotice />
      <ShuttlePrompt />
      <ContextMenu />
      <NavPane />
      <Inspector />
      <Kicked />
    </div>
  );
}
