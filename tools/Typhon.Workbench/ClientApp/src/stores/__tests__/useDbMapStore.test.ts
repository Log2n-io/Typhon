import { beforeEach, describe, expect, it } from 'vitest';
import { useDbMapStore } from '../useDbMapStore';
import { newBookmarkId, type DbMapBookmark } from '@/libs/dbmap/dbMapBookmarks';

// Covers the persisted bookmarks slice of the Database File Map store (Module 15, A4 — §13 A4 AC3): per-database
// scoping, newest-first ordering, rename / delete by id.

function bm(label: string): DbMapBookmark {
  return { id: newBookmarkId(), label, camera: { scale: 2, x: 10, y: 20 }, createdAt: Date.now() };
}

describe('useDbMapStore — bookmarks', () => {
  beforeEach(() => useDbMapStore.setState({ bookmarks: {} }));

  it('adds bookmarks newest-first, scoped per database', () => {
    const s = useDbMapStore.getState();
    const a = bm('A');
    const b = bm('B');
    s.addBookmark('db1', a);
    s.addBookmark('db1', b);
    s.addBookmark('db2', bm('C'));
    const st = useDbMapStore.getState();
    expect(st.bookmarks.db1.map((x) => x.label)).toEqual(['B', 'A']);
    expect(st.bookmarks.db2.map((x) => x.label)).toEqual(['C']);
  });

  it('renames and removes a bookmark by id without touching other databases', () => {
    const s = useDbMapStore.getState();
    const a = bm('A');
    const b = bm('B');
    s.addBookmark('db', a);
    s.addBookmark('db', b);
    s.addBookmark('other', bm('keep'));
    s.renameBookmark('db', a.id, 'A renamed');
    s.removeBookmark('db', b.id);
    const st = useDbMapStore.getState();
    expect(st.bookmarks.db).toHaveLength(1);
    expect(st.bookmarks.db[0].label).toBe('A renamed');
    expect(st.bookmarks.other).toHaveLength(1);
  });

  it('newBookmarkId yields unique ids', () => {
    expect(newBookmarkId()).not.toBe(newBookmarkId());
  });
});
