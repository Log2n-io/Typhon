// Search query resolution for the Database File Map (Module 15, A3, §4.5).
//
// Parses the toolbar search box into an ordered list of match pages the camera flies to (Enter cycles them).
// All resolution is synchronous, over the StructuralMap the client already holds. Supported query forms:
//   page:N · #N · bare integer  → file page N
//   segment:N · seg:N           → every page of segment N, in file order
//   chunk:S:C                   → chunk C of segment S (resolved to that segment's root page — best effort)
//   free text                   → component type name / segment kind, case-insensitive substring

import type { DbMapData } from './types';

/** One resolved search match — a representative file page plus a label for the result chip. */
export interface DbMapSearchMatch {
  pageIndex: number;
  label: string;
}

/** Pages owned by `segmentId`, in ascending file order. */
function segmentPages(data: DbMapData, segmentId: number): number[] {
  const pages: number[] = [];
  for (let p = 0; p < data.pageCount; p++) {
    if (data.ownerSegmentId[p] === segmentId) {
      pages.push(p);
    }
  }
  return pages;
}

/** Resolves a search query into ordered match pages. Returns an empty list when nothing matches. */
export function searchDbMap(query: string, data: DbMapData): DbMapSearchMatch[] {
  const q = query.trim();
  if (q.length === 0 || data.pageCount === 0) {
    return [];
  }
  const lower = q.toLowerCase();
  const colon = lower.indexOf(':');
  const prefix = colon >= 0 ? lower.slice(0, colon) : '';
  const rest = colon >= 0 ? q.slice(colon + 1).trim() : '';

  if (prefix === 'page') {
    return resolvePage(rest, data);
  }
  if (prefix === 'segment' || prefix === 'seg') {
    return resolveSegment(rest, data);
  }
  if (prefix === 'chunk') {
    return resolveChunk(rest, data);
  }
  // No prefix — `#N` or a bare integer is a page; anything else is a component-type / kind text search.
  if (q.startsWith('#') && /^\d+$/.test(q.slice(1))) {
    return resolvePage(q.slice(1), data);
  }
  if (/^\d+$/.test(q)) {
    return resolvePage(q, data);
  }
  return resolveText(lower, data);
}

function resolvePage(text: string, data: DbMapData): DbMapSearchMatch[] {
  const n = Number.parseInt(text, 10);
  if (!Number.isFinite(n) || n < 0 || n >= data.pageCount) {
    return [];
  }
  return [{ pageIndex: n, label: `page ${n}` }];
}

function resolveSegment(text: string, data: DbMapData): DbMapSearchMatch[] {
  const id = Number.parseInt(text, 10);
  if (!Number.isFinite(id)) {
    return [];
  }
  const pages = segmentPages(data, id);
  return pages.map((p, i) => ({ pageIndex: p, label: `segment ${id} · page ${p} (${i + 1}/${pages.length})` }));
}

function resolveChunk(text: string, data: DbMapData): DbMapSearchMatch[] {
  // `chunk:S:C` — resolving C to its exact file page needs the segment's chunk layout (not in the coarse
  // map), so the camera flies to segment S's root page; the user then drills into chunk C.
  const parts = text.split(':');
  if (parts.length < 2) {
    return [];
  }
  const segId = Number.parseInt(parts[0], 10);
  const chunkId = Number.parseInt(parts[1], 10);
  const seg = data.segments.find((s) => s.id === segId);
  if (!seg || !Number.isFinite(chunkId)) {
    return [];
  }
  return [{ pageIndex: seg.rootPageIndex, label: `segment ${segId} · chunk ${chunkId}` }];
}

function resolveText(lower: string, data: DbMapData): DbMapSearchMatch[] {
  const matches: DbMapSearchMatch[] = [];
  for (const seg of data.segments) {
    const typeName = seg.typeName ?? '';
    if (typeName.toLowerCase().includes(lower) || seg.kind.toLowerCase().includes(lower)) {
      const label = typeName.length > 0 ? `${typeName} (segment ${seg.id})` : `${seg.kind} #${seg.id}`;
      matches.push({ pageIndex: seg.rootPageIndex, label });
    }
  }
  return matches;
}
