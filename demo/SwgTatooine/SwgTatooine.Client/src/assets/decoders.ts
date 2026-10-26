import { AbstractEngine } from '@babylonjs/core/Engines/abstractEngine';
import { MeshoptCompression } from '@babylonjs/core/Meshes/Compression/meshoptCompression';
import { KhronosTextureContainer2 } from '@babylonjs/core/Misc/khronosTextureContainer2';
import { AutoReleaseWorkerPool } from '@babylonjs/core/Misc/workerPool';

/**
 * How long an idle KTX2 decoder worker is kept: the session, at `setTimeout`'s ceiling — `Infinity` would fire at once.
 *
 * <b>Babylon's default is one second.</b> Assets stream in bursts as the session moves, and a worker terminated between two
 * bursts is a new worker for the next: its decoder module and its transcoder's wasm fetched (from cache) and compiled again
 * before the first texture of the burst decodes. Kept, the pool's workers — at most four — cost their wasm memory for the
 * session, which a client that will stream textures for the whole session spends anyway.
 */
export const DECODER_WORKER_IDLE_MS = 2 ** 31 - 1;

/**
 * Points Babylon's run-time decoders at self-hosted copies (ASSET_PIPELINE.md § 6.5.1).
 *
 * The published GLBs REQUIRE `KHR_texture_basisu` (KTX2: a JS decoder module, wasm transcoders, zstd) and
 * `EXT_meshopt_compression` (meshopt decoder). Babylon fetches all of them lazily from its CDN unless told otherwise;
 * a game should not have a third-party CDN in its boot path, and the files must match the engine version exactly, so
 * `npm run vendor` mirrors them into `public/vendor/babylon-<version>/` and this points there.
 *
 * URLs are made absolute because the KTX2 decoder runs in workers created from `blob:` URLs, against which a relative
 * URL resolves to nothing.
 */
export function configureDecoders(
  base = `${import.meta.env.BASE_URL}vendor/babylon-${AbstractEngine.Version}/`,
): string {
  const root = new URL(base, location.href).href;
  const ktx = `${root}ktx2Transcoders/1/`;
  KhronosTextureContainer2.URLConfig = {
    jsDecoderModule: `${root}babylon.ktx2Decoder.js`,
    wasmUASTCToASTC: `${ktx}uastc_astc.wasm`,
    wasmUASTCToBC7: `${ktx}uastc_bc7.wasm`,
    wasmUASTCToRGBA_UNORM: `${ktx}uastc_rgba8_unorm_v2.wasm`,
    wasmUASTCToRGBA_SRGB: `${ktx}uastc_rgba8_srgb_v2.wasm`,
    wasmUASTCToR8_UNORM: `${ktx}uastc_r8_unorm.wasm`,
    wasmUASTCToRG8_UNORM: `${ktx}uastc_rg8_unorm.wasm`,
    jsMSCTranscoder: `${ktx}msc_basis_transcoder.js`,
    wasmMSCTranscoder: `${ktx}msc_basis_transcoder.wasm`,
    wasmZSTDDecoder: `${root}zstddec.wasm`,
  };
  MeshoptCompression.Configuration = { decoder: { url: `${root}meshopt_decoder.js` } };
  // Read by the pool's constructor, which the first KTX2 texture runs — after this, which runs before the first load.
  // Babylon's only pool of this kind here: Draco's is the other user, and no published asset is Draco-compressed.
  AutoReleaseWorkerPool.DefaultOptions = { idleTimeElapsedBeforeRelease: DECODER_WORKER_IDLE_MS };
  return root;
}
