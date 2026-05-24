import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

// Suite H gate (DS-1): font sizes must use the density-responsive `text-fs-*` scale — never a raw `text-[Npx]`
// literal or the retired `text-density-*` tokens, which don't react to a density switch. This catches a
// regression the moment someone hardcodes a pixel size in a className. Canvas text (ctx.font) is out of scope.
function walkTsx(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) {
      if (entry !== '__tests__' && entry !== 'generated') {
        walkTsx(p, out);
      }
    } else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
      out.push(p);
    }
  }
  return out;
}

describe('suite H gate — density-responsive font sizes', () => {
  it('no raw text-[Npx] or text-density-* in source (use the text-fs-* scale)', () => {
    const files = walkTsx(join(process.cwd(), 'src'));
    const rawPx = /text-\[\d+px\]/;
    const oldToken = /text-density-(?:sm|base)\b/;
    const offenders = files
      .filter((f) => {
        const txt = readFileSync(f, 'utf8');
        return rawPx.test(txt) || oldToken.test(txt);
      })
      .map((f) => f.slice(f.indexOf('src')).replace(/\\/g, '/'));
    expect(offenders, `raw font-size literals found: ${offenders.join(', ')}`).toEqual([]);
  });
});
