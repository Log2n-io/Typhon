import { useState } from 'react';

import { useSessionStore } from '../state/session-store';

/**
 * The login screen (A8): a name, a password, and the two things you can do with them.
 *
 * <b>One form for both actions rather than two screens.</b> Signing up and logging in take the same two fields and
 * differ only in which button is pressed, so a tab switch would be a second place for the state to live and a second
 * chance for the two to disagree about what was typed.
 *
 * <b>The rules are not restated here.</b> The server owns them — length, the character set, what makes a name taken —
 * and it answers with a sentence fit to show. A copy in the client is a copy that drifts, and the first symptom is a form
 * that refuses something the server would have accepted.
 */
export function Login() {
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const busy = useSessionStore((s) => s.busy);
  const error = useSessionStore((s) => s.error);
  const kicked = useSessionStore((s) => s.kicked);
  const doSignUp = useSessionStore((s) => s.signUp);
  const doLogIn = useSessionStore((s) => s.logIn);

  // Enough to stop an obviously empty submission; everything else is the server's to judge.
  const ready = !busy && name.trim().length > 0 && password.length > 0;

  return (
    <div className="login">
      <form
        className="login-card"
        onSubmit={(e) => {
          // The default submit reloads the page, which would drop the store and the token with it.
          e.preventDefault();
          if (ready) {
            void doLogIn(name, password);
          }
        }}
      >
        <h1>Tatooine</h1>

        <label htmlFor="login-name">Name</label>
        <input
          id="login-name"
          // `username`, so a password manager offers the pair rather than treating this as a search box.
          autoComplete="username"
          autoFocus
          value={name}
          disabled={busy}
          onChange={(e) => {
            setName(e.target.value);
          }}
        />

        <label htmlFor="login-password">Password</label>
        <input
          id="login-password"
          type="password"
          autoComplete="current-password"
          value={password}
          disabled={busy}
          onChange={(e) => {
            setPassword(e.target.value);
          }}
        />

        <div className="login-actions">
          {/* Log in is the submit, so Enter does the common thing. */}
          <button type="submit" disabled={!ready}>
            Log in
          </button>
          <button
            type="button"
            disabled={!ready}
            onClick={() => {
              void doSignUp(name, password);
            }}
          >
            Sign up
          </button>
        </div>

        {/* role=alert so a screen reader announces a refusal rather than leaving it to be noticed. */}
        {error !== null && (
          <p className="login-error" role="alert">
            {error}
          </p>
        )}
        {error === null && kicked !== null && (
          <p className="login-error" role="alert">
            {kicked}
          </p>
        )}

        <p className="login-note">
          A name and a password, and nothing else — no e-mail, no recovery. An operator can delete an account and can
          never read its password.
        </p>
      </form>
    </div>
  );
}
