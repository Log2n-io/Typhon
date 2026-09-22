/**
 * Unit shapes for the near band: every shape spans x and z in [−0.5, 0.5] and y in [0, 1], facing +Z, so one per-instance
 * size (width, height, length) and one yaw place it. Flat-shaded: each face owns its vertices, so its normal is exact.
 * Faces lying on the ground (y = 0) are left out: the camera is always above it.
 */

export type ShapeKind = 'box' | 'arrow' | 'prism' | 'mound' | 'quad';

export interface ShapeData {
  positions: number[];
  normals: number[];
  indices: number[];
}

function face(b: ShapeData, corners: readonly (readonly [number, number, number])[]): void {
  const [a, c, d] = [corners[0], corners[1], corners[2]];
  const ux = c[0] - a[0];
  const uy = c[1] - a[1];
  const uz = c[2] - a[2];
  const vx = d[0] - a[0];
  const vy = d[1] - a[1];
  const vz = d[2] - a[2];
  // Corners are listed so that u × v, the face normal, points outward.
  let nx = uy * vz - uz * vy;
  let ny = uz * vx - ux * vz;
  let nz = ux * vy - uy * vx;
  const len = Math.hypot(nx, ny, nz) || 1;
  nx /= len;
  ny /= len;
  nz /= len;
  const base = b.positions.length / 3;
  for (const p of corners) {
    b.positions.push(p[0], p[1], p[2]);
    b.normals.push(nx, ny, nz);
  }

  // The fan is emitted in reverse, which makes the triangles counter-clockwise on screen seen from outside: the front face
  // Babylon keeps for a left-handed scene.
  for (let k = 1; k + 1 < corners.length; k++) {
    b.indices.push(base, base + k + 1, base + k);
  }
}

function box(b: ShapeData): void {
  const x0 = -0.5;
  const x1 = 0.5;
  const z0 = -0.5;
  const z1 = 0.5;
  face(b, [
    [x0, 1, z0],
    [x0, 1, z1],
    [x1, 1, z1],
    [x1, 1, z0],
  ]);
  face(b, [
    [x0, 0, z0],
    [x0, 1, z0],
    [x1, 1, z0],
    [x1, 0, z0],
  ]);
  face(b, [
    [x1, 0, z1],
    [x1, 1, z1],
    [x0, 1, z1],
    [x0, 0, z1],
  ]);
  face(b, [
    [x0, 0, z1],
    [x0, 1, z1],
    [x0, 1, z0],
    [x0, 0, z0],
  ]);
  face(b, [
    [x1, 0, z0],
    [x1, 1, z0],
    [x1, 1, z1],
    [x1, 0, z1],
  ]);
}

/** A wedge pointing +Z: reads as a heading from above and from the side. */
function arrow(b: ShapeData): void {
  const tip: [number, number, number] = [0, 0.35, 0.5];
  const bl: [number, number, number] = [-0.5, 0, -0.5];
  const br: [number, number, number] = [0.5, 0, -0.5];
  const tl: [number, number, number] = [-0.35, 1, -0.5];
  const tr: [number, number, number] = [0.35, 1, -0.5];
  face(b, [tl, tip, tr]);
  face(b, [bl, tl, tr, br]);
  face(b, [bl, tip, tl]);
  face(b, [br, tr, tip]);
  // The underside rises from the tail to the nose: visible from a low camera ahead of the creature.
  face(b, [bl, br, tip]);
}

/** A hexagonal prism: people. */
function prism(b: ShapeData, sides: number): void {
  const ring: [number, number][] = [];
  for (let s = 0; s < sides; s++) {
    const a = (s / sides) * Math.PI * 2;
    ring.push([Math.sin(a) * 0.5, Math.cos(a) * 0.5]);
  }

  const top: [number, number, number][] = ring.map(([x, z]) => [x, 1, z]);
  face(b, top);
  for (let s = 0; s < sides; s++) {
    const [ax, az] = ring[s];
    const [cx, cz] = ring[(s + 1) % sides];
    face(b, [
      [cx, 0, cz],
      [cx, 1, cz],
      [ax, 1, az],
      [ax, 0, az],
    ]);
  }
}

/** A low truncated cone: lairs. */
function mound(b: ShapeData, sides: number): void {
  const ring = (radius: number, y: number): [number, number, number][] => {
    const out: [number, number, number][] = [];
    for (let s = 0; s < sides; s++) {
      const a = (s / sides) * Math.PI * 2;
      out.push([Math.sin(a) * radius, y, Math.cos(a) * radius]);
    }

    return out;
  };
  const bottom = ring(0.5, 0);
  const top = ring(0.25, 1);
  face(b, top);
  for (let s = 0; s < sides; s++) {
    const n = (s + 1) % sides;
    face(b, [bottom[n], top[n], top[s], bottom[s]]);
  }
}

/** A unit quad in xy, corners at ±1: the far band's screen-facing sprite, expanded in the vertex shader. */
function quad(b: ShapeData): void {
  b.positions.push(-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0);
  b.normals.push(0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1);
  b.indices.push(0, 2, 1, 0, 3, 2);
}

/** Vertices, normals and indices of a unit shape. */
export function buildShape(kind: ShapeKind): ShapeData {
  const b: ShapeData = { positions: [], normals: [], indices: [] };
  switch (kind) {
    case 'box':
      box(b);
      break;
    case 'arrow':
      arrow(b);
      break;
    case 'prism':
      prism(b, 6);
      break;
    case 'mound':
      mound(b, 8);
      break;
    case 'quad':
      quad(b);
      break;
  }

  return b;
}
