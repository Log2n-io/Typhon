/**
 * Babylon rendering groups. The ground draws alone first; Babylon clears depth before the next group, so the ground never
 * hides anything drawn after it. Exact while the camera stays above the plane y = 0 (`map-camera.ts`): no segment from the
 * eye to a point above the plane crosses it. Every other draw belongs to {@link SCENE_GROUP}.
 */
export const GROUND_GROUP = 0;
export const SCENE_GROUP = 1;
