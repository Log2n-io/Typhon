import { CITIES, PLANET_HALF_EXTENT_M, POIS } from '../data/world-data';
import { NODE_GRID } from './terrain-mesh';
import { SELECTED_MESH_SCALE, SELECTED_SPRITE_SCALE, STYLE_COUNT, TINT_COUNT } from './styles';
import { SELECTED_BIT, STYLE_LIMIT } from './view-math';

/**
 * GLSL for the four materials. Babylon's WebGL2 engine converts this WebGL1-style source (attribute / varying /
 * texture2D) to GLSL ES 3.00. Array sizes, the packing layout and the planet extent come from the TypeScript constants,
 * so the two sides cannot drift.
 *
 * Per-instance data is one vec4 packed by the CPU each frame (`entity-layer.ts`):
 *   x, z   — position relative to the render origin (metres; small, so float32 is exact near the camera)
 *   yaw    — radians around +Y, 0 facing +Z
 *   packed — small integer: style index, mode, selected (`packState` in `view-math.ts`)
 */

const f = (v: number): string => (Number.isInteger(v) ? `${v}.0` : `${v}`);

const UNPACK = `
  float packed = instData.w;
  float styleIndex = mod(packed, ${f(STYLE_LIMIT)});
  float mode = mod(floor(packed / ${f(STYLE_LIMIT)}), ${f(SELECTED_BIT / STYLE_LIMIT)});
  float selected = mod(floor(packed / ${f(SELECTED_BIT)}), 2.0);
`;

const SELECTED_COLOR = 'vec3(1.0, 0.95, 0.6)';

export const ENTITY_VERTEX = `
precision highp float;
attribute vec3 position;
attribute vec3 normal;
attribute vec4 instData;
attribute float instYaw;
uniform mat4 viewProjection;
uniform vec3 uColors[${STYLE_COUNT}];
uniform vec3 uSizes[${STYLE_COUNT}];
uniform vec4 uTints[${TINT_COUNT}];
uniform float uFlattenMode;
varying vec3 vColor;
varying vec3 vNormal;
varying float vHeight;

void main(void) {
  ${UNPACK}
  vec3 size = uSizes[int(styleIndex)] * (1.0 + selected * ${f(SELECTED_MESH_SCALE - 1)});
  if (mode == uFlattenMode) {
    size.y *= 0.25;
  }

  // instData is (x, y, z, packed) with y the entity's altitude — 0 in a flat realm, so this is the old placement there.
  float c = cos(instYaw);
  float s = sin(instYaw);
  vec3 local = position * size;
  vec3 turned = vec3(local.x * c + local.z * s, local.y, -local.x * s + local.z * c);
  gl_Position = viewProjection * vec4(instData.xyz + turned, 1.0);

  // A normal transforms by the inverse transpose: for a scale, divide by it.
  vec3 n = normalize(normal / size);
  vNormal = vec3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c);
  vec4 tint = uTints[int(mode)];
  vec3 color = mix(uColors[int(styleIndex)], tint.rgb, tint.a);
  vColor = mix(color, ${SELECTED_COLOR}, selected * 0.55);
  vHeight = position.y;
}
`;

export const ENTITY_FRAGMENT = `
precision highp float;
uniform vec3 uSunDirection;
varying vec3 vColor;
varying vec3 vNormal;
varying float vHeight;

void main(void) {
  vec3 n = normalize(vNormal);
  float sun = max(dot(n, -uSunDirection), 0.0);
  float sky = 0.5 + 0.5 * n.y;
  vec3 lit = vColor * (0.32 + 0.55 * sun + 0.18 * sky);
  // A darker foot anchors a shape to the ground at a distance.
  lit *= 0.8 + 0.2 * smoothstep(0.0, 0.3, vHeight);
  gl_FragColor = vec4(lit, 1.0);
}
`;

