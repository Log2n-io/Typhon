import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from '@/stores/safeStorage';

const MAX_ENTRIES = 12;

/** An engine endpoint the Workbench has attached to, remembered so it does not have to be retyped. */
export interface AttachEndpoint {
  /** `host` or `host:port`, exactly as it was attached — the string the server is given. */
  endpoint: string;
  lastAttachedAt: string;
  /** What the user called it, if they named it. Free text; a hostname is rarely the useful label for a box. */
  label?: string;
}

/**
 * Whether an endpoint's host is the loopback interface, and therefore whether the stream stays on this machine.
 *
 * <b>The question is not cosmetic, and getting it wrong in either direction misleads.</b> `TcpExporter` binds
 * `IPAddress.Loopback`, has no authentication and no transport security, and its stream is the engine's internals. A
 * loopback endpoint is a process on this machine — or, as often, the near end of an SSM port forward to a box in
 * eu-west-1 (`scripts/attach-remote.ps1`), which is the shape a deployed attach actually takes and the reason the
 * default is not treated as suspicious. Anything else is a socket to another host, in clear, with nobody checking who
 * is on the other end.
 *
 * IPv6 loopback is matched in both the bracketed form a `host:port` needs and the bare form, since the field accepts a
 * host on its own. `localhost` is matched by name and the whole `127.0.0.0/8` block by prefix — `127.0.0.1` is the one
 * anybody types, but the entire /8 is loopback and a check that only knew the one address would badge `127.0.0.2` as
 * remote.
 */
export function isLoopbackEndpoint(endpoint: string): boolean {
  const host = hostOf(endpoint);
  if (host === '') {
    return false;
  }

  const lower = host.toLowerCase();
  if (lower === 'localhost' || lower.endsWith('.localhost')) {
    return true;
  }

  if (lower === '::1' || lower === '[::1]' || lower === '0:0:0:0:0:0:0:1') {
    return true;
  }

  // The whole 127.0.0.0/8, not just 127.0.0.1.
  return /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(lower);
}

/**
 * The host part of `host` or `host:port`, with an IPv6 address's brackets kept.
 *
 * <b>Splitting on the first colon would break every IPv6 endpoint</b> — `[::1]:9100` would yield `[`. The bracketed
 * form is therefore read as a unit, and only a bare `host:port` is split; a bare IPv6 address with no port has more
 * than one colon and no brackets, which is how it is told apart from a host with a port.
 */
export function hostOf(endpoint: string): string {
  const trimmed = endpoint.trim();
  if (trimmed === '') {
    return '';
  }

  if (trimmed.startsWith('[')) {
    const close = trimmed.indexOf(']');
    return close < 0 ? trimmed : trimmed.slice(0, close + 1);
  }

  const colons = trimmed.split(':').length - 1;
  if (colons > 1) {
    // A bare IPv6 address: no brackets, several colons, so none of them is a port separator.
    return trimmed;
  }

  return colons === 1 ? trimmed.slice(0, trimmed.indexOf(':')) : trimmed;
}

interface AttachEndpointsStore {
  entries: AttachEndpoint[];
  /** Records an attach, moving the endpoint to the front. Keeps a label the entry already had when none is given. */
  record: (endpoint: string, label?: string) => void;
  remove: (endpoint: string) => void;
  rename: (endpoint: string, label: string) => void;
  clear: () => void;
}

/** Endpoints are compared case-insensitively on the whole string: a host is case-insensitive and a port has no case. */
function key(endpoint: string): string {
  return endpoint.trim().toLowerCase();
}

/**
 * The endpoints the Workbench has attached to, most recent first, persisted across restarts (W3).
 *
 * <b>Separate from `useRecentFilesStore` on purpose.</b> That store's entries are files: they carry a schema DLL list,
 * pinned resources and a saved viewport, all keyed by a path, and its `dirOf`/`getRecentLocations` helpers are about a
 * filesystem. An endpoint has none of that and is not a path — folding it in would have meant a `kind` discriminator
 * on every one of those fields and a path normaliser that had to leave `host:port` alone.
 */
export const useAttachEndpointsStore = create<AttachEndpointsStore>()(
  persist(
    (set) => ({
      entries: [],
      record: (endpoint, label) =>
        set((s) => {
          const trimmed = endpoint.trim();
          if (trimmed === '') {
            return s;
          }
          const k = key(trimmed);
          const existing = s.entries.find((e) => key(e.endpoint) === k);
          const merged: AttachEndpoint = {
            endpoint: trimmed,
            lastAttachedAt: new Date().toISOString(),
            // Re-attaching carries no label — keep the one the entry had, or it would be cleared by every attach.
            label: label ?? existing?.label,
          };
          return { entries: [merged, ...s.entries.filter((e) => key(e.endpoint) !== k)].slice(0, MAX_ENTRIES) };
        }),
      remove: (endpoint) => set((s) => ({ entries: s.entries.filter((e) => key(e.endpoint) !== key(endpoint)) })),
      rename: (endpoint, label) =>
        set((s) => {
          const k = key(endpoint);
          const trimmed = label.trim();
          return {
            entries: s.entries.map((e) =>
              key(e.endpoint) === k ? { ...e, label: trimmed === '' ? undefined : trimmed } : e,
            ),
          };
        }),
      clear: () => set({ entries: [] }),
    }),
    {
      name: 'workbench-attach-endpoints',
      storage: safeStorage,
    },
  ),
);
