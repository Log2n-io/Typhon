// Bookmarks for the Database File Map (Module 15, §4.5 / §13 A4 AC3).
//
// A bookmark pins a viewport (the 2D camera) under a label. Bookmarks persist across Workbench sessions,
// scoped per database — the persistence lives in `useDbMapStore` (a `persist`-partialized slice keyed by
// database name). This module owns the value type and the id factory.

import type { Camera } from './camera';

/** A saved viewport on the file map — a labelled camera, persisted per database, surviving sessions. */
export interface DbMapBookmark {
  /** Stable id — the React key and the delete / rename handle. */
  id: string;
  /** User-facing label; defaults to `View N`, editable in the Bookmarks tab. */
  label: string;
  /** The pinned camera — fly-to restores exactly this framing. */
  camera: Camera;
  /** Creation timestamp (epoch ms) — the list is shown newest-first. */
  createdAt: number;
}

let counter = 0;

/** A process-unique bookmark id — timestamp + counter, no crypto dependency, stable as a React key. */
export function newBookmarkId(): string {
  counter += 1;
  return `bm-${Date.now().toString(36)}-${counter}`;
}
