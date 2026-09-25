#!/usr/bin/env node
// typhon-codegen (05-sdks § 2): writes <dir>/catalog.gen.ts, the catalog's generated ENTITIES decoders, for
// FrameApplier's `decoders` option. The catalog is the one the server serves: GET /typhon/catalog.json, or
// `dotnet typhon catalog export`.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CatalogPlan, CODEGEN_USAGE, generateDecoders, parseCatalog, parseCodegenArgs } from '../dist/index.js';

try {
  const args = parseCodegenArgs(process.argv.slice(2));
  const bytes = new Uint8Array(readFileSync(args.catalog));
  const plan = CatalogPlan.compile(parseCatalog(bytes));
  mkdirSync(args.outDir, { recursive: true });
  const file = join(args.outDir, 'catalog.gen.ts');
  writeFileSync(file, generateDecoders(plan, bytes, { importFrom: args.importFrom }));
  console.log(`typhon-codegen: ${file} (${plan.archetypes.length} archetype decoder(s))`);
} catch (e) {
  console.error(`typhon-codegen: ${e instanceof Error ? e.message : String(e)}`);
  if (!(e instanceof Error) || !e.message.includes(CODEGEN_USAGE)) {
    console.error(CODEGEN_USAGE);
  }

  process.exit(1);
}
