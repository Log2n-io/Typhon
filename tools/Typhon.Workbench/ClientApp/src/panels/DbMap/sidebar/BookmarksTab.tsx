// The side-rail Bookmarks tab (Module 15, §6.4). Bookmark persistence is an A4 deliverable (§13 A4 AC3) — the
// tab exists now so the side-rail shape is stable, showing an empty-state placeholder until A4 lands.
export function BookmarksTab() {
  return (
    <div className="flex flex-col gap-1 p-3 text-[11px] text-muted-foreground">
      <p className="font-medium text-foreground">No bookmarks</p>
      <p>Pinning viewports and selections arrives with the A4 integration phase.</p>
    </div>
  );
}
