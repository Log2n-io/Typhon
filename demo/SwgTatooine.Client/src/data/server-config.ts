/**
 * What the demo server says it was started with (`/typhon/demo.json`).
 *
 * <b>Why the server has to tell us at all.</b> A `ClientRegion` larger than the god profile's ceiling is silently shrunk
 * about its centroid, and the ceiling is on no wire the client can read — so a viewer asking for a 4 km radius was served
 * 1.5 km and told it had 4 (Typhon #1075). The engine should eventually say so itself; until it does, the one process
 * that owns both the server and this page publishes its own configuration and the slider stops offering what cannot be
 * had.
 *
 * Absent — a mock session, a server too old to serve it, a page opened from a file — leaves the limit unknown, and the
 * UI says "asked" rather than claiming a number it cannot stand behind.
 */
export interface ServerConfig {
  /** The god camera's largest region edge in metres, as `--god-region` gave it; 0 for a whole-world camera. */
  readonly godRegionMaxEdgeM: number;
  /**
   * The largest view radius the server will actually honour, metres — half {@link godRegionMaxEdgeM}, because the client
   * sends the smallest square containing its disc. 0 when the camera has no client-driven region at all.
   */
  readonly maxViewRadiusM: number;
}

function isFinitePositive(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

/** Reads the server's configuration, or `null` when there is none to read. Never throws. */
export async function fetchServerConfig(): Promise<ServerConfig | null> {
  try {
    const response = await fetch('typhon/demo.json', { cache: 'no-store' });
    if (!response.ok) {
      return null;
    }

    const body: unknown = await response.json();
    if (typeof body !== 'object' || body === null) {
      return null;
    }

    const { godRegionMaxEdgeM, maxViewRadiusM } = body as Record<string, unknown>;
    if (!isFinitePositive(godRegionMaxEdgeM) || !isFinitePositive(maxViewRadiusM)) {
      return null;
    }

    return { godRegionMaxEdgeM, maxViewRadiusM };
  } catch {
    // A page served from somewhere else, or an older server. Not knowing is a state the UI handles.
    return null;
  }
}
