import type { BaseTexture } from '@babylonjs/core/Materials/Textures/baseTexture';
import { DynamicTexture } from '@babylonjs/core/Materials/Textures/dynamicTexture';
import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import { Vector2 } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import type { Scene } from '@babylonjs/core/scene';

import type { FrameView } from './layer-packer';
import { PrefixUploader } from './prefix-upload';
import { SCENE_GROUP } from './render-groups';
import { DAMAGE_FRAGMENT, DAMAGE_VERTEX } from './shaders';

/** How long a damage number rises and fades, milliseconds (Loïc, 2026-10-24: one second). */
export const DAMAGE_MS = 1000;

/** Past this many metres from the camera a damage number is not shown: a fight across the desert is not read by its numbers. */
export const DAMAGE_RANGE_M = 60;

/** How far a number rises over its second, CSS pixels. */
const RISE_PX = 36;

/** A glyph's height on screen, CSS pixels. */
const GLYPH_PX = 17;

/** The atlas: what a number is spelt with, in column order. */
const GLYPHS = '-0123456789';
const CELL_W = 34;
const CELL_H = 64;

const NUMBERS = 128;
const CAPACITY = 640;

/** Where a body's head is, planet metres: what a damage number rises from. */
export interface Anchor {
  x: number;
  y: number;
  z: number;
}

interface Number_ {
  netId: number;
  born: number;
  text: string;
  live: boolean;
  /** Where it was last seen: a body that leaves the view mid-number keeps its number where it was. */
  readonly at: Anchor;
  seen: boolean;
}

/**
 * The damage a blow did, in red over the body it landed on, rising and fading for a second — drawn IN the scene, so the
 * wall between the camera and the body hides it, and only within {@link DAMAGE_RANGE_M}.
 *
 * <b>One draw call for every number.</b> Each glyph is a thin instance of one quad, spelt from one atlas texture; the
 * shader projects the head and offsets the glyph in screen pixels from it (`DAMAGE_VERTEX`).
 */
export class DamageText {
  /** Glyphs drawn last frame. */
  glyphs = 0;

  private readonly numbers: Number_[] = [];
  private readonly mesh: Mesh;
  private readonly material: ShaderMaterial;
  private readonly atlas: BaseTexture;
  private readonly anchors = new Float32Array(CAPACITY * 4);
  private readonly glyphData = new Float32Array(CAPACITY * 4);
  private readonly anchorsUploader = new PrefixUploader(this.anchors, 4);
  private readonly glyphUploader = new PrefixUploader(this.glyphData, 4);
  private readonly viewport = new Vector2(1, 1);
  private readonly head: Anchor = { x: 0, y: 0, z: 0 };

  /**
   * @param scene The live scene.
   * @param atlas The glyphs, `-0123456789` in equal columns: drawn on a canvas by default; a test without one hands its own.
   */
  constructor(scene: Scene, atlas: BaseTexture = glyphAtlas(scene)) {
    this.atlas = atlas;
    const mesh = new Mesh('damage-text', scene);
    const quad = new VertexData();
    quad.positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0];
    quad.indices = [0, 1, 2, 0, 2, 3];
    quad.applyToMesh(mesh, false);
    mesh.alwaysSelectAsActiveMesh = true;
    mesh.doNotSyncBoundingInfo = true;
    mesh.isPickable = false;
    mesh.renderingGroupId = SCENE_GROUP;
    mesh.thinInstanceSetBuffer('dmgAnchor', this.anchors, 4, false);
    mesh.thinInstanceSetBuffer('dmgGlyph', this.glyphData, 4, false);
    this.mesh = mesh;

