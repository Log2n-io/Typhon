/**
 * Per-frame view math on the CPU, in render space (planet coordinates minus the render origin), float64 throughout.
 * Babylon matrices are row-major arrays that transform row vectors: `clip = (x, y, z, 1) · M`.
 */

/** Band of the level of detail: a mesh, or a screen-sized sprite. */
export const Band = { Unset: 0, Near: 1, Far: 2 } as const;

/** Enter the near band at this many projected pixels, leave it below {@link LEAVE_NEAR_PX}: hysteresis, no flicker. */
export const ENTER_NEAR_PX = 7;
export const LEAVE_NEAR_PX = 5;

export function chooseBand(previous: number, pixels: number): number {
  if (previous === Band.Near) {
    return pixels < LEAVE_NEAR_PX ? Band.Far : Band.Near;
  }

  if (previous === Band.Far) {
    return pixels >= ENTER_NEAR_PX ? Band.Near : Band.Far;
  }

  return pixels >= (ENTER_NEAR_PX + LEAVE_NEAR_PX) / 2 ? Band.Near : Band.Far;
}

/**
 * The per-instance integer the shaders unpack: bits 0–3 style index, 4–6 mode, 7 selected. A style index outside
 * [0, {@link STYLE_LIMIT}) draws as style 0 rather than reading past the shaders' arrays.
 */
export const STYLE_LIMIT = 16;
export const MODE_LIMIT = 8;
export const SELECTED_BIT = STYLE_LIMIT * MODE_LIMIT;

export function packState(style: number, mode: number, selected: boolean): number {
  const s = style >= 0 && style < STYLE_LIMIT ? style | 0 : 0;
  return s + ((mode | 0) & (MODE_LIMIT - 1)) * STYLE_LIMIT + (selected ? SELECTED_BIT : 0);
}

/** Style index of a packed instance. */
export function packedStyle(packed: number): number {
  return packed % STYLE_LIMIT;
}

/** Mode index of a packed instance. */
export function packedMode(packed: number): number {
  return Math.floor((packed % SELECTED_BIT) / STYLE_LIMIT);
}

/** Six frustum planes `(a, b, c, d)` from a view-projection matrix, normalised so `a·x + b·y + c·z + d` is a distance. */
export function extractFrustumPlanes(m: ArrayLike<number>, out: Float64Array): void {
  // Gribb–Hartmann: plane = column 3 ± column k, where column k holds the coefficients of clip coordinate k.
  for (let p = 0; p < 6; p++) {
    const k = p >> 1;
    const sign = (p & 1) === 0 ? 1 : -1;
    const a = m[3] + sign * m[k];
    const b = m[7] + sign * m[4 + k];
    const c = m[11] + sign * m[8 + k];
    const d = m[15] + sign * m[12 + k];
    const len = Math.hypot(a, b, c) || 1;
    out[p * 4] = a / len;
    out[p * 4 + 1] = b / len;
    out[p * 4 + 2] = c / len;
    out[p * 4 + 3] = d / len;
  }
}

/** Whether a sphere is at least partly inside all six planes. */
export function sphereInFrustum(planes: Float64Array, x: number, y: number, z: number, radius: number): boolean {
  for (let p = 0; p < 24; p += 4) {
    if (planes[p] * x + planes[p + 1] * y + planes[p + 2] * z + planes[p + 3] < -radius) {
      return false;
    }
  }

  return true;
}

export interface ScreenPoint {
  x: number;
  y: number;
  /** Clip w: the distance along the view direction; ≤ 0 means behind the camera. */
  w: number;
}

/** Projects a render-space point to viewport pixels (origin top-left). */
export function projectToScreen(
  m: ArrayLike<number>,
  x: number,
  y: number,
  z: number,
  width: number,
  height: number,
  out: ScreenPoint,
): ScreenPoint {
  const cx = x * m[0] + y * m[4] + z * m[8] + m[12];
  const cy = x * m[1] + y * m[5] + z * m[9] + m[13];
  const cw = x * m[3] + y * m[7] + z * m[11] + m[15];
  out.w = cw;
  if (cw <= 1e-6) {
    out.x = Number.NaN;
    out.y = Number.NaN;
    return out;
  }

  out.x = ((cx / cw) * 0.5 + 0.5) * width;
  out.y = (1 - ((cy / cw) * 0.5 + 0.5)) * height;
  return out;
}

