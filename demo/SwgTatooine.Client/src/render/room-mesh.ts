import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import type { Scene } from '@babylonjs/core/scene';

/**
 * How tall a room is drawn, metres.
 *
 * <b>Invented, and it has to be.</b> An interior realm's bounds are a flat 64 × 64 m square — `SpatialGridConfig.Flat`
 * leaves a degenerate slab on the third axis and the simulation puts everything at altitude 0 — so the server has no
 * opinion about height at all. Four metres is a room a person stands in, and it is the one number here that is a choice
 * rather than a fact.
 */
const ROOM_HEIGHT_M = 4;

/**
 * The inside of a box: a floor at `y = 0` and four walls, with no ceiling.
 *
 * <b>Wound inward.</b> Every face's triangles are ordered so their normals point into the room, because that is the only
 * side anything looks at it from. The alternative — an outward box with back-face culling off — draws the far walls in
 * front of the near ones and lights every surface from the wrong side.
 *
 * <b>No ceiling, deliberately.</b> A map camera looks down into a room; a lid would be an opaque quad between the eye
 * and everything in it, and an interior is one cell so there is no case where the camera is legitimately above a
 * neighbouring room looking across.
 *
 * Positions are in ROOM space — `(0,0,0)` to `(width, height, depth)` — not centred, which matches the realm bounds an
 * interior actually has (`(0,0)…(64,64)`, `WorldBuilder.InteriorEdgeM`). The mesh is placed in world space by its own
 * position, so nothing here needs the realm's origin.
 */
export function buildRoom(width: number, depth: number, height = ROOM_HEIGHT_M): VertexData {
  return buildInward(width, height, depth, false);
}

/**
 * A closed box seen from the inside: the room's five faces plus a lid.
 *
 * <b>Space uses this and a room does not.</b> A map camera looks down INTO a room, so a ceiling there is an opaque quad
 * between the eye and everything in it. Space is a realm the camera is genuinely inside — its bounds run below zero on
 * every axis and the eye orbits within them — so every direction needs something to draw, or looking up gives the
 * clear colour and nothing else.
 *
 * Positions run `(0,0,0)` to `(width, height, depth)`, and the caller places the box in the world.
 */
export function buildBox(width: number, height: number, depth: number): VertexData {
  return buildInward(width, height, depth, true);
}

function buildInward(width: number, height: number, depth: number, ceiling: boolean): VertexData {
  const positions: number[] = [];
  const normals: number[] = [];
  const uvs: number[] = [];
  const indices: number[] = [];

  /** One quad, wound so that `(a→b→c)` faces `normal`. `uv` runs 0..1 across it for the grid to read. */
  const quad = (
    ax: number, ay: number, az: number,
    bx: number, by: number, bz: number,
    cx: number, cy: number, cz: number,
    dx: number, dy: number, dz: number,
    nx: number, ny: number, nz: number,
  ): void => {
    const base = positions.length / 3;
    positions.push(ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz);
    for (let i = 0; i < 4; i++) {
      normals.push(nx, ny, nz);
    }

    uvs.push(0, 0, 1, 0, 1, 1, 0, 1);
    indices.push(base, base + 1, base + 2, base, base + 2, base + 3);
  };

  const w = width;
  const d = depth;
  const h = height;

  // The floor, facing up.
  quad(0, 0, 0, w, 0, 0, w, 0, d, 0, 0, d, 0, 1, 0);

  if (ceiling) {
    // And a lid, facing down. Wound the other way round the same corners.
    quad(0, h, d, w, h, d, w, h, 0, 0, h, 0, 0, -1, 0);
  }

  // The four walls, each facing inward.
  quad(0, 0, 0, 0, h, 0, w, h, 0, w, 0, 0, 0, 0, 1);
  quad(w, 0, d, w, h, d, 0, h, d, 0, 0, d, 0, 0, -1);
  quad(0, 0, d, 0, h, d, 0, h, 0, 0, 0, 0, 1, 0, 0);
  quad(w, 0, 0, w, h, 0, w, h, d, w, 0, d, -1, 0, 0);

  const data = new VertexData();
  data.positions = positions;
  data.normals = normals;
  data.uvs = uvs;
  data.indices = indices;
  return data;
}

/** The room as a mesh, at the origin of room space. The caller positions it in the world. */
export function createRoomMesh(name: string, scene: Scene, width: number, depth: number): Mesh {
  return meshOf(name, scene, buildRoom(width, depth));
}

/** A closed box as a mesh, seen from inside: what space is drawn on. */
export function createBoxMesh(name: string, scene: Scene, width: number, height: number, depth: number): Mesh {
  const mesh = meshOf(name, scene, buildBox(width, height, depth));

  // The camera lives inside this box and the box IS the world's edge, so it must never be culled for being behind the
  // near plane of its own interior.
  mesh.alwaysSelectAsActiveMesh = true;
  return mesh;
}

function meshOf(name: string, scene: Scene, data: VertexData): Mesh {
  const mesh = new Mesh(name, scene);
  // Not updatable: neither the room nor the box is ever re-meshed — a realm of another size is SCALED. `true` keeps a
  // CPU copy and a DYNAMIC_DRAW buffer for geometry that is written once.
  data.applyToMesh(mesh, false);
  mesh.isPickable = false;
  return mesh;
}
