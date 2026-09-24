// AC-7 (07-delivery.md): a 10 000-record frame decoded into the store in ≤ 1 ms with the interpreter, ≤ 0.5 ms with
// generated decoders (typhon-codegen), on the reference laptop. Report-only: it prints both, it gates nothing.
//
// A Node script over the built package, not a vitest test: vitest's module runner executes this code about four times
// slower than Node does (measured on the same frames, source and built package alike), so a figure taken there says more
// about the harness than about the decoder. Run with `npm run bench:decode`; Node runs the TypeScript by stripping its types.
//
// The frames, on the SWG catalog's `Creature` (moving, 24-bit positions, 16-bit velocities, an onEnter byte, a packed
// enum, an 8-bit unorm): 10 000 enters; the steady state of a stream-all x1 view, 5 000 segments and 5 000 state
// records; and 10 000 leaves between rounds. Each round takes later ticks, so the store never sees time go back. The
// figure is the median of the timed rounds after a warm-up, both passes and the leaves included.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import ts from 'typescript';
import {
  catalogHashFromHex,
  catalogHashOf,
  CatalogPlan,
  FrameApplier,
  generateDecoders,
  parseCatalog,
  WireWriter,
  writeEntitiesBlock,
  writeTickHeader,
  type EnterRecord,
  type GeneratedDecoders,
  type SegmentRecord,
  type StateRecord,
} from '../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const catalogBytes = new Uint8Array(
  readFileSync(join(here, '../../../test/Typhon.Protocol.Tests/Golden/catalog-swg.bin')),
);
const plan = CatalogPlan.compile(parseCatalog(catalogBytes));
const creature = plan.archetypeByName('Creature')!;
const RECORDS = 10_000;
const WARMUP = 300;
const ROUNDS = 400;

// The generated module, compiled to JavaScript next to this script and importing the built package.
const out = join(here, '.generated');
mkdirSync(out, { recursive: true });
const source = generateDecoders(plan, catalogBytes, { importFrom: '../../dist/index.js' });
const file = join(out, 'catalog-swg.gen.mjs');
writeFileSync(
  file,
  ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } })
    .outputText,
);
const { decoders } = (await import(pathToFileURL(file).href)) as { decoders: GeneratedDecoders };
const hash = catalogHashFromHex(catalogHashOf(catalogBytes));

function frame(enters: EnterRecord[], segments: SegmentRecord[], states: StateRecord[], leaves: number[]): Uint8Array {
  const w = new WireWriter(1 << 20);
  writeTickHeader(w, 1, 0);
  writeEntitiesBlock(w, 1, creature, enters, segments, states, leaves);
  return w.toBytes();
}

/** Rewrites a frame's tick in place: the u32 after the type byte. Start ticks travel relative to it, so they follow. */
function retick(message: Uint8Array, tick: number): void {
  new DataView(message.buffer, message.byteOffset).setUint32(1, tick, true);
}

const at = (k: number): number[] => [(k % 100) * 10 - 500 + 0.25, Math.floor(k / 100) * 10 - 500 + 0.5];
const ids = Array.from({ length: RECORDS }, (_, k) => k + 1);
const enterFrame = frame(
  ids.map((netId, k) => ({
    netId,
    position: at(k),
    velocity: [0.125, -0.25],
    t0: 1,
    epoch: 1,
    values: { template: k & 0xff, mode: k % 5, hp: 0.5 },
  })),
  [],
  [],
  [],
);
const steadyFrame = frame(
  [],
  ids.slice(0, RECORDS / 2).map((netId, k) => ({ netId, position: at(k), velocity: [0.25, 0.125], t0: 1, epoch: 1 })),
  ids.slice(RECORDS / 2).map((netId, k) => ({ netId, groupMask: 0b11, values: { mode: k % 5, hp: 0.25 } })),
  [],
);
const leaveFrame = frame([], [], [], ids);

function measure(applier: FrameApplier): { enter: number; steady: number } {
  const enters: number[] = [];
  const steady: number[] = [];
  let tick = 10_000;
  for (let round = 0; round < WARMUP + ROUNDS; round++) {
    retick(enterFrame, tick++);
    retick(steadyFrame, tick++);
    retick(leaveFrame, tick++);
    let start = performance.now();
    applier.apply(enterFrame);
    const enter = performance.now() - start;
    start = performance.now();
    applier.apply(steadyFrame);
    const state = performance.now() - start;
    applier.apply(leaveFrame);
    if (round >= WARMUP) {
      enters.push(enter);
      steady.push(state);
    }
  }

  if (applier.world.anomalies !== 0) {
    throw new Error(`${applier.world.anomalies} anomalies: the frames are not what this benchmark means to measure`);
  }

  const median = (values: number[]): number => values.sort((a, b) => a - b)[values.length >> 1]!;
  return { enter: median(enters), steady: median(steady) };
}

const interpreter = measure(new FrameApplier(plan));
const generated = measure(new FrameApplier(plan, { decoders, catalogHash: hash }));
const row = (name: string, r: { enter: number; steady: number }, budget: number): string =>
  `${name.padEnd(12)} enter ${r.enter.toFixed(3)} ms   steady ${r.steady.toFixed(3)} ms   (budget ${budget} ms)`;
console.log(`AC-7, ${RECORDS} records, SWG Creature, median of ${ROUNDS} rounds`);
console.log(row('interpreter', interpreter, 1));
console.log(row('generated', generated, 0.5));
console.log(
  `generated / interpreter: enter ${(generated.enter / interpreter.enter).toFixed(2)}, steady ${(generated.steady / interpreter.steady).toFixed(2)}`,
);
