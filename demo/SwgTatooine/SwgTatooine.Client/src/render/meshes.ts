import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import type { Scene } from '@babylonjs/core/scene';
import { buildShape, type ShapeKind } from './shapes';

export type { ShapeKind } from './shapes';

export function createShapeMesh(name: string, kind: ShapeKind, scene: Scene): Mesh {
  const b = buildShape(kind);
  const mesh = new Mesh(name, scene);
  const data = new VertexData();
  data.positions = b.positions;
  data.normals = b.normals;
  data.indices = b.indices;
  data.applyToMesh(mesh, false);
  // Instances are placed by the shader from per-instance data: the mesh's own bounds mean nothing, and the renderer
  // culls on the CPU before packing.
  mesh.alwaysSelectAsActiveMesh = true;
  mesh.doNotSyncBoundingInfo = true;
  mesh.isPickable = false;
  return mesh;
}