/** Sprites are sized in CSS pixels: `uViewport` is the canvas's CSS size, whatever the device pixel ratio. */
export const SPRITE_VERTEX = `
precision highp float;
attribute vec3 position;
attribute vec4 instData;
uniform mat4 viewProjection;
uniform vec2 uViewport;
uniform vec3 uColors[${STYLE_COUNT}];
uniform vec4 uTints[${TINT_COUNT}];
uniform float uPixels;
uniform float uLift;
varying vec3 vColor;
varying vec2 vCorner;

void main(void) {
  ${UNPACK}
  // The lift is above the ENTITY, not above the ground: at altitude the dot follows it up.
  vec4 clip = viewProjection * vec4(instData.x, instData.y + uLift, instData.z, 1.0);
  float pixels = uPixels * (1.0 + selected * ${f(SELECTED_SPRITE_SCALE - 1)});
  clip.xy += position.xy * (pixels / uViewport) * clip.w;
  gl_Position = clip;
  vec4 tint = uTints[int(mode)];
  vec3 color = mix(uColors[int(styleIndex)], tint.rgb, tint.a);
  vColor = mix(color, ${SELECTED_COLOR}, selected * 0.55);
  vCorner = position.xy;
}
`;

export const SPRITE_FRAGMENT = `
precision highp float;
varying vec3 vColor;
varying vec2 vCorner;

void main(void) {
  float r = dot(vCorner, vCorner);
  if (r > 1.0) {
    discard;
  }

  gl_FragColor = vec4(vColor * (1.0 - 0.35 * r), 1.0);
}
`;

/**
 * Screen-space quads between two points, or — for a hit marker — a short diagonal through one point, a fixed number of CSS
 * pixels long (two instances, one per diagonal, make a cross). An end behind the camera is first moved along the segment to
 * just in front of it: dividing by a negative w would mirror it and turn the quad inside out.
 */
export const LINE_VERTEX = `
precision highp float;
attribute vec3 position;
attribute vec4 lineEnds;
attribute vec2 lineStyle;
/** Each end's altitude; 0 in a flat realm, so uHeight alone is the old behaviour (CLI3D-04). */
attribute vec2 lineHeights;
uniform mat4 viewProjection;
uniform vec2 uViewport;
uniform float uWidth;
uniform float uHeight;
varying vec4 vColor;

const float MIN_W = 0.01;

void main(void) {
  // lineStyle.x: alpha. lineStyle.y: 0 for a line; for a marker, its half-diagonal in CSS pixels, the sign picking the diagonal.
  vec4 a = viewProjection * vec4(lineEnds.x, lineHeights.x + uHeight, lineEnds.y, 1.0);
  vec4 b;
  float marker = lineStyle.y;
  if (marker != 0.0) {
    if (a.w < MIN_W) {
      gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
      vColor = vec4(0.0);
      return;
    }

    vec2 d = vec2(abs(marker), marker) * 2.0 / uViewport * a.w;
    b = a;
    a.xy -= d;
    b.xy += d;
  } else {
    b = viewProjection * vec4(lineEnds.z, lineHeights.y + uHeight, lineEnds.w, 1.0);
    if (a.w < MIN_W && b.w < MIN_W) {
      gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
      vColor = vec4(0.0);
      return;
    }

    if (a.w < MIN_W) {
      a = mix(a, b, (MIN_W - a.w) / (b.w - a.w));
    } else if (b.w < MIN_W) {
      b = mix(b, a, (MIN_W - b.w) / (a.w - b.w));
    }
  }

  // position.x in {0, 1} picks the end, position.y in {-1, 1} the side; width in CSS pixels.
  vec4 p = mix(a, b, position.x);
  vec2 sa = a.xy / a.w * uViewport;
  vec2 sb = b.xy / b.w * uViewport;
  vec2 dir = normalize(sb - sa + vec2(1e-6, 0.0));
  vec2 side = vec2(-dir.y, dir.x);
  p.xy += side * position.y * (uWidth / uViewport) * p.w;
  gl_Position = p;
  // Attacker end yellow, target end red; a marker is all red.
  float towardTarget = marker != 0.0 ? 1.0 : position.x;
  vColor = vec4(mix(vec3(1.0, 0.85, 0.3), vec3(1.0, 0.25, 0.15), towardTarget), lineStyle.x);
}
`;

