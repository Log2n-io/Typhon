import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The SDK is consumed from its sources, so a change to it is picked up without building its dist (05-client § 2).
const sdkRoot = fileURLToPath(new URL('../../src/Typhon.Client.TypeScript', import.meta.url));
const sdkEntry = `${sdkRoot}/src/index.ts`;

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@typhondb/client': sdkEntry },
  },
  server: {
    port: 5190,
    strictPort: true,
    fs: { allow: ['.', sdkRoot] },
    proxy: {
      // The live server (M1): Subscriptions v2 over WebSocket.
      '/ws': { target: 'ws://127.0.0.1:8080', ws: true },
      // What the process was started with, and the live realm inventory beside it: proxied so a dev page reads the real
      // server's configuration — including whether it serves assets — instead of behaving like a page served from a file.
      '/typhon': { target: 'http://127.0.0.1:8080' },
      // The published asset set (`--assets`). Proxied rather than copied so every GLB is fetched from ONE folder URL on
      // this origin, which is what keeps the shared KTX2 library down to a single GPU texture per file.
      '/assets': { target: 'http://127.0.0.1:8080' },
      // Sign-up and log-in. Without this the dev page can only be a spectator: the POST reaches Vite, which has no such
      // route and answers 404, and the session never gets the token that `/ws` needs to give it an avatar.
      '/auth': { target: 'http://127.0.0.1:8080' },
    },
  },
  preview: { port: 5191, strictPort: true },
  worker: { format: 'es' },
  build: {
    target: 'es2022',
    sourcemap: true,
    chunkSizeWarningLimit: 4096,
  },
});
