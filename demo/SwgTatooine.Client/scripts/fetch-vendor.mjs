// Mirrors the Babylon runtime files the glTF path loads at run time (KTX2 decoder + transcoders, zstd, meshopt decoder)
// and the IBL environment into public/vendor/babylon-<version>/, pinned to the installed @babylonjs/core, so nothing is
// fetched from a third-party CDN. Writes SHA256SUMS; files already present are kept.
//   npm run vendor
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const version = JSON.parse(
  readFileSync(join(here, '..', 'node_modules', '@babylonjs', 'core', 'package.json'), 'utf8'),
).version;
const cdn = `https://cdn.babylonjs.com/v${version}/`;
const files = [
  'babylon.ktx2Decoder.js',
  'ktx2Transcoders/1/uastc_astc.wasm',
  'ktx2Transcoders/1/uastc_bc7.wasm',
  'ktx2Transcoders/1/uastc_rgba8_unorm_v2.wasm',
  'ktx2Transcoders/1/uastc_rgba8_srgb_v2.wasm',
  'ktx2Transcoders/1/uastc_r8_unorm.wasm',
  'ktx2Transcoders/1/uastc_rg8_unorm.wasm',
  'ktx2Transcoders/1/msc_basis_transcoder.js',
  'ktx2Transcoders/1/msc_basis_transcoder.wasm',
  'zstddec.wasm',
  'meshopt_decoder.js',
].map((f) => [f, cdn + f]);
files.push(['environmentSpecular.env', 'https://assets.babylonjs.com/environments/environmentSpecular.env']);

const out = join(here, '..', 'public', 'vendor', `babylon-${version}`);
const sums = [];
for (const [rel, url] of files) {
  const dst = join(out, ...rel.split('/'));
  if (!existsSync(dst)) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
    const data = Buffer.from(await res.arrayBuffer());
    mkdirSync(dirname(dst), { recursive: true });
    writeFileSync(dst, data);
    console.log('fetched', rel, data.length);
  }
  sums.push(`${createHash('sha256').update(readFileSync(dst)).digest('hex')}  ${rel}`);
}
writeFileSync(join(out, 'SHA256SUMS'), sums.join('\n') + '\n');
console.log('vendor ready:', out);
