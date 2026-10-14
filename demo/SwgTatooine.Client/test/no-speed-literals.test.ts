// Node types for THIS FILE only — the app's tsconfig sets `types: ["vite/client"]` deliberately, so `src` cannot reach a node
// API by accident. The same exemption `styles.test.ts` takes, for the same reason: this has to read the source off disk.
/// <reference types="node" />
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

/**
 * No movement SPEED may be written into client source (A14).
 *
 * <b>This is a test about a failure shape, not about tidiness.</b> The client sends a speed CLASS and the server maps it onto
 * metres per second, so prediction needs the magnitude — and a magnitude mirrored here is a number the server can never
 * correct. The consequences of the two kinds of mismatch are not comparable: a wrong class VALUE is refused on the wire and
 * counted, loudly and immediately, while a wrong SPEED produces position drift that grows for as long as the player keeps
 * walking and shows up as "the server keeps yanking me back".
 *
 * The client already shipped exactly this bug once with a different number — `TICK_PERIOD_MS = 100`, a tick period the server
 * only ever announced on CHANGE, so a server merely running at another rate never corrected it. It cost four separate
 * reported symptoms. `walkMps` and `runMps` are replicated per player for that reason, and this test is here so the shortcut
 * cannot come back quietly.
 */

const SRC = fileURLToPath(new URL('../src', import.meta.url));

/**
 * Every `.ts`/`.tsx` file under `src`, except the mock world's own.
 *
 * <b>`data/mock/` is exempt on principle, not for convenience.</b> The mock is a client-side simulation with no server at all:
 * it owns the entities it draws, so it is the authority on how fast they move and a speed constant there is the right shape.
 * What must not happen is a LIVE path borrowing one, and the second test below is what forbids that.
 */
function sources(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'mock') {
      continue;
    }

    const path = join(dir, entry);
    if (statSync(path).isDirectory()) {
      out.push(...sources(path));
    } else if (entry.endsWith('.ts') || entry.endsWith('.tsx')) {
      out.push(path);
    }
  }

  return out;
}

/** The mock's movement speeds, which no live path may read. */
const MOCK_SPEEDS = ['PLAYER_RUN_SPEED_MPS', 'PLAYER_MOUNT_SPEED_MPS'];

/** The names a speed constant would plausibly be given, as an identifier being DECLARED. */
const SPEED_DECLARATION =
  /\b(?:const|let|var|readonly)\s+([A-Za-z_$][\w$]*(?:SPEED|Speed|speed|MPS|Mps|mps)[\w$]*)\s*(?::[^=]+)?=\s*[-\d.]/;

describe('client source', () => {
  it('declares no movement speed of its own', () => {
    const offenders: string[] = [];
    for (const path of sources(SRC)) {
      const text = readFileSync(path, 'utf8');
      const lines = text.split('\n');
      for (let i = 0; i < lines.length; i++) {
        const match = SPEED_DECLARATION.exec(lines[i]);
        if (match !== null) {
          offenders.push(`${path.slice(SRC.length + 1)}:${i + 1} declares ${match[1]}`);
        }
      }
    }

    expect(
      offenders,
      'a movement speed belongs to the server and reaches the client as a replicated stat (walkMps / runMps), never as a literal here',
    ).toEqual([]);
  });

  /**
   * The mock's speeds stay in the mock. This is the import that would reintroduce the bug while satisfying the test above —
   * `world-data.ts` is shared between the mock and the live client, so its constants are one import away from a live path.
   */
  it('keeps the mock world speeds out of every live path', () => {
    const offenders: string[] = [];
    for (const path of sources(SRC)) {
      const text = readFileSync(path, 'utf8');
      for (const name of MOCK_SPEEDS) {
        if (text.includes(name)) {
          offenders.push(`${path.slice(SRC.length + 1)} reads ${name}`);
        }
      }
    }

    expect(offenders, 'a live path takes its speeds from the server, never from the mock world constants').toEqual([]);
  });

  /**
   * The positive half: the stats are actually read. Without this the test above could be satisfied by a client that predicts
   * nothing at all, which is the one way to have no speed literal and no prediction either.
   */
  it('reads the replicated speed stats', () => {
    const predictor = readFileSync(join(SRC, 'app', 'client-app.ts'), 'utf8');
    // Every gait Shift+1..4 can select, because a gait whose figure the client never reads is a gait it cannot predict —
    // and the fallback it lands in is silent to the viewer, who just sees the body stutter at that one speed.
    for (const stat of ['walkMps', 'runMps', 'burstMps', 'speederMps', 'speederFastMps']) {
      expect(predictor, `the client predicts a gait without reading what the server grants for it`).toContain(
        `selfNumber('${stat}')`,
      );
    }
  });
});