    const material = new ShaderMaterial(
      'damage-text',
      scene,
      { vertexSource: DAMAGE_VERTEX, fragmentSource: DAMAGE_FRAGMENT },
      {
        attributes: ['position', 'dmgAnchor', 'dmgGlyph'],
        uniforms: ['viewProjection', 'uViewport', 'uGlyphPx', 'uAspect', 'uColumns'],
        samplers: ['uAtlas'],
        needAlphaBlending: true,
      },
    );
    material.backFaceCulling = false;
    // Tested against the scene, never written: a number must not hide the one behind it.
    material.disableDepthWrite = true;
    material.setTexture('uAtlas', this.atlas);
    material.setFloat('uGlyphPx', GLYPH_PX);
    material.setFloat('uAspect', CELL_W / CELL_H);
    material.setFloat('uColumns', GLYPHS.length);
    mesh.material = material;
    this.material = material;
    this.setCount(0);
  }

  /** A blow landed on `netId` for `amount`: "-amount" rises over its head for {@link DAMAGE_MS}. */
  spawn(netId: number, amount: number, nowMs: number): void {
    let n = this.numbers.find((d) => !d.live);
    if (n === undefined && this.numbers.length < NUMBERS) {
      n = { netId: 0, born: 0, text: '', live: false, at: { x: 0, y: 0, z: 0 }, seen: false };
      this.numbers.push(n);
    }

    n ??= this.numbers.reduce((a, b) => (a.born <= b.born ? a : b));
    n.netId = netId;
    n.born = nowMs;
    n.text = `-${String(Math.max(0, Math.round(amount)))}`;
    n.live = true;
    n.seen = false;
  }

  /**
   * Places this frame's numbers.
   *
   * @param locate Where a body's head is now, planet metres, or false when it is not held.
   */
  update(view: FrameView, nowMs: number, locate: (netId: number, out: Anchor) => boolean): void {
    let g = 0;
    for (const n of this.numbers) {
      if (!n.live) {
        continue;
      }

      const age = (nowMs - n.born) / DAMAGE_MS;
      if (age >= 1) {
        n.live = false;
        continue;
      }

      if (locate(n.netId, this.head)) {
        n.at.x = this.head.x;
        n.at.y = this.head.y;
        n.at.z = this.head.z;
        n.seen = true;
      }

      if (!n.seen) {
        continue;
      }

      const x = n.at.x - view.originX;
      const z = n.at.z - view.originZ;
      if (Math.hypot(x - view.eyeX, n.at.y - view.eyeY, z - view.eyeZ) > DAMAGE_RANGE_M) {
        continue;
      }

      // Out fast and slowing, fading late: readable for most of its second.
      const rise = RISE_PX * (1 - (1 - age) * (1 - age));
      const alpha = 1 - age * age;
      for (let i = 0; i < n.text.length && g < CAPACITY; i++) {
        const b = g * 4;
        this.anchors[b] = x;
        this.anchors[b + 1] = n.at.y;
        this.anchors[b + 2] = z;
        this.anchors[b + 3] = alpha;
        this.glyphData[b] = GLYPHS.indexOf(n.text[i]);
        this.glyphData[b + 1] = i;
        this.glyphData[b + 2] = n.text.length;
        this.glyphData[b + 3] = rise;
        g++;
      }
    }

    this.anchorsUploader.upload(this.mesh.getVertexBuffer('dmgAnchor')?.getWrapperBuffer() ?? null, g);
    this.glyphUploader.upload(this.mesh.getVertexBuffer('dmgGlyph')?.getWrapperBuffer() ?? null, g);
    this.viewport.set(view.viewportWidth, view.viewportHeight);
    this.material.setVector2('uViewport', this.viewport);
    this.setCount(g);
  }

  dispose(): void {
    this.material.dispose();
    this.atlas.dispose();
    this.mesh.dispose();
  }

  private setCount(n: number): void {
    this.glyphs = n;
    this.mesh.forcedInstanceCount = n;
    this.mesh.isVisible = n > 0;
  }
}

/** The glyphs, drawn once: red with a dark outline, one cell each. */
function glyphAtlas(scene: Scene): DynamicTexture {
  const texture = new DynamicTexture(
    'damage-glyphs',
    { width: CELL_W * GLYPHS.length, height: CELL_H },
    scene,
    true,
    Texture.TRILINEAR_SAMPLINGMODE,
  );
  texture.hasAlpha = true;
  texture.wrapU = Texture.CLAMP_ADDRESSMODE;
  texture.wrapV = Texture.CLAMP_ADDRESSMODE;
  const ctx = texture.getContext() as CanvasRenderingContext2D;
  ctx.clearRect(0, 0, CELL_W * GLYPHS.length, CELL_H);
  ctx.font = `800 ${String(CELL_H * 0.78)}px system-ui, sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.lineJoin = 'round';
  for (let i = 0; i < GLYPHS.length; i++) {
    const cx = CELL_W * (i + 0.5);
    const cy = CELL_H * 0.55;
    ctx.lineWidth = 8;
    ctx.strokeStyle = 'rgba(20, 0, 0, 0.9)';
    ctx.strokeText(GLYPHS[i], cx, cy);
    ctx.fillStyle = '#ff4b3e';
    ctx.fillText(GLYPHS[i], cx, cy);
  }

  texture.update(true);
  return texture;
}
