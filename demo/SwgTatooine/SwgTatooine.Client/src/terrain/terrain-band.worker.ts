import { bakeLandformBand, type BandRequest, type BandResult } from './band-bake';

/**
 * Bakes one horizontal band of the planet's landform and posts its rows back, transferred.
 *
 * All the reasoning is in `band-bake.ts`; this file is the thread. It is deliberately the whole of the worker: a band is a
 * pure function of its request, so there is nothing here to get wrong and nothing to test that is not already tested
 * against {@link bakeLandformBand} directly.
 */

declare const self: {
  postMessage(message: BandResult, transfer: Transferable[]): void;
  onmessage: ((event: MessageEvent<BandRequest>) => void) | null;
};

self.onmessage = (event: MessageEvent<BandRequest>): void => {
  const band = bakeLandformBand(event.data);
  self.postMessage(band, [band.height.buffer]);
};