export const LINE_FRAGMENT = `
precision highp float;
varying vec4 vColor;

void main(void) {
  gl_FragColor = vColor;
}
`;

/**
 * Bilinear sampling of the height field, spelled out rather than left to the sampler.
 *
 * `uField` is `(originM, spacingM, posts, 1 / posts)` and the texture is R32F sampled NEAREST, so these four taps and the
 * two `mix`es are the **same four texels and the same weights** as `Heightfield.heightAt` on the CPU. Hardware filtering of
 * a float texture is not specified to agree even that far, and the whole design rests on the GPU's displaced ground and
 * the CPU's entity altitudes landing in the same place.
 *
 * It is not bit-identical and does not need to be: GLSL `mix(x, y, a)` is `x·(1−a) + y·a` where the CPU computes
 * `x + (y − x)·a`, and the CPU works in float64. **Measured worst divergence over ±140 m of relief: 1.25e-5 m.**
 */
const HEIGHT_SAMPLER = `
uniform sampler2D uHeight;
uniform vec4 uField;

float heightAt(vec2 planet) {
  float last = uField.z - 1.0;
  vec2 f = clamp((planet - uField.x) / uField.y, vec2(0.0), vec2(last));
  vec2 i0 = floor(f);
  vec2 t = f - i0;
  vec2 i1 = min(i0 + 1.0, vec2(last));
  vec2 uvA = (i0 + 0.5) * uField.w;
  vec2 uvB = (i1 + 0.5) * uField.w;
  float h00 = texture2D(uHeight, vec2(uvA.x, uvA.y)).r;
  float h10 = texture2D(uHeight, vec2(uvB.x, uvA.y)).r;
  float h01 = texture2D(uHeight, vec2(uvA.x, uvB.y)).r;
  float h11 = texture2D(uHeight, vec2(uvB.x, uvB.y)).r;
  return mix(mix(h00, h10, t.x), mix(h01, h11, t.x), t.y);
}

/*
 * The ground's normal at a planet coordinate, from a central difference at the FIELD's post spacing.
 *
 * This is a function of the PLANET, and of nothing else. That is the whole point of computing it here rather than at the
 * vertices: the mesh is camera-centred, so its vertices sit on different planet coordinates every time the camera moves,
 * and a per-vertex normal interpolated across triangles tens to hundreds of metres wide therefore CRAWLED — a zig-zag of
 * false shadows that slid over the ground as you flew, tracking the triangulation rather than the terrain. Sampling per
 * pixel makes the shading stand still, and makes it independent of how finely the mesh happens to be tessellated there.
 */
vec3 groundNormal(vec2 planet) {
  float e = uField.y;
  float dx = heightAt(planet + vec2(e, 0.0)) - heightAt(planet - vec2(e, 0.0));
  float dz = heightAt(planet + vec2(0.0, e)) - heightAt(planet - vec2(0.0, e));
  return normalize(vec3(-dx, 2.0 * e, -dz));
}
`;

