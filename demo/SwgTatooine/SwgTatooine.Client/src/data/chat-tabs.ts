/**
 * The chat's categories and tabs (client.md C44, mail.md § 6): every line has a category, each category keeps its own history — so the speech of a busy town
 * cannot push a drop out — and a tab is a name and the categories it shows.
 */

/** What a chat line is about. */
export type ChatCategory = 'local' | 'tells' | 'loot' | 'combat' | 'economy' | 'structures' | 'system';

/** Every category, in the order a tab's editor lists them. */
export const CHAT_CATEGORIES: readonly ChatCategory[] = [
  'local',
  'tells',
  'loot',
  'combat',
  'economy',
  'structures',
  'system',
];

/** What a category is called in the tab editor. */
export const CATEGORY_LABELS: Readonly<Record<ChatCategory, string>> = {
  local: 'Speech around you',
  tells: 'Tells',
  loot: 'Loot and items',
  combat: 'Combat and levels',
  economy: 'Credits and the bazaar',
  structures: 'Structures',
  system: 'System and commands',
};

/** The lines each category keeps; the oldest go first. */
export const CATEGORY_HISTORY = 200;

/** A tab: a name and the categories it shows. */
export interface ChatTab {
  readonly id: string;
  readonly name: string;
  readonly categories: readonly ChatCategory[];
}

/** The tabs of a first visit: everything, and everything but the speech around you. */
export const DEFAULT_TABS: readonly ChatTab[] = [
  { id: 'general', name: 'General', categories: CHAT_CATEGORIES },
  { id: 'events', name: 'Events', categories: CHAT_CATEGORIES.filter((c) => c !== 'local') },
];

/** The most tabs a chat keeps, and a tab name's longest. */
export const MAX_TABS = 6;
export const TAB_NAME_MAX = 16;

/** A line with what the merge needs: its order across categories. */
export interface Sequenced {
  readonly seq: number;
}

/**
 * The lines a tab shows: its categories' histories merged in arrival order, newest last, at most `limit` (each history is capped already; the merge of
 * several is capped again so a tab of every category shows the same span a single one does).
 */
export function tabLines<T extends Sequenced>(
  histories: Readonly<Partial<Record<ChatCategory, readonly T[]>>>,
  tab: ChatTab,
  limit = CATEGORY_HISTORY,
): T[] {
  const merged: T[] = [];
  for (const category of tab.categories) {
    merged.push(...(histories[category] ?? []));
  }

  merged.sort((a, b) => a.seq - b.seq);
  return merged.length > limit ? merged.slice(merged.length - limit) : merged;
}

/** How many lines a tab has that arrived after `seen` (a sequence number). */
export function unreadIn<T extends Sequenced>(
  histories: Readonly<Partial<Record<ChatCategory, readonly T[]>>>,
  tab: ChatTab,
  seen: number,
): number {
  let n = 0;
  for (const category of tab.categories) {
    for (const line of histories[category] ?? []) {
      n += line.seq > seen ? 1 : 0;
    }
  }

  return n;
}

/** Tabs read back from a saved layout: well-formed ones only, at most {@link MAX_TABS}; none valid gives the defaults. */
export function parseTabs(value: unknown): readonly ChatTab[] {
  if (!Array.isArray(value)) {
    return DEFAULT_TABS;
  }

  const tabs: ChatTab[] = [];
  for (const raw of value) {
    const t = raw as Record<string, unknown> | null;
    if (
      t === null ||
      typeof t !== 'object' ||
      typeof t.id !== 'string' ||
      typeof t.name !== 'string' ||
      !Array.isArray(t.categories)
    ) {
      continue;
    }

    const categories = (t.categories as unknown[]).filter((c): c is ChatCategory =>
      CHAT_CATEGORIES.includes(c as ChatCategory),
    );
    const name = t.name.trim().slice(0, TAB_NAME_MAX);
    if (name.length > 0 && !tabs.some((x) => x.id === t.id)) {
      tabs.push({ id: t.id, name, categories });
    }
  }

  return tabs.length === 0 ? DEFAULT_TABS : tabs.slice(0, MAX_TABS);
}

/** A new tab's id: one no tab has. */
export function newTabId(tabs: readonly ChatTab[]): string {
  let n = tabs.length + 1;
  while (tabs.some((t) => t.id === `tab${String(n)}`)) {
    n++;
  }

  return `tab${String(n)}`;
}
