import { useUi } from '../state/ui-store';

/**
 * The curtain a realm crossing is drawn behind.
 *
 * <b>A DOM overlay rather than a scene effect</b>, for a reason that is not about cost: the place names are HTML in
 * `viewport-overlay`, so anything drawn inside Babylon leaves a room's arrival showing the previous planet's town names
 * on top of it. Covering the canvas is not enough — it has to cover the canvas AND everything layered over it.
 *
 * <b>Its opacity is written by the frame loop, not by React</b> (`ClientApp.paintFade`). React rendered it from an
 * animation-frame loop of its own, which lagged the frame the realm switched on: measured live, a door's arrival landed
 * with the curtain at 61 % while the camera was still in the old realm's coordinates. Written in the same frame as the
 * scene it covers, the two cannot disagree. This only mounts it and shows a caption; the stylesheet keeps it at 0 until
 * the loop says otherwise.
 *
 * <b>It never gates input or the render loop.</b> Every failure mode of a realm crossing — an ask that was refused, one
 * that was rate-limited, one whose frame never came — ends with the curtain up, so the worst case has to stay cosmetic:
 * the world underneath keeps running, keeps accepting drags, and `advance`'s hold limit gives the screen back.
 */
export function RealmFade() {
  const caption = useUi((s) => s.transition.caption);
  return (
    <div className="realm-fade">{caption === '' ? null : <span className="realm-fade-caption">{caption}</span>}</div>
  );
}