/**
 * The render origin: a point near the camera, snapped to a 1 km grid, subtracted from every position before it reaches
 * the GPU. Float32 then only holds values within a few kilometres, so nothing jitters when the camera is close.
 */
export class RenderOrigin {
  x = 0;
  z = 0;
  private readonly snapM: number;

  constructor(snapM = 1024) {
    this.snapM = snapM;
  }

  /** Re-centres on a planet point when it has moved a full snap away; returns true when the origin changed. */
  follow(x: number, z: number): boolean {
    const nx = Math.round(x / this.snapM) * this.snapM;
    const nz = Math.round(z / this.snapM) * this.snapM;
    if (nx === this.x && nz === this.z) {
      return false;
    }

    this.x = nx;
    this.z = nz;
    return true;
  }
}

export interface PickHit {
  archetype: number;
  netId: number;
  /** Screen distance from the cursor, pixels. Only meaningful when {@link PickHit.inside} is false. */
  pixels: number;
  /** Clip w of the hit, or the ray distance for a shape hit: nearer wins a tie. */
  depth: number;
  /**
   * Whether the cursor was **inside the drawn shape**, rather than merely near its centre.
   *
   * This is the distinction the pick used not to make, and it is the whole defect: an 18 m building 60 m away covers
   * about 320 screen pixels, and only the 16 around its centre were clickable. Every visible pixel counts now, and a
   * shape the cursor is genuinely over always beats one it is only near — however much nearer that one's centre is.
   */
  inside: boolean;
}

/** A ray through a viewport pixel, in render space. */
export interface PickRay {
  ox: number;
  oy: number;
  oz: number;
  /** Unit direction. */
  dx: number;
  dy: number;
  dz: number;
}

/**
 * Inverts a 4×4 matrix in this file's convention (row-major, row vectors).
 *
 * @returns False when the matrix is singular, leaving `out` untouched. A projection matrix never is, but one taken from
 *   a half-initialised camera would otherwise unproject to NaN and pick nothing at all, silently.
 */
export function invertMatrix4(m: ArrayLike<number>, out: Float64Array): boolean {
  const a00 = m[0];
  const a01 = m[1];
  const a02 = m[2];
  const a03 = m[3];
  const a10 = m[4];
  const a11 = m[5];
  const a12 = m[6];
  const a13 = m[7];
  const a20 = m[8];
  const a21 = m[9];
  const a22 = m[10];
  const a23 = m[11];
  const a30 = m[12];
  const a31 = m[13];
  const a32 = m[14];
  const a33 = m[15];

  const b00 = a00 * a11 - a01 * a10;
  const b01 = a00 * a12 - a02 * a10;
  const b02 = a00 * a13 - a03 * a10;
  const b03 = a01 * a12 - a02 * a11;
  const b04 = a01 * a13 - a03 * a11;
  const b05 = a02 * a13 - a03 * a12;
  const b06 = a20 * a31 - a21 * a30;
  const b07 = a20 * a32 - a22 * a30;
  const b08 = a20 * a33 - a23 * a30;
  const b09 = a21 * a32 - a22 * a31;
  const b10 = a21 * a33 - a23 * a31;
  const b11 = a22 * a33 - a23 * a32;

  const det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  // Scaled, not compared against zero. A determinant of 1e-320 is not zero and `1 / det` is Infinity, which fills the
  // inverse with Infinity and NaN and hands `unprojectRay` a ray that fails every comparison — the pick then selects
  // nothing and reports no reason, which is the exact failure this guard exists to prevent. The scale is the largest
  // cofactor product in the sum, so the test is on the determinant's significance rather than on its magnitude.
  const scale = Math.max(
    Math.abs(b00 * b11), Math.abs(b01 * b10), Math.abs(b02 * b09),
    Math.abs(b03 * b08), Math.abs(b04 * b07), Math.abs(b05 * b06));
  if (!Number.isFinite(det) || !(Math.abs(det) > scale * 1e-12)) {
    return false;
  }

  const d = 1 / det;
  out[0] = (a11 * b11 - a12 * b10 + a13 * b09) * d;
  out[1] = (a02 * b10 - a01 * b11 - a03 * b09) * d;
  out[2] = (a31 * b05 - a32 * b04 + a33 * b03) * d;
  out[3] = (a22 * b04 - a21 * b05 - a23 * b03) * d;
  out[4] = (a12 * b08 - a10 * b11 - a13 * b07) * d;
  out[5] = (a00 * b11 - a02 * b08 + a03 * b07) * d;
  out[6] = (a32 * b02 - a30 * b05 - a33 * b01) * d;
  out[7] = (a20 * b05 - a22 * b02 + a23 * b01) * d;
  out[8] = (a10 * b10 - a11 * b08 + a13 * b06) * d;
  out[9] = (a01 * b08 - a00 * b10 - a03 * b06) * d;
  out[10] = (a30 * b04 - a31 * b02 + a33 * b00) * d;
  out[11] = (a21 * b02 - a20 * b04 - a23 * b00) * d;
  out[12] = (a11 * b07 - a10 * b09 - a12 * b06) * d;
  out[13] = (a00 * b09 - a01 * b07 + a02 * b06) * d;
  out[14] = (a31 * b01 - a30 * b03 - a32 * b00) * d;
  out[15] = (a20 * b03 - a21 * b01 + a22 * b00) * d;
  return true;
}

