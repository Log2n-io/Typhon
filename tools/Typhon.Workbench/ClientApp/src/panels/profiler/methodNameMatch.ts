/**
 * Method-declaration filter matching for the Call Tree panel (#351).
 *
 * A profiler frame is a full method declaration —
 * `AntHill.AntUpdateSystem.Execute(value class Typhon.Engine.TickContext)`. It is split into
 * identifier **words** (maximal `[A-Za-z0-9]` runs, so every `.` / `(` / `<` / `` ` `` / `,` / space
 * is a boundary); a query matches only if it matches **within a single word** — never stitched
 * across words. That is the whole point: a cross-word subsequence would let `AUS` "match"
 * `ReadAsync(…unsigned…System)` via the `A` of Async, the `u` of unsigned and the `S` of System,
 * which is noise.
 *
 * This is deliberately **not** the command palette's matcher (`camelHumpFilter`): a palette label
 * is a phrase whose word initials are matched *across* the label; a method declaration is a
 * structured identifier where only a within-word match is meaningful.
 *
 * Per word, two modes (smart-case):
 *  - **CamelCase hump** — a **case-sensitive** subsequence of the word's hump letters
 *    (`AUS` → `A`nt`U`pdate`S`ystem). Typing the capitals is the query; `aus` will not hump-match.
 *  - **Substring** — a **case-insensitive** fallback (`spatial` → Write`Spatial`).
 */

interface MethodWord {
  /** The identifier text of the word. */
  text: string;
  /** Offset of the word's first character in the original declaration. */
  offset: number;
}

/** A word is a maximal run of identifier characters; every other character is a boundary. */
const WORD_PATTERN = /[A-Za-z0-9]+/g;

/** Splits a method declaration into its identifier words, each with its offset in the original string. */
function splitWords(decl: string): MethodWord[] {
  const words: MethodWord[] = [];
  for (const match of decl.matchAll(WORD_PATTERN)) {
    words.push({ text: match[0], offset: match.index ?? 0 });
  }
  return words;
}

const isUpper = (c: string): boolean => c >= 'A' && c <= 'Z';
const isLower = (c: string): boolean => c >= 'a' && c <= 'z';

/**
 * The hump positions within a single word: index 0, and every uppercase letter that begins a
 * CamelCase part — an uppercase after a non-uppercase, or the trailing uppercase of an acronym run
 * followed by a lowercase (the `P` in `HTMLParser`).
 */
function humpPositions(word: string): number[] {
  const humps: number[] = [];
  for (let i = 0; i < word.length; i++) {
    const c = word[i];
    const prev = i > 0 ? word[i - 1] : '';
    const next = i + 1 < word.length ? word[i + 1] : '';
    const camelBoundary = isUpper(c) && !isUpper(prev);
    const acronymTail = isUpper(c) && isUpper(prev) && isLower(next);
    if (i === 0 || camelBoundary || acronymTail) {
      humps.push(i);
    }
  }
  return humps;
}

/** Case-sensitive subsequence of `query` over `word`'s hump letters → matched positions in the word, or null. */
function matchHump(word: string, query: string): number[] | null {
  const humps = humpPositions(word);
  const matched: number[] = [];
  let qi = 0;
  for (let i = 0; i < humps.length && qi < query.length; i++) {
    if (word[humps[i]] === query[qi]) {
      matched.push(humps[i]);
      qi++;
    }
  }
  return qi === query.length ? matched : null;
}

/** Case-insensitive substring of `query` within `word` → the matched run of positions, or null. */
function matchSubstring(word: string, query: string): number[] | null {
  const at = word.toLowerCase().indexOf(query.toLowerCase());
  return at === -1 ? null : Array.from({ length: query.length }, (_, k) => at + k);
}

/**
 * Matches `query` against a method declaration, confined to a single identifier word. Returns the
 * character positions (offsets into `decl`) to highlight — the first matching word wins — or null
 * when no word matches. An empty query returns `[]` (matches everything, highlights nothing).
 */
export function matchMethodName(decl: string, query: string): number[] | null {
  if (query === '') {
    return [];
  }
  for (const word of splitWords(decl)) {
    const local = matchHump(word.text, query) ?? matchSubstring(word.text, query);
    if (local) {
      return local.map((p) => p + word.offset);
    }
  }
  return null;
}
