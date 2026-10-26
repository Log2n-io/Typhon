/**
 * How fast the MOCK world's players move.
 *
 * <b>These belong to the mock and nowhere else.</b> The mock is a client-side simulation with no server: it owns the entities
 * it draws, so it is the authority on their speed and a constant here is the right shape. A LIVE session is the opposite — the
 * server owns movement, sends the client a speed CLASS, and replicates the metres per second as per-player stats
 * (`walkMps` / `runMps`, A14). A live path that borrowed one of these numbers would be mirroring a figure the server can never
 * correct, which is the `TICK_PERIOD_MS = 100` bug with a different constant, and `test/no-speed-literals.test.ts` forbids it.
 *
 * They were declared in `world-data.ts` until A14. Nothing outside `mock/` ever read them, and sharing a module with the live
 * client put them one import away from being reused.
 */

export const PLAYER_RUN_SPEED_MPS = 5;
export const PLAYER_MOUNT_SPEED_MPS = 12;
