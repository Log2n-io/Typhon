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
