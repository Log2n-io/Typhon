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

/**
 * The terrain, alone, drawn before everything else — and {@link SCENE_GROUP} keeps the depth it leaves rather than clearing
 * it (`configureScene`), so it is still the one depth buffer above.
 *
 * <b>First, so that what the ground hides costs no shading.</b> Within a group Babylon draws opaque meshes in the order it
 * collects them, which put the ground wherever it happened to fall among two thousand placements; a building behind a
 * mesa, drawn before the mesa, was shaded in full and then painted over. Drawn first, the ground's depth rejects it before
 * its fragment shader runs. A depth-only prepass of the ground was measured instead (2026-10-26) and cost 0.25–0.4 ms of
 * GPU a frame at 4K for nothing back: the ground's vertex work is the expensive half, and a prepass pays it twice.
 */
export const TERRAIN_GROUP = 0;

/**
 * The debug gizmos (portals, spawns, interactions, colliders): drawn after the scene with its depth cleared, so a portal box
 * half inside a wall, or a collider inside the mesh it stands for, shows through it. A gizmo hidden by the thing it
 * describes would be no use for checking that the two agree.
 */
export const GIZMO_GROUP = 2;