export const GROUND_VERTEX = `
precision highp float;
attribute vec3 position;
/** Per instance: the node's planet origin, its size in metres, spare. */
attribute vec4 nodeData;
/** Per instance: where the morph toward the parent grid begins and ends, in metres of camera distance. */
attribute vec2 nodeMorph;
uniform mat4 world;
uniform mat4 viewProjection;
/** Camera in planet metres (xz) and its altitude (y), for the morph's distance. */
uniform vec3 uCameraPlanet;
varying vec2 vPlanet;
varying vec3 vRender;
varying float vHeightM;
${HEIGHT_SAMPLER}

void main(void) {
  // The vertex is a FRACTION of its node, and the node is a fixed cell of the world — so this planet coordinate is a
  // constant of the terrain, not of the camera. That is the whole reason for the quadtree: two earlier meshes were
  // centred on the camera, and their vertices slid across the ground whenever it moved sideways.
  vec2 grid = position.xz;
  vec2 planet = nodeData.xy + grid * nodeData.z;

  // CDLOD morph. Approaching the distance at which this node is replaced by its four children, blend each vertex onto the
  // grid its PARENT has — drop the odd half of the grid coordinate — so the swap, when it comes, moves nothing. This is
  // what closes the T-junction at a level boundary and removes the pop, without a stitching strip or a skirt.
  float toCamera = distance(vec3(planet.x, heightAt(planet), planet.y), uCameraPlanet);
  float morph = clamp((toCamera - nodeMorph.x) / max(nodeMorph.y - nodeMorph.x, 1e-3), 0.0, 1.0);
  vec2 coarse = floor(grid * ${f(NODE_GRID)} * 0.5 + 0.5) * 2.0 / ${f(NODE_GRID)};
  planet = nodeData.xy + mix(grid, coarse, morph) * nodeData.z;

  float h = heightAt(planet);
  vec4 w = world * vec4(planet.x, h, planet.y, 1.0);
  vPlanet = planet;
  vHeightM = h;
  vRender = w.xyz;
  gl_Position = viewProjection * w;
}
`;

/**
 * The inside of a room: a floor grid at the realm's own replication cell, and plain walls.
 *
 * <b>Its own program rather than a branch in the ground's.</b> An interior has no heightfield, so the whole CDLOD vertex
 * path, the height sampler, the fog curve, the heatmap and the place discs are dead here — that is most of
 * `GROUND_FRAGMENT`, not a corner of it, and the planet is the one profile whose ground fills the screen and would pay
 * the dead ALU.
 */
export const ROOM_VERTEX = `
precision highp float;
attribute vec3 position;
attribute vec3 normal;
uniform mat4 world;
uniform mat4 viewProjection;
varying vec3 vRoom;
varying vec3 vNormal;
void main() {
  vec4 w = world * vec4(position, 1.0);

  // WORLD metres, not the mesh's own local space. The room geometry is authored at a nominal edge and SCALED per
  // realm, so a grid measured in local units draws one cell per room whatever the room's size — a 200 m realm would
  // get a single 200 m "cell" for a 64 m replication cell. In world space the lines land on the realm's own cell
  // boundaries, which is what the grid is claiming to show.
  vRoom = w.xyz;
  vNormal = normal;
  gl_Position = viewProjection * w;
}
`;

export const ROOM_FRAGMENT = `
precision highp float;
uniform vec3 uFloor;
uniform vec3 uWall;
uniform float uCellM;
uniform float uGrid;
varying vec3 vRoom;
varying vec3 vNormal;

/**
 * A line of constant screen width wherever the surface is, so the grid does not alias into a moiré at a distance.
 *
 * The derivative is CAPPED, for the reason GROUND_FRAGMENT caps its own: at a grazing angle one pixel spans a long
 * stretch of surface and an uncapped fwidth() smears a line into a band. A room seen from a low pitch is exactly that
 * case.
 */
float line(float v, float spacing) {
  float g = abs(fract(v / spacing - 0.5) - 0.5) / min(fwidth(v / spacing), 4.0);
  return 1.0 - clamp(g, 0.0, 1.0);
}

void main() {
  bool floorFace = vNormal.y > 0.5;
  vec3 color = floorFace ? uFloor : uWall;

  // The grid is the realm's OWN cell, so what it draws is the replication cell an interior is served as — one square,
  // which is the point: it is the picture of "this whole realm is a single cell".
  //
  // Computed UNCONDITIONALLY and applied by a mask. line() calls fwidth(), whose value is undefined in non-uniform
  // control flow: floorFace varies per fragment, so a 2x2 quad straddling the floor/wall seam would have had some of
  // its derivatives evaluated and some not. uGrid is a uniform and would have been safe on its own; the face test is
  // what made it illegal.
  float g = max(line(vRoom.x, uCellM), line(vRoom.z, uCellM));
  float fine = max(line(vRoom.x, uCellM * 0.125), line(vRoom.z, uCellM * 0.125));
  if (floorFace && uGrid > 0.5) {
    color = mix(color, color * 0.55, fine * 0.35);
    color = mix(color, color * 0.35, g * 0.8);
  }

  // A flat wash by face, so the corners of the room read without a light in it. Walls facing the default view are
  // lifted slightly; the floor keeps its own colour so the grid stays legible.
  float shade = floorFace ? 1.0 : 0.82 + 0.18 * abs(vNormal.z);
  gl_FragColor = vec4(color * shade, 1.0);
}
`;

