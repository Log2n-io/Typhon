import { useEffect, useState } from 'react';

import { outfitLabel } from '../character/outfits';
import { fetchServerConfig } from '../data/server-config';
import { useSessionStore } from '../state/session-store';

/** The outfit the form offers first: the demo's own character. */
const DEFAULT_OUTFIT = 'casual2_male';

/**
 * The login screen (A8): a login form, and a link to a registration form.
 *
 * <b>Two forms, no longer one with two buttons</b> (Loïc, 2026-10-25: "mixing login and registration is now too
 * confusing"). They shared two fields until sign-up gained the outfit, which a login has no use for; one card offering
 * both made the outfit read as something a login could change. The name and password typed carry over a switch, so a
 * person who started typing on the wrong form does not type them twice; the last refusal does not.
 *
 * <b>The rules are not restated here.</b> The server owns them — length, the character set, what makes a name taken —
 * and it answers with a sentence fit to show. A copy in the client is a copy that drifts, and the first symptom is a form
 * that refuses something the server would have accepted.
 */
export function Login() {
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const busy = useSessionStore((s) => s.busy);
  const error = useSessionStore((s) => s.error);
  const kicked = useSessionStore((s) => s.kicked);
  const doSignUp = useSessionStore((s) => s.signUp);
  const doLogIn = useSessionStore((s) => s.logIn);
  const clearError = useSessionStore((s) => s.clearError);
  // The outfits a new character may wear, as the server publishes them (`assets.players`); empty until it has answered,
  // and from a server that publishes none — which then picks one itself.
  const [outfits, setOutfits] = useState<readonly string[]>([]);
  // One-based into `outfits`, as the wire counts it; 0 until the list has arrived.
  const [outfit, setOutfit] = useState(0);

  useEffect(() => {
    let cancelled = false;
    void fetchServerConfig().then((config) => {
      const players = config?.assets?.players ?? [];
      if (!cancelled) {
        setOutfits(players);
        const preferred = players.indexOf(DEFAULT_OUTFIT);
        setOutfit(players.length === 0 ? 0 : (preferred < 0 ? 0 : preferred) + 1);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  // Enough to stop an obviously empty submission; everything else is the server's to judge.
  const ready = !busy && name.trim().length > 0 && password.length > 0;
  const registering = mode === 'register';

  const switchTo = (next: 'login' | 'register'): void => {
    clearError();
    setMode(next);
  };

  return (
    <div className="login">
      <form
        className="login-card"
        onSubmit={(e) => {
          // The default submit reloads the page, which would drop the store and the token with it.
          e.preventDefault();
          if (!ready) {
            return;
          }

          if (registering) {
            void doSignUp(name, password, outfit);
          } else {
            void doLogIn(name, password);
          }
        }}
      >
        <h1>{registering ? 'Create an account' : 'Tatooine'}</h1>

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
          // `new-password` on the registration form, so a password manager offers to generate and save one.
          autoComplete={registering ? 'new-password' : 'current-password'}
          value={password}
          disabled={busy}
          onChange={(e) => {
            setPassword(e.target.value);
          }}
        />

        {/* What the new character wears, for good — changing it is the economy's to sell. */}
        {registering && outfits.length > 0 && (
          <>
            <label htmlFor="login-outfit">Outfit</label>
            <select
              id="login-outfit"
              value={outfit}
              disabled={busy}
              onChange={(e) => {
                setOutfit(Number(e.target.value));
              }}
            >
              {outfits.map((id, i) =>
                id === '' ? null : (
                  <option key={id} value={i + 1}>
                    {outfitLabel(id)}
                  </option>
                ),
              )}
            </select>
          </>
        )}

        <div className="login-actions">
          {/* The submit either way, so Enter does what the form is for. */}
          <button type="submit" disabled={!ready}>
            {registering ? 'Register' : 'Log in'}
          </button>
        </div>

        {/* role=alert so a screen reader announces a refusal rather than leaving it to be noticed. */}
        {error !== null && (
          <p className="login-error" role="alert">
            {error}
          </p>
        )}
        {!registering && error === null && kicked !== null && (
          <p className="login-error" role="alert">
            {kicked}
          </p>
        )}

        <p className="login-switch">
          {registering ? 'Already have an account? ' : 'No account yet? '}
          <button
            type="button"
            className="login-link"
            disabled={busy}
            onClick={() => {
              switchTo(registering ? 'login' : 'register');
            }}
          >
            {registering ? 'Log in' : 'Register'}
          </button>
        </p>

        {registering && (
          <p className="login-note">
            A name and a password, and nothing else — no e-mail, no recovery. An operator can delete an account and can
            never read its password.
          </p>
        )}
      </form>
    </div>
  );
}
