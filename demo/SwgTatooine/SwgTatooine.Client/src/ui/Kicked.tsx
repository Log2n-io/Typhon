import { useSessionStore } from '../state/session-store';

/**
 * What the server said when it ended the session, over a world that has stopped moving.
 *
 * <b>The message had nowhere to go, and that is the bug this fixes.</b> `noteClosed` records a reason for every close code, but the
 * only thing reading it was the login form — which mounts only when there is no grant. The three codes that KEEP the grant
 * (`4100` shutdown, `4101` house full, `4103` already playing) therefore left the viewer in a world that had silently stopped
 * receiving frames, with the explanation sitting in a store nothing rendered. Reconnect does not help either: the SDK gives up on
 * application close codes, correctly, because retrying a refusal is a loop.
 *
 * <b>Two ways out, because the two useful answers are different.</b> "Already playing" is fixed by waiting for the other client to
 * go and reconnecting with the SAME credentials, so Reconnect reloads. A shutdown or a full house might want a different account,
 * so Log out is there too. Nothing is dismissible-to-nothing: a frozen world with the banner closed is the state this exists to
 * prevent.
 */
export function Kicked() {
  const kicked = useSessionStore((s) => s.kicked);
  const grant = useSessionStore((s) => s.grant);
  const logOut = useSessionStore((s) => s.logOut);

  // With no grant the login screen is already showing this; this overlay is only for the codes that kept the session's credentials.
  if (kicked === null || grant === null) {
    return null;
  }

  return (
    <div className="kicked-scrim">
      <section className="panel kicked">
        <h3>The session ended</h3>
        <p className="kicked-reason">{kicked}</p>
        <div className="kicked-actions">
          <button
            type="button"
            onClick={() => {
              // A reload rather than rebuilding the session in place: the token is still good, and this is the one path that is
              // certainly in step with whatever the server now thinks.
              window.location.reload();
            }}
          >
            Reconnect
          </button>
          <button type="button" className="secondary" onClick={logOut}>
            Log out
          </button>
        </div>
      </section>
    </div>
  );
}
