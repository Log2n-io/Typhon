import { RealmFrame } from '../src/index.js';

/** catalog-kitchen-sink's realm, the same numbers as the C# suites': deep, ±8192 m by ±64 m at 24 bits, 256 m cells. */
export const KITCHEN = new RealmFrame(3, 7, 1, 0xc0ffee, 24, 256, true, [-8192, -8192, -64], [8192, 8192, 64]);

/** catalog-swg's realm: flat, ±8192 m at 24 bits, 256 m cells. */
export const SWG = new RealmFrame(0, 0, 0, 0, 24, 256, false, [-8192, -8192, 0], [8192, 8192, 256]);
