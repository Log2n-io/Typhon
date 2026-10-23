import { useEffect, useRef, useState } from 'react';
import { opacityAt } from '../app/realm-transition';
import { useUi } from '../state/ui-store';

/**
 * The curtain a realm crossing is drawn behind.
 *
 * <b>A DOM overlay rather than a scene effect</b>, for a reason that is not about cost: the place names are HTML in
 * `viewport-overlay`, so anything drawn inside Babylon leaves a room's arrival showing the previous planet's town names
 * on top of it. Covering the canvas is not enough — it has to cover the canvas AND everything layered over it.
 *
 * <b>It never gates input or the render loop.</b> Every failure mode of a realm crossing — an ask that was refused, one
 * that was rate-limited, one whose frame never came — ends with the curtain up, so the worst case has to stay cosmetic:
 * the world underneath keeps running, keeps accepting drags, and `advance`'s hold limit gives the screen back.
 */
export function RealmFade() {
  const transition = useUi((s) => s.transition);
  const [opacity, setOpacity] = useState(0);
  const frame = useRef(0);

  const running = transition.phase !== 'idle';

  useEffect(() => {
    if (!running) {
      // Nothing to sample. Without this the loop runs a callback and a setState every frame for the life of the page,
      // beside Babylon's own, to redraw an element that is not even mounted. The held value is not reset here — a
      // setState inside an effect cascades a render — it is simply not read while idle, below.
      return;
    }

    // Sampled per animation frame rather than transitioned in CSS: the phase is a state machine on the client's own
    // clock, and handing the ramp to CSS would make the two disagree about where it had got to whenever a frame was
    // late — exactly when a crossing is most likely to be visible.
    const tick = (): void => {
      setOpacity(opacityAt(transition, performance.now()));
      frame.current = requestAnimationFrame(tick);
    };

    frame.current = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(frame.current);
    };
  }, [transition, running]);

  if (!running) {
    return null;
  }

  return (
    <div className="realm-fade" style={{ opacity }}>
      {transition.caption === '' ? null : <span className="realm-fade-caption">{transition.caption}</span>}
    </div>
  );
}
