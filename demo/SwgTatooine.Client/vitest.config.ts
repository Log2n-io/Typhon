import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const sdkEntry = fileURLToPath(new URL('../../src/Typhon.Client.TypeScript/src/index.ts', import.meta.url));

export default defineConfig({
  resolve: {
    alias: { '@typhondb/client': sdkEntry },
  },
  test: {
    environment: 'node',
    include: ['test/**/*.test.ts'],
  },
});
