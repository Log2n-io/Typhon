/**
 * CamelCase hump matching for the command palette.
 *
 * "TVRT" matches "Toggle View Resource Tree" because T/V/R/T are the first
 * letters of each word (the "humps"). Mixed-case input is normalised to uppercase
 * before comparing, so "tvrt" and "TvRt" both work. Falls back to a plain
 * case-insensitive substring match when hump matching fails.
 *
 * This is the command-palette matcher only — a palette label is a phrase, so word
 * initials are matched *across* the whole label. Method-declaration filtering is a
 * different policy (a within-word match) and has its own matcher — see
 * panels/profiler/methodNameMatch.ts.
 */

/** Positions of the first character of each whitespace-separated word in `label`. */
function wordStarts(label: string): number[] {
  const starts: number[] = [];
  let afterSpace = true;
  for (let i = 0; i < label.length; i++) {
    if (label[i] === ' ') {
      afterSpace = true;
    } else if (afterSpace) {
      starts.push(i);
      afterSpace = false;
    }
  }
  return starts;
}

/**
 * Attempt to match `query` as a subsequence of the word initials of `label`.
 * The comparison is case-insensitive ("tv" behaves like "TV").
 * Returns matched positions in `label` (first char of each matched word), or null on failure.
 */
function matchInitials(label: string, query: string): number[] | null {
  const starts = wordStarts(label);
  const q = query.toUpperCase();
  const matched: number[] = [];
  let qi = 0;
  for (let i = 0; i < starts.length && qi < q.length; i++) {
    if (label[starts[i]].toUpperCase() === q[qi]) {
      matched.push(starts[i]);
      qi++;
    }
  }
  return qi === q.length ? matched : null;
}

/**
 * Compute which character positions in `label` should be highlighted for `query`.
 *
 * Priority:
 *  1. Word-initial hump match  → returns the first letter of each matched word.
 *  2. Case-insensitive substring match → returns the consecutive matched run.
 *  3. No label match → returns null (the item may still be visible via keyword match).
 */
export function matchHighlight(label: string, query: string): number[] | null {
  if (!query) return [];

  const hump = matchInitials(label, query);
  if (hump) return hump;

  const idx = label.toLowerCase().indexOf(query.toLowerCase());
  if (idx !== -1) return Array.from({ length: query.length }, (_, k) => idx + k);

  return null;
}

/**
 * cmdk `filter` prop implementation.
 *
 * `keywords[0]` = the command label (used for hump + substring matching).
 * `keywords[1]` = extra keyword string (used for substring fallback only).
 *
 * Scores: hump match = 1.0, label substring = 0.8, keyword substring = 0.5, no match = 0.
 */
export function humpFilter(
  _itemValue: string,
  search: string,
  keywords?: string[],
): number {
  if (!search) return 1;
  const label = keywords?.[0] ?? '';
  const kw = keywords?.[1] ?? '';

  if (matchInitials(label, search)) return 1.0;
  if (label.toLowerCase().includes(search.toLowerCase())) return 0.8;
  if (kw.toLowerCase().includes(search.toLowerCase())) return 0.5;

  return 0;
}