/**
 * Space: a star field, the realm's own cell grid, and a bright edge where the bounds meet.
 *
 * <b>All three on one box, seen from inside.</b> Space has no ground and no heightfield, so the ground program is dead
 * here — and rather than a star mesh, a wire mesh and three grid quads, everything is a function of where you are on the
 * inside surface of the realm's own bounds. One mesh, one material, one draw call, and the wire box is the realm's
 * extent by construction rather than by a constant that could drift from it.
 */
export const SPACE_VERTEX = `
precision highp float;
attribute vec3 position;
attribute vec3 normal;
attribute vec2 uv;
uniform mat4 world;
uniform mat4 viewProjection;
varying vec2 vFace;
varying vec3 vWorld;
varying vec3 vNormal;
void main() {
  vec4 w = world * vec4(position, 1.0);
  vFace = uv;

  // World metres as well as face coordinates: the rim and the stars belong to a FACE, the grid belongs to the REALM.
  // Measuring the grid in uv would draw the same number of cells on every face however large the realm is, and on a
  // realm that is not a cube it would draw a different cell size on each one.
  vWorld = w.xyz;
  vNormal = normal;
  gl_Position = viewProjection * w;
}
`;

export const SPACE_FRAGMENT = `
precision highp float;
uniform vec3 uGridColor;
uniform float uCellM;
uniform float uGrid;
varying vec2 vFace;
varying vec3 vWorld;
varying vec3 vNormal;

float hash(vec2 p) {
  return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

/**
 * A line of constant screen width, so the grid does not alias into a moiré across a 16 km face.
 *
 * The derivative is capped for the reason GROUND_FRAGMENT caps its own, and space is the worst case for it: the camera
 * is INSIDE the box, so the far end of every face is always at a grazing angle.
 */
float line(float v) {
  float g = abs(fract(v - 0.5) - 0.5) / min(fwidth(v), 4.0);
  return 1.0 - clamp(g, 0.0, 1.0);
}

void main() {
  // Stars: one candidate per cell of a fine lattice over the face, most of them rejected, so the field is sparse and
  // stable rather than a uniform speckle. Hashed from face coordinates, so it does not swim as the camera moves.
  //
  // The face's own NORMAL seeds the hash as well, or all six faces would carry the same field — mirrored across every
  // edge, which inside a cube reads as structure rather than as sky.
  vec2 star = vFace * 420.0;
  vec2 cell = floor(star) + vNormal.xy * 37.0 + vNormal.z * 91.0;
  float pick = hash(cell);
  vec3 color = vec3(0.02, 0.025, 0.045);
  if (pick > 0.983) {
    vec2 at = fract(star) - vec2(hash(cell + 11.0), hash(cell + 23.0));
    float d = length(at);
    float brightness = 0.35 + 0.65 * hash(cell + 41.0);
    color += vec3(brightness) * (1.0 - smoothstep(0.0, 0.35, d));
  }

  if (uGrid > 0.5) {
    // The realm's OWN replication cell, in world metres. This is the picture of how space is partitioned, at the size
    // the server actually partitions it — 500 m cells, 32 across a 16 km cube.
    //
    // Two of the three axes per face: the one the face is perpendicular to is constant across it and would draw a
    // single line or none at all.
    vec3 axis = vec3(line(vWorld.x / uCellM), line(vWorld.y / uCellM), line(vWorld.z / uCellM));
    vec3 keep = 1.0 - abs(vNormal);
    float g = max(max(axis.x * keep.x, axis.y * keep.y), axis.z * keep.z);
    color = mix(color, uGridColor, g * 0.25);
  }

  // The bounds themselves: a bright edge wherever a face runs out. This IS the wire box — the realm's extent, drawn
  // from the geometry that has it, not from a number kept beside it.
  vec2 edge = min(vFace, 1.0 - vFace);
  float rim = 1.0 - smoothstep(0.0, 0.004, min(edge.x, edge.y));
  color = mix(color, uGridColor, rim * 0.85);

  gl_FragColor = vec4(color, 1.0);
}
`;

