import { beforeEach, describe, expect, it } from 'vitest';

import { useUi } from '../src/state/ui-store';

/**
 * A player's own interest radius, and the range the server states for it.
 *
 * <b>Clamped in the store rather than at the slider, because the server REFUSES rather than clamps.</b> A speed class the
 * server does not grant is refused and counted, and so is a radius outside the declared range — which means an unclamped
 * ask is not "a bit more than I get", it is a session that was never widened at all, with a slider sitting at a figure
 * nothing honours. That is the failure the god camera's radius already had once (#1075: the slider said 4000, the server
 * served 1500, the HUD reported 4000), and this is the same defence for the other mechanism.
 *
 * <b>Both ends come from the server.</b> The range is a property of the profile and of the replication grid's window
 * bound, neither of which the client can know; a figure mirrored here would be one the server can never correct.
 */
describe("a player's view radius", () => {
  beforeEach(() => {
    useUi.setState({ playerRadius: null, playerRadiusRange: null });
  });

  it('is unknown until the server states its range', () => {
    expect(useUi.getState().playerRadius).toBeNull();

    // An ask before the range arrives changes nothing: there is no range to clamp into, and guessing one is how a client
    // ends up with a figure of its own.
    useUi.getState().setPlayerRadius(300);
    expect(useUi.getState().playerRadius).toBeNull();
  });

  it("starts at the range's floor when the server states no default, as every server did before the figure existed", () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    expect(useUi.getState().playerRadius).toBe(192);
  });

  it('starts at what the server actually SERVES, which is neither end of the range', () => {
    // The floor is the narrowest a session may ask to be, not the radius it has: a renderer is served 1 024 m of a
    // 192..1 280 m range, and a slider that opened at 192 reported a view distance five times smaller than the one the
    // session was being given.
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 1280, def: 1024 });
    expect(useUi.getState().playerRadius).toBe(1024);
  });

  it('clamps a default the server states outside its own range rather than honouring it', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320, def: 4000 });
    expect(useUi.getState().playerRadius).toBe(320);
  });

  it('keeps a radius the person already chose when the range is re-stated', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 1280, def: 1024 });
    useUi.getState().setPlayerRadius(640);
    // A re-fetch must not pull the slider back to the default: the person's ask outlives the config.
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 1280, def: 1024 });
    expect(useUi.getState().playerRadius).toBe(640);
  });

  it('accepts anything inside the range', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(256);
    expect(useUi.getState().playerRadius).toBe(256);
  });

  it('clamps to both ends, because either side of the range is refused', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });

    useUi.getState().setPlayerRadius(5000);
    expect(useUi.getState().playerRadius, 'above the ceiling').toBe(320);

    useUi.getState().setPlayerRadius(10);
    expect(useUi.getState().playerRadius, 'below the profile’s own radius').toBe(192);
  });

  /** The engine takes metres as an integer, and a slider can hand over a float on some browsers. */
  it('is a whole number of metres', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(260.7);
    expect(useUi.getState().playerRadius).toBe(261);
  });

  it('ignores a radius that is not a number', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(256);
    useUi.getState().setPlayerRadius(Number.NaN);
    expect(useUi.getState().playerRadius).toBe(256);
  });

  /**
   * A re-fetch of the configuration must not undo what the person set. The config document is fetched once today, but it
   * is the kind of thing a reconnect would re-read, and silently snapping the slider back to 192 m on a reconnect is the
   * sort of behaviour nobody reports as a bug and everybody notices.
   */
  it('keeps a radius the person set when the range arrives again', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(288);
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    expect(useUi.getState().playerRadius).toBe(288);
  });

  /** A server with a narrower range pulls an existing radius down, the way the god camera's ceiling does. */
  it('pulls an existing radius into a narrower range', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(320);
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 256 });
    expect(useUi.getState().playerRadius).toBe(256);
  });

  it('forgets the radius when the server stops stating a range', () => {
    useUi.getState().setPlayerRadiusRange({ min: 192, max: 320 });
    useUi.getState().setPlayerRadius(256);
    useUi.getState().setPlayerRadiusRange(null);
    expect(useUi.getState().playerRadius).toBeNull();
  });
});
