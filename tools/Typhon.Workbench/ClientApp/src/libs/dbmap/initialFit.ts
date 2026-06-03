import { cameraCenteredOn, fitToRect, type Camera, type Rect } from './camera';
import { L0_FADE_FRACTION } from './dbMapLod';

/**
 * Whether the File Map should run its one-time fit-to-file *now*.
 *
 * The map frames the whole file exactly once, the first time it has BOTH decoded data and a real surface to
 * fit into — thereafter the user's pan/zoom is preserved (a refresh must not yank the camera back). Two
 * conditions make this subtle:
 *  - **Dimensions.** A dockview panel mounted as the *inactive* tab has a 0×0 content box, so the data-driven
 *    fit can't run when the data first arrives. The fit must be retried when the panel gets its first real
 *    size (on activation) — otherwise the camera stays at its `{scale:1, x:0, y:0}` default and the file
 *    renders ~90% off the top-left. This guard is the crux of that fix.
 *  - **In-flight fly-to.** A cross-link "Reveal in File Map" owns the camera via a tween; the auto-fit must
 *    not fight it.
 *
 * Pure so the precedence is unit-tested without a canvas / renderer.
 */
export function shouldFitViewport(p: {
  hasData: boolean;
  alreadyFitted: boolean;
  flying: boolean;
  width: number;
  height: number;
}): boolean {
  return p.hasData && !p.alreadyFitted && !p.flying && p.width > 0 && p.height > 0;
}

/**
 * The initial camera: the L0 composition view at its largest size that does NOT yet cross into L1.
 *
 * The L0→L1 crossfade is keyed to a fraction of the fit-to-screen scale (see {@link L0_FADE_FRACTION} and the
 * renderer's `l1AlphaForScale`): L1 (the Hilbert page grid) is fully shown at the fit scale, crossfading down to a
 * pure L0 composition view at `L0_FADE_FRACTION · fit` and below. So the largest zoom that is still entirely L0 is
 * exactly `L0_FADE_FRACTION · fit` — this frames the file centred at that scale. (Fit-to-file at `scale = fit` lands
 * on L1; that remains the `f` / Fit-button behaviour.)
 */
export function initialL0Camera(world: Rect, viewportW: number, viewportH: number, padding: number): Camera {
  const fit = fitToRect(world, viewportW, viewportH, padding);
  const scale = fit.scale * L0_FADE_FRACTION;
  const centreX = world.x + world.w / 2;
  const centreY = world.y + world.h / 2;
  return cameraCenteredOn(centreX, centreY, scale, viewportW, viewportH);
}
