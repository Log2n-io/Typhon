import { describe, expect, it } from 'vitest';
import { cityNear, nearestFacilities, navLabel, relativeBearing, type NavTarget } from '../src/app/nav-finder';
import { CITIES } from '../src/data/world-data';
import { StructureKind } from '../src/data/swg-schema';
import { formatDistance } from '../src/ui/NavPane';

describe('nearestFacilities', () => {
  it('finds the nearest of each kind among the structures held, and ignores every other kind', () => {
    const held = [
      { kind: StructureKind.Bazaar, x: 100, z: 0 },
      { kind: StructureKind.Bazaar, x: 30, z: 40 },
      { kind: StructureKind.MissionTerminal, x: -10, z: 0 },
      { kind: StructureKind.Building, x: 1, z: 0 },
      { kind: StructureKind.Shuttleport, x: 0, z: 200 },
    ];
    const out: NavTarget[] = [];
    nearestFacilities(held, held.length, 0, 0, out);
    const byKind = new Map(out.map((t) => [t.kind, t]));
    expect(byKind.get(StructureKind.Bazaar)).toMatchObject({ x: 30, z: 40, distance: 50, held: true });
    expect(byKind.get(StructureKind.MissionTerminal)).toMatchObject({ x: -10, distance: 10, held: true });
    expect(byKind.get(StructureKind.Shuttleport)).toMatchObject({ z: 200, distance: 200, held: true });
  });

  it('reads only the live records, the pooled tail is last frame', () => {
    const held = [{ kind: StructureKind.Bazaar, x: 5, z: 0 }];
    const out: NavTarget[] = [];
    nearestFacilities(held, 0, 0, 0, out);
    expect(out.find((t) => t.kind === StructureKind.Bazaar)?.distance).toBe(Infinity);
  });

  it('finds a shuttleport out of range at the nearest city centre, where every city has one', () => {
    const out: NavTarget[] = [];
    const city = CITIES[4];
    nearestFacilities([], 0, city.x + 3000, city.z, out);
    const port = out.find((t) => t.kind === StructureKind.Shuttleport)!;
    expect(port.held).toBe(false);
    expect(Number.isFinite(port.distance)).toBe(true);
    expect(navLabel(port)).toMatch(/Shuttleport$/);
  });
});

describe('relativeBearing', () => {
  // The eye camera looks along (sin yaw, cos yaw), and on screen +X is to its right.
  it('is 0 straight ahead, a quarter turn right, and ±π behind', () => {
    expect(relativeBearing(0, 0, 0, 10, 0)).toBeCloseTo(0, 9);
    expect(relativeBearing(0, 0, 10, 0, 0)).toBeCloseTo(Math.PI / 2, 9);
    expect(relativeBearing(0, 0, -10, 0, 0)).toBeCloseTo(-Math.PI / 2, 9);
    expect(Math.abs(relativeBearing(0, 0, 0, -10, 0))).toBeCloseTo(Math.PI, 9);
  });

  it('turns with the camera: looking at the target puts it straight ahead whatever the yaw', () => {
    expect(relativeBearing(0, 0, 10, 0, Math.PI / 2)).toBeCloseTo(0, 9);
    expect(relativeBearing(5, 5, 5, 15, 2 * Math.PI)).toBeCloseTo(0, 9);
  });
});

describe('the pane text', () => {
  it('names a shuttleport by its city and shows metres, then kilometres', () => {
    expect(cityNear(CITIES[0].x + 10, CITIES[0].z)).toBe(CITIES[0].name);
    expect(formatDistance(67.4)).toBe('67m');
    expect(formatDistance(1530)).toBe('1.5km');
  });
});
