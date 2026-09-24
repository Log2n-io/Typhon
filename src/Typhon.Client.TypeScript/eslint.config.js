import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig(
  { ignores: ['dist', 'node_modules', 'coverage', 'eslint.config.js', 'test/.generated', 'bench/.generated'] },
  js.configs.recommended,
  tseslint.configs.strictTypeChecked,
  {
    languageOptions: {
      parserOptions: {
        // src compiles against ES2022 alone (tsconfig.json); tests add the Node typings (test/tsconfig.json).
        projectService: { allowDefaultProject: ['vitest.config.ts'] },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // noUncheckedIndexedAccess is on (byte-codec safety); `a[i]!` is how an in-bounds typed-array read says so.
      '@typescript-eslint/no-non-null-assertion': 'off',
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
    },
  },
  {
    // The Node scripts: the codegen CLI and the decode benchmark, type-checked through their own tsconfig (Node typings).
    files: ['bin/*.mjs', 'bench/*.ts'],
    languageOptions: { globals: { console: 'readonly', process: 'readonly', performance: 'readonly' } },
  },
);
