import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './ui/App';
import './ui/styles.css';
import { useTerrain } from './state/terrain-store';
import { sharedTerrain } from './terrain/terrain-provider';

const root = document.getElementById('root');
if (root === null) {
  throw new Error('index.html has no #root element');
}

// The planet, started before React renders anything.
//
// It is the longest thing a cold session does — ~2.1 s of bake across eight workers — and it depends on nothing: not the
// account, not the socket, not the canvas. Starting it here overlaps it with the login screen and with the WebSocket
// handshake, so by the time there is a world to show the ground is usually already there. On a warm cache it is a read of
// the local database instead, and finishes in a fraction of that.
//
// Deliberately not awaited: React mounts immediately and the one screen that needs the field waits for it.
void sharedTerrain({
  onPhase: (phase) => {
    useTerrain.getState().setPhase(phase);
  },
}).then((loaded) => {
  useTerrain.getState().setLoaded(loaded.baked, loaded.fromCache, loaded.loadMs);
});

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
