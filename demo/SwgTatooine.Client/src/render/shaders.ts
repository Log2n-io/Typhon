import { CITIES, PLANET_HALF_EXTENT_M, POIS } from '../data/world-data';
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

  float c = cos(instData.z);
  float s = sin(instData.z);
  vec3 local = position * size;
  vec3 turned = vec3(local.x * c + local.z * s, local.y, -local.x * s + local.z * c);
  gl_Position = viewProjection * vec4(instData.x + turned.x, turned.y, instData.y + turned.z, 1.0);

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
  vec4 clip = viewProjection * vec4(instData.x, uLift, instData.y, 1.0);
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
uniform mat4 viewProjection;
uniform vec2 uViewport;
uniform float uWidth;
uniform float uHeight;
varying vec4 vColor;

const float MIN_W = 0.01;

void main(void) {
  // lineStyle.x: alpha. lineStyle.y: 0 for a line; for a marker, its half-diagonal in CSS pixels, the sign picking the diagonal.
  vec4 a = viewProjection * vec4(lineEnds.x, uHeight, lineEnds.y, 1.0);
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
    b = viewProjection * vec4(lineEnds.z, uHeight, lineEnds.w, 1.0);
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

export const GROUND_VERTEX = `
precision highp float;
attribute vec3 position;
uniform mat4 world;
uniform mat4 viewProjection;
varying vec2 vPlanet;
varying vec3 vRender;

void main(void) {
  vec4 w = world * vec4(position, 1.0);
  vPlanet = position.xz;
  vRender = w.xyz;
  gl_Position = viewProjection * w;
}
`;

const PLANET = f(PLANET_HALF_EXTENT_M);

export const GROUND_FRAGMENT = `
precision highp float;
uniform vec3 uCamera;
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
varying vec2 vPlanet;
varying vec3 vRender;

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
  vec2 w = fwidth(p) * widthPx;
  vec2 l = 1.0 - smoothstep(vec2(0.0), w, g);
  return max(l.x, l.y);
}

float ring(float d, float radius, float widthPx, float footprint) {
  return 1.0 - smoothstep(0.0, footprint * widthPx, abs(d - radius));
}

vec3 heatRamp(float t) {
  vec3 cold = vec3(0.10, 0.30, 0.85);
  vec3 mid = vec3(0.95, 0.85, 0.20);
  vec3 hot = vec3(0.95, 0.15, 0.10);
  return t < 0.5 ? mix(cold, mid, t * 2.0) : mix(mid, hot, (t - 0.5) * 2.0);
}

void main(void) {
  float edge = max(abs(vPlanet.x), abs(vPlanet.y));
  vec3 sand = vec3(0.78, 0.66, 0.47);
  float dunes = noise(vPlanet * 0.0015) * 0.6 + noise(vPlanet * 0.011) * 0.3 + noise(vPlanet * 0.09) * 0.1;
  vec3 color = sand * (0.82 + 0.28 * dunes);

  float footprint = length(fwidth(vPlanet));
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

  if (uSelection.z > 0.5) {
    float d = distance(vPlanet, uSelection.xy);
    color = mix(color, vec3(0.95, 0.30, 0.20), ring(d, 24.0, 1.5, footprint) * 0.9);
    color = mix(color, vec3(0.95, 0.75, 0.20), ring(d, 75.0, 1.5, footprint) * 0.9);
    color = mix(color, vec3(0.30, 0.65, 0.95), ring(d, 192.0, 1.5, footprint) * 0.9);
  }

  if (edge > ${PLANET}) {
    color *= 0.35;
  }

  float dist = distance(vRender, uCamera);
  float fog = 1.0 - exp(-dist * 0.00004);
  gl_FragColor = vec4(mix(color, uSkyColor, clamp(fog, 0.0, 0.85)), 1.0);
}
`;
