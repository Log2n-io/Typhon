/**
 * Babylon rendering groups.
 *
 * **There is one group now.** The ground used to draw alone in group 0, and Babylon clears depth before the next group, so
 * the ground could never hide anything. That was exact rather than a trick: while the ground was the plane `y = 0` and the
 * camera and every entity were above it, no segment from the eye to a drawn point crossed it.
 *
 * Terrain ended it. With a ridge, an entity behind a mesa would draw through it — a defensible map affordance at 2 km up,
 * and a bug at eye level, which is the camera this client now has. Everything shares one depth buffer, and depth testing
 * *is* the occlusion; no other mechanism is needed.
 */
export const SCENE_GROUP = 1;