/**
 * The ray through a viewport pixel, from the **inverse** of the same view-projection {@link projectToScreen} uses.
 *
 * Taking it from that matrix rather than from the camera is deliberate: the pick then agrees with what was drawn by
 * construction, including whatever the camera did to its own field of view that frame. A ray rebuilt from camera state
 * is a second source of truth, and two sources of truth for where a thing is on screen is how a pick drifts off it.
 */
export function unprojectRay(
  inverse: Float64Array,
  px: number,
  py: number,
  width: number,
  height: number,
  out: PickRay,
): boolean {
  const nx = (px / width) * 2 - 1;
  const ny = 1 - (py / height) * 2;
  if (!unprojectPoint(inverse, nx, ny, -1, nearPoint) || !unprojectPoint(inverse, nx, ny, 1, farPoint)) {
    return false;
  }

  const dx = farPoint[0] - nearPoint[0];
  const dy = farPoint[1] - nearPoint[1];
  const dz = farPoint[2] - nearPoint[2];
  const len = Math.hypot(dx, dy, dz);
  if (!(len > 0) || !Number.isFinite(len)) {
    return false;
  }

  out.ox = nearPoint[0];
  out.oy = nearPoint[1];
  out.oz = nearPoint[2];
  out.dx = dx / len;
  out.dy = dy / len;
  out.dz = dz / len;
  return true;
}

/** Scratch for {@link unprojectRay}: one buffer per plane, so a pick allocates nothing and neither aliases the other. */
const nearPoint = new Float64Array(3);
const farPoint = new Float64Array(3);

function unprojectPoint(inverse: Float64Array, nx: number, ny: number, nz: number, out: Float64Array): boolean {
  const x = nx * inverse[0] + ny * inverse[4] + nz * inverse[8] + inverse[12];
  const y = nx * inverse[1] + ny * inverse[5] + nz * inverse[9] + inverse[13];
  const z = nx * inverse[2] + ny * inverse[6] + nz * inverse[10] + inverse[14];
  const w = nx * inverse[3] + ny * inverse[7] + nz * inverse[11] + inverse[15];
  if (w === 0 || !Number.isFinite(w)) {
    return false;
  }

  out[0] = x / w;
  out[1] = y / w;
  out[2] = z / w;
  return true;
}

/**
 * How far along a ray the ground is, in metres, or {@link Infinity} when it never reaches it.
 *
 * **This is what makes a click mean what the picture shows.** Without it the pick tests entity boxes and sprite discs
 * against a ray that passes straight through the planet, so clicking the visible rock face of a mesa selects the creature
 * standing behind it — an entity that is not on screen at all. Depth testing does this for the GPU (`render-groups.ts`
 * collapses everything into one group so that it can); the pick has to do it for itself.
 *
 * Marched rather than solved: the field is a bilinear surface over 16.7 M posts and has no closed form. The step grows
 * with distance because the mesh does too — a hundred metres out, the ground the viewer sees is already interpolated over
 * several posts — and the crossing is then bisected, so the returned distance is accurate to a few centimetres regardless
 * of how coarse the step that found it was.
 *
 * @param ray The unprojected cursor ray; its direction must be unit length.
 * @param field The client's own heightfield.
 * @param maxDistance Stop looking beyond this — the far plane, or the fog's reach.
 */