const PLANET = f(PLANET_HALF_EXTENT_M);

/** Slots in the ground shader's hull array: the engine's own `PushGeometry.MaxVertices`, so a hull always fits whole. */
export const HULL_SLOTS = 16;

export const GROUND_FRAGMENT = `
precision highp float;
uniform vec3 uCamera;
uniform vec2 uFog;
uniform vec4 uCities[${CITIES.length}];
uniform vec4 uPois[${POIS.length}];
uniform sampler2D uHeat;
uniform vec4 uHeatRect;
uniform vec4 uHeatMask;
uniform float uHeatNear;
uniform vec2 uViewCenter;
uniform float uHeatAlpha;
uniform float uGrid;
uniform vec4 uSelection;
uniform vec3 uSkyColor;
uniform sampler2D uCells;
uniform vec4 uCellRect;
uniform float uCellAlpha;
uniform vec4 uReplGrid;
uniform vec3 uPalette0;
uniform vec3 uPalette1;
uniform vec3 uPalette2;
uniform vec4 uHull[${HULL_SLOTS}];
uniform float uHullCount;
uniform vec4 uSessionDisc;
varying vec2 vPlanet;
varying vec3 vRender;
varying float vHeightM;
${HEIGHT_SAMPLER}

float hash(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

float noise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float gridLine(vec2 p, float cell, float widthPx) {
  vec2 g = abs(fract(p / cell - 0.5) - 0.5) * cell;
  // Capped for the same reason as the ring footprint below: on a slope at a grazing angle an uncapped fwidth turns a grid
  // line into a wash.
  vec2 w = min(fwidth(p), vec2(24.0)) * widthPx;
  vec2 l = 1.0 - smoothstep(vec2(0.0), w, g);
  return max(l.x, l.y);
}

float ring(float d, float radius, float widthPx, float footprint) {
  return 1.0 - smoothstep(0.0, footprint * widthPx, abs(d - radius));
}

// Distance from p to the segment ab, for the session hull's edges.
float segment(vec2 p, vec2 a, vec2 b) {
  vec2 ab = b - a;
  vec2 ap = p - a;
  float t = clamp(dot(ap, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
  return distance(p, a + ab * t);
}

vec3 heatRamp(float t) {
  vec3 cold = vec3(0.10, 0.30, 0.85);
  vec3 mid = vec3(0.95, 0.85, 0.20);
  vec3 hot = vec3(0.95, 0.15, 0.10);
  return t < 0.5 ? mix(cold, mid, t * 2.0) : mix(mid, hot, (t - 0.5) * 2.0);
}

void main(void) {
  float edge = max(abs(vPlanet.x), abs(vPlanet.y));
  // The ground's three colours are uniforms, not literals: a further planet is the same terrain under a different
  // palette (decision 3 — one bake for every planet), so this is what makes planet 1 read as somewhere else.
  vec3 sand = uPalette0;
  // The fine-grain sand noise stays, at half its old weight: it used to carry ALL of the ground's variation and now only
  // has to break up the surface between real landforms.
  float dunes = noise(vPlanet * 0.011) * 0.65 + noise(vPlanet * 0.09) * 0.35;
  vec3 color = sand * (0.9 + 0.14 * dunes);

  // Where the ground is steep it is rock, not sand — the one colour cue that makes relief legible without a texture, and
  // it is free because the normal is already here. one minus n.y is the sine of the slope angle.
  vec3 ground = groundNormal(vPlanet);
  // Saturating at ~50 deg, not ~19, and at half the strength. At the old settings every modest slope took the full rock
  // colour, so a ridge read as a uniform dark ribbon instead of a lit face beside a shaded one — the tint was doing the
  // work the lighting should do, and drowning it.
  float steep = clamp((1.0 - ground.y) * 1.5, 0.0, 1.0);
  color = mix(color, uPalette1, steep * 0.45);
  // And a pale crest tint high up, which reads as sun-bleached stone and separates the mesas from the flats.
  color = mix(color, uPalette2, clamp((vHeightM - 12.0) / 30.0, 0.0, 1.0) * 0.35);

  // fwidth(vPlanet) is the antialiasing footprint for every line and ring below, and on a slope seen at a grazing angle
  // it explodes: one pixel spans a long stretch of ground. Left alone, a selection ring smears into a band. Capped, a ring
  // at a grazing angle merely gets as wide as it would on flat ground — which is what the eye expects.
  float footprint = min(length(fwidth(vPlanet)), 24.0);
  for (int i = 0; i < ${CITIES.length}; i++) {
    float d = distance(vPlanet, uCities[i].xy);
    float r = uCities[i].z;
    color = mix(color, vec3(0.62, 0.58, 0.52), (1.0 - smoothstep(r - 20.0, r + 20.0, d)) * 0.55);
    color = mix(color, vec3(0.45, 0.40, 0.34), ring(d, r, 1.5, footprint) * 0.6);
  }

  for (int i = 0; i < ${POIS.length}; i++) {
    float pd = distance(vPlanet, uPois[i].xy);
    float pr = uPois[i].z;
    color = mix(color, vec3(0.60, 0.46, 0.36), (1.0 - smoothstep(pr - 15.0, pr + 15.0, pd)) * 0.45);
  }

  if (uGrid > 0.5) {
    float minor = gridLine(vPlanet, 256.0, 1.0);
    float major = gridLine(vPlanet, 1024.0, 1.6);
    float fade = 1.0 - smoothstep(4.0, 40.0, footprint);
    color = mix(color, vec3(0.35, 0.30, 0.24), minor * 0.25 * fade);
    color = mix(color, vec3(0.25, 0.20, 0.16), major * 0.45);
  }

  vec2 heatUv = (vPlanet - uHeatRect.xy) / uHeatRect.zw;
  if (uHeatAlpha > 0.0 && all(greaterThanEqual(heatUv, vec2(0.0))) && all(lessThanEqual(heatUv, vec2(1.0)))) {
    vec4 heat = texture2D(uHeat, heatUv);
    float value = clamp(dot(heat, uHeatMask), 0.0, 1.0);
    // The +1 keeps the edges apart before the first region arrives (radius 0): smoothstep(e, e, x) is undefined.
    float beyond = smoothstep(uHeatNear * 0.85, uHeatNear * 1.05 + 1.0, distance(vPlanet, uViewCenter));
    float a = smoothstep(0.02, 0.25, value) * uHeatAlpha * beyond;
    color = mix(color, heatRamp(value), a * 0.75);
  }

  // The DEBUG block, drawn last so it reads over everything else (CLI3D-03). All of it is the SERVER's account of this
  // session: the grid it keys cells on, the cells it has actually delivered, and the shape it is serving after clamping.
  if (uReplGrid.w > 0.5) {
    float cells = gridLine(vPlanet - uReplGrid.xy, uReplGrid.z, 1.0);
    color = mix(color, vec3(0.16, 0.34, 0.52), cells * 0.55 * (1.0 - smoothstep(2.0, 24.0, footprint)));
  }

  // Delivered cells are tinted FAINTLY and the gaps loudly, which is the way round that carries information: a settled
  // view is almost all delivered, so shading what IS held paints the whole screen and says nothing, while the handful of
  // cells still filling is the thing worth seeing. Drawn at full strength the first version was a green wash over the
  // world.
  if (uCellAlpha > 0.0) {
    vec2 cellUv = (vPlanet - uCellRect.xy) / uCellRect.zw;
    if (all(greaterThanEqual(cellUv, vec2(0.0))) && all(lessThan(cellUv, vec2(1.0)))) {
      float held = texture2D(uCells, cellUv).a;
      color = mix(color, vec3(0.30, 0.85, 0.55), held * uCellAlpha * 0.12);
      color = mix(color, vec3(0.95, 0.35, 0.20), (1.0 - held) * uCellAlpha * 0.38);
    }
  }

  // The hull is closed by carrying the previous vertex rather than indexing uHull[i + 1]: GLSL ES 1.0 only promises
  // uniform-array indexing by a constant-index-expression, and the loop counter is the only one here that is certainly one.
  if (uHullCount > 0.5) {
    vec2 first = uHull[0].xy;
    vec2 prev = first;
    float hullEdge = 1e9;
    for (int i = 0; i < ${HULL_SLOTS}; i++) {
      if (float(i) >= uHullCount) { break; }
      vec2 v = uHull[i].xy;
      if (i > 0) { hullEdge = min(hullEdge, segment(vPlanet, prev, v)); }
      prev = v;
    }

    hullEdge = min(hullEdge, segment(vPlanet, prev, first));
    color = mix(color, vec3(0.98, 0.82, 0.25), (1.0 - smoothstep(0.0, footprint * 2.0, hullEdge)) * 0.95);
  }

  if (uSessionDisc.w > 0.5) {
    color = mix(color, vec3(0.98, 0.82, 0.25), ring(distance(vPlanet, uSessionDisc.xy), uSessionDisc.z, 2.0, footprint) * 0.95);
  }

  if (uSelection.z > 0.5) {
    float d = distance(vPlanet, uSelection.xy);
    color = mix(color, vec3(0.95, 0.30, 0.20), ring(d, 24.0, 1.5, footprint) * 0.9);
    color = mix(color, vec3(0.95, 0.75, 0.20), ring(d, 75.0, 1.5, footprint) * 0.9);
    color = mix(color, vec3(0.30, 0.65, 0.95), ring(d, 192.0, 1.5, footprint) * 0.9);
  }

  if (edge > ${PLANET}) {
    color *= 0.35;
  }

  // Lighting last, over EVERY overlay rather than only the sand. An overlay is paint on the ground, so it takes the
  // ground's light; shading the sand alone would leave a city disc looking like a decal hovering over the slope it is on.
  // A fixed sun, because nothing in this demo has a time of day.
  // A higher sun and more ambient: a desert at midday has bright sand and soft shadows, and the previous low sun with a
  // 0.45 floor put near-black on any face turned away from it.
  float lambert = clamp(dot(ground, normalize(vec3(0.42, 0.84, 0.34))), 0.0, 1.0);
  color *= 0.66 + 0.5 * lambert;

  float dist = distance(vRender, uCamera);
  // Haze density and its ceiling both come from the CPU, because they depend on the camera ALTITUDE: see fogFor in
  // ground.ts. A fixed density cannot serve both views — the value that leaves a 22 km map view readable leaves the
  // horizon at eye level as a dark band of ground, which is what the planet-edge darkening looks like from down there.
  float fog = 1.0 - exp(-dist * uFog.x);
  gl_FragColor = vec4(mix(color, uSkyColor, clamp(fog, 0.0, uFog.y)), 1.0);
}
`;
