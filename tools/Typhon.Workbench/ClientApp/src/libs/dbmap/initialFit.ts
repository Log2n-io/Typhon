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