export function rayGroundT(
  ray: PickRay,
  field: { heightAt(x: number, z: number): number; maxHeightM: number },
  maxDistance: number,
): number {
  // An eye already under the ground has no horizon to be occluded by, and marching from inside the terrain would return 0
  // and make everything unpickable. This happens: the eye camera clamps to the ground, and a frame taken mid-bake sees a
  // field that is still flat.
  if (ray.oy - field.heightAt(ray.ox, ray.oz) < 0) {
    return Infinity;
  }

  // Looking up, out of the field's own ceiling: nothing below can be in the way.
  if (ray.dy >= 0 && ray.oy >= field.maxHeightM) {
    return Infinity;
  }

  let prev = 0;
  let t = 0;
  while (t < maxDistance) {
    t = Math.min(t + Math.max(GROUND_MARCH_M, t * 0.02), maxDistance);
    const y = ray.oy + ray.dy * t;
    if (y > field.maxHeightM && ray.dy >= 0) {
      return Infinity;
    }

    if (y - field.heightAt(ray.ox + ray.dx * t, ray.oz + ray.dz * t) <= 0) {
      // Bisect the step that crossed. Twelve halvings take a 100 m step to 2.4 cm, which is far below anything the
      // occlusion decision can turn on.
      let lo = prev;
      let hi = t;
      for (let i = 0; i < 12; i++) {
        const mid = (lo + hi) * 0.5;
        if (ray.oy + ray.dy * mid - field.heightAt(ray.ox + ray.dx * mid, ray.oz + ray.dz * mid) <= 0) {
          hi = mid;
        } else {
          lo = mid;
        }
      }

      return hi;
    }

    prev = t;
  }

  return Infinity;
}

/** The march's step close to the eye, in metres — the field's own post spacing, so a single post cannot be stepped over. */
const GROUND_MARCH_M = 4;

/**
 * Picks among near-band instances by testing the ray against each one's **drawn box**, exactly as the vertex shader
 * places it: sized by style, grown by the selection scale, flattened in the flatten mode, and turned by its yaw.
 *
 * Every shape in `shapes.ts` fits `x, z ∈ [−½, ½]` and `y ∈ [0, 1]` before scaling, so its box bounds the shape and a hit
 * on the box is a hit on the silhouette or just beside it. That is the right side to err on for a pick.
 *
 * @param sizes Per style index, `(width, height, length)` in metres, flat at stride 3.
 * @param flattenMode The mode whose height is quartered, or −1.
 * @param selectedScale What the shader grows a selected shape by.
 */
export function pickShapes(
  ray: PickRay,
  data: Float32Array,
  yaws: Float32Array,
  netIds: Uint32Array,
  count: number,
  sizes: Float64Array,
  flattenMode: number,
  selectedScale: number,
  archetype: number,
  best: PickHit,
  maxT = Infinity,
): void {
  for (let k = 0; k < count; k++) {
    const b = k * 4;
    const packed = data[b + 3];
    const style = packedStyle(packed);
    const grow = packed >= SELECTED_BIT ? selectedScale : 1;
    const hx = sizes[style * 3] * grow * 0.5;
    const sy = sizes[style * 3 + 1] * grow * (packedMode(packed) === flattenMode ? 0.25 : 1);
    const hz = sizes[style * 3 + 2] * grow * 0.5;

    // Into the instance's own frame: translate, then undo the shader's yaw. It computes
    // `turned = (lx·c + lz·s, ly, −lx·s + lz·c)`, whose inverse is `lx = dx·c − dz·s`, `lz = dx·s + dz·c`.
    const c = Math.cos(yaws[k]);
    const sn = Math.sin(yaws[k]);
    const rx = ray.ox - data[b];
    const rz = ray.oz - data[b + 2];
    const t = rayBox(
      rx * c - rz * sn,
      ray.oy - data[b + 1],
      rx * sn + rz * c,
      ray.dx * c - ray.dz * sn,
      ray.dy,
      ray.dx * sn + ray.dz * c,
      hx,
      sy,
      hz,
    );
    // Behind the camera, or behind the ground the viewer is looking at.
    if (t < 0 || t > maxT) {
      continue;
    }

    // A shape the cursor is genuinely over beats any near-miss, and among those the nearest along the ray wins.
    if (!best.inside || t < best.depth) {
      best.archetype = archetype;
      best.netId = netIds[k];
      best.pixels = 0;
      best.depth = t;
      best.inside = true;
    }
  }
}

/**
 * Ray against the axis-aligned box `[−hx, hx] × [0, sy] × [−hz, hz]`, in the box's own frame.
 *
 * The three slabs are written out rather than looped, because a loop over them needs either an array per call or a
 * shared one — and this runs once per drawn instance per click.
 *
 * @returns Distance along the ray to the first intersection, 0 when the origin is already inside, or −1 for a miss.
 */
