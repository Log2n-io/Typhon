// Node types for THIS FILE only. The app's tsconfig sets `types: ["vite/client"]` deliberately, so that `src` cannot
// reach for a node API by accident; a reference here lifts that for the one file that has to read a file off disk.
// `?raw` was the obvious alternative and does not work: vitest runs in the node environment, where Vite stubs CSS
// imports to an empty string, so the test passed against nothing at all until it was probed.
/// <reference types="node" />
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const css = readFileSync(fileURLToPath(new URL('../src/ui/styles.css', import.meta.url)), 'utf8');

/**
 * The stylesheet's custom properties, checked because getting one wrong is SILENT.
 *
 * An unknown `var(--nope)` makes the whole declaration invalid at computed-value time, so the property simply does not
 * apply — no console warning, no build error, no lint complaint. The login card shipped with `background: var(--panel)`
 * when the token is `--panel-bg`, so it had no background at all and the page's sand showed through it; the labels and
 * the button text, both designed against a dark panel, became unreadable. Nothing in the suite could see that, because
 * nothing in the suite looks at CSS.
 */

/** The custom properties the sheet defines, anywhere. */
function defined(sheet: string): Set<string> {
  const names = new Set<string>();
  for (const match of sheet.matchAll(/^\s*(--[a-z0-9-]+)\s*:/gim)) {
    names.add(match[1]);
  }

  return names;
}

/** Every custom property the sheet reads, with the line it is read on. */
function used(sheet: string): { name: string; line: number }[] {
  const out: { name: string; line: number }[] = [];
  const lines = sheet.split('\n');
  for (let i = 0; i < lines.length; i++) {
    for (const match of lines[i].matchAll(/var\(\s*(--[a-z0-9-]+)/gi)) {
      out.push({ name: match[1], line: i + 1 });
    }
  }

  return out;
}

describe('styles.css custom properties', () => {
  it('defines every property it reads', () => {
    const have = defined(css);
    const missing = used(css)
      .filter((u) => !have.has(u.name))
      .map((u) => `${u.name} (line ${u.line})`);

    expect(missing, 'a var() naming a property nothing defines silently drops its whole declaration').toEqual([]);
  });

  /**
   * Not a style opinion: these are the four the rest of the sheet is built on, and a rename that missed a call site is
   * exactly the failure above. Naming them here means the rename has to come past this test.
   */
  it('keeps the tokens the sheet is built on', () => {
    const have = defined(css);
    for (const token of ['--panel-bg', '--panel-solid', '--panel-fg', '--muted', '--accent', '--border']) {
      expect(have.has(token), `${token} is gone`).toBe(true);
    }
  });

  /**
   * The login screen is the one surface with no world behind it — `App` does not mount the viewport without a grant —
   * so it must not use the translucent panel, which composites against the page's sand background into a mid-tone that
   * neither the muted label grey nor the button text can be read against.
   */
  it('gives the login card an opaque background', () => {
    const card = /\.login-card\s*\{([^}]*)\}/.exec(css);
    expect(card, '.login-card rule not found').not.toBeNull();
    expect(card![1]).toContain('background: var(--panel-solid)');
  });
});
