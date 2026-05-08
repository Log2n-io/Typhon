import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import checker from 'vite-plugin-checker';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';

// Dev-proxy forwards every path that the back end owns — both REST (`/api`, `/health`) and documentation surfaces (`/openapi.json`,
// `/swagger`) — from Vite (:5173) to Kestrel (:5200). Without this, a client `fetch('/health')` during dev would hit Vite's own 404
// handler instead of reaching ASP.NET Core. The same proxy list is what Orval consumes at codegen time (it fetches /openapi.json
// via the dev-proxy), so keeping the two in sync here is what makes `npm run generate-types` work from a cold `npm run dev`.
// Kestrel binds to 127.0.0.1 only — no IPv6 or non-loopback — to keep the API off the network.
const BACKEND_URL = 'http://127.0.0.1:5200';

// The Kestrel host generates a random bootstrap token at startup and writes it under
// %LOCALAPPDATA%\Typhon\Workbench\bootstrap.token (or $XDG_DATA_HOME/typhon/workbench on POSIX).
// The dev proxy reads it at each request and attaches X-Workbench-Token, so the SPA never sees
// the token — defense against a malicious page doing fetch('http://localhost:5173/api/...') is
// the same-origin policy (no cross-origin read) + the fact that the token only lives on disk
// (local filesystem, not reachable from the browser sandbox).
function bootstrapTokenPath(): string {
  if (process.platform === 'win32') {
    const base = process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local');
    return path.join(base, 'Typhon', 'Workbench', 'bootstrap.token');
  }
  const xdg = process.env.XDG_DATA_HOME ?? path.join(os.homedir(), '.local', 'share');
  return path.join(xdg, 'typhon', 'workbench', 'bootstrap.token');
}

function readBootstrapToken(): string | null {
  try {
    return fs.readFileSync(bootstrapTokenPath(), 'utf8').trim();
  } catch {
    return null;
  }
}

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    checker({
      typescript: true,
      eslint: {
        lintCommand: 'eslint "./src/**/*.{ts,tsx}"',
        useFlatConfig: false,
      },
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: BACKEND_URL,
        configure: (proxy) => {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          proxy.on('proxyReq', (proxyReq: any) => {
            const token = readBootstrapToken();
            if (token) proxyReq.setHeader('X-Workbench-Token', token);
          });
        },
      },
      '/openapi.json': {
        target: BACKEND_URL,
        configure: (proxy) => {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          proxy.on('proxyReq', (proxyReq: any) => {
            const token = readBootstrapToken();
            if (token) proxyReq.setHeader('X-Workbench-Token', token);
          });
        },
      },
      '/swagger': BACKEND_URL,
      '/health': BACKEND_URL,
      // Scalar API explorer — debug/troubleshooting UI served by Kestrel. The page fetches
      // /openapi.json (already proxied above) and lets the user paste a PAT into its auth dialog.
      // Proxy both the page and any sub-paths Scalar uses for its bundled assets.
      '/api-explorer': BACKEND_URL,
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    sourcemap: true,
  },
});