function rayBox(
  ox: number,
  oy: number,
  oz: number,
  dx: number,
  dy: number,
  dz: number,
  hx: number,
  sy: number,
  hz: number,
): number {
  let near = -Infinity;
  let far = Infinity;

  if (Math.abs(dx) < 1e-12) {
    if (ox < -hx || ox > hx) {
      return -1;
    }
  } else {
    const a = (-hx - ox) / dx;
    const b = (hx - ox) / dx;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
  }

  if (Math.abs(dy) < 1e-12) {
    if (oy < 0 || oy > sy) {
      return -1;
    }
  } else {
    const a = (0 - oy) / dy;
    const b = (sy - oy) / dy;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
  }

  if (Math.abs(dz) < 1e-12) {
    if (oz < -hz || oz > hz) {
      return -1;
    }
  } else {
    const a = (-hz - oz) / dz;
    const b = (hz - oz) / dz;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
  }

  if (far < near || far < 0) {
    return -1;
  }

  return near < 0 ? 0 : near;
}

/**
 * Picks among packed instances by where they project, for the things a box test cannot settle.
 *
 * Two jobs, and the order between them is the point:
 *
 * 1. **Inside a sprite.** A far-band entity is drawn as a fixed-size screen sprite, so its silhouette is a disc of
 *    `insideRadius` pixels and a cursor within it is on the thing, exactly as a cursor inside a near-band box is.
 *    It competes with {@link pickShapes} on distance along the same ray, which is what makes a creature standing in
 *    front of a building win over the building rather than losing to it for being small.
 * 2. **Near a centre.** When nothing at all was hit, the nearest centre within `maxPixels` wins. This is the old
 *    behaviour and it stays, because a 7-pixel mesh at the band boundary is a fiddly target to hit exactly.
 *
 * `netIds[k]` is the entity packed at `k` — captured at pack time, so a store changed since then cannot redirect a pick.
 *
 * @param insideRadius Sprite radius in pixels, or 0 for the near band, whose silhouette {@link pickShapes} already has.
 */
export function pickInstances(
  ray: PickRay,
  matrix: ArrayLike<number>,
  data: Float32Array,
  netIds: Uint32Array,
  count: number,
  liftByStyle: Float64Array | number,
  insideRadius: number,
  selectedScale: number,
  width: number,
  height: number,
  px: number,
  py: number,
  maxPixels: number,
  archetype: number,
  best: PickHit,
  screen: ScreenPoint,
  maxT = Infinity,
): void {
  for (let k = 0; k < count; k++) {
    const b = k * 4;
    const packed = data[b + 3];
    const lift = typeof liftByStyle === 'number' ? liftByStyle : liftByStyle[packedStyle(packed)];
    // (x, y, z, packed), y being the entity's altitude (CLI3D-04): the lift is measured from the ENTITY, so that a
    // flying thing is picked where it is drawn and not at the point on the ground beneath it.
    // Behind the ground the viewer is looking at. Measured from the entity rather than from its screen position,
    // because an entity's distance is what the occlusion question is about and its pixel offset is not.
    const along = Math.hypot(data[b] - ray.ox, data[b + 1] + lift - ray.oy, data[b + 2] - ray.oz);
    if (along > maxT) {
      continue;
    }

    const s = projectToScreen(matrix, data[b], data[b + 1] + lift, data[b + 2], width, height, screen);
    // A point behind the camera projects to NaN, which is never within reach of either test below.
    const d = Math.hypot(s.x - px, s.y - py);
    const radius = insideRadius * (packed >= SELECTED_BIT ? selectedScale : 1);
    if (radius > 0 && d <= radius) {
      if (!best.inside || along < best.depth) {
        best.archetype = archetype;
        best.netId = netIds[k];
        best.pixels = d;
        best.depth = along;
        best.inside = true;
      }

      continue;
    }

    // Nothing that is merely NEAR a centre may displace something the cursor is actually over.
    if (best.inside) {
      continue;
    }

    if (d <= maxPixels && (d < best.pixels - 0.5 || (Math.abs(d - best.pixels) <= 0.5 && s.w < best.depth))) {
      best.archetype = archetype;
      best.netId = netIds[k];
      best.pixels = d;
      best.depth = s.w;
    }
  }
}
