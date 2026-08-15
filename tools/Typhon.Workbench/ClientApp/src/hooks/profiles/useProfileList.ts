import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ProfileDto, ProfileListDto, SessionDto } from '@/api/generated/model';
import { customFetch } from '@/api/client';
import { useSessionCapability, useSessionStore } from '@/stores/useSessionStore';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/** A capture in the database's `profilings/` directory, plus the drift figure the row displays. */
export interface Profile {
  fileName: string;
  profileId: string | null;
  isActive: boolean;
  createdUtcTicks: number;
  durationTicks: number;
  timestampFrequency: number;
  tickCount: number;
  tsnMin: number;
  tsnMax: number;
  databaseId: string;
  databaseName: string;
  multipleEnginesObserved: boolean;
  sizeBytes: number;
  isPinned: boolean;
  isReadable: boolean;
  /** Whether this capture was recorded against the database the session has open (design D-1 — co-location is not provenance). */
  belongsToDatabase: boolean;
  /**
   * How many transactions the database has moved on since this capture closed, or `null` when the question does not
   * apply — an unclosed capture, or one belonging to a different database, whose transaction numbers are simply not
   * comparable to this database's.
   *
   * The number §4.6 of the design calls "frequently the answer someone debugging a regression is looking for" — it turns
   * "this profile is old" into "this profile is 845,331 transactions behind", which is actionable. Computed here rather
   * than server-side so the list endpoint stays a pure projection of what is on disk.
   */
  driftTransactions: number | null;
}

const num = (v: unknown): number => (typeof v === 'number' ? v : Number(v ?? 0));

function normalizeProfile(raw: ProfileDto, databaseTsn: number): Profile {
  const tsnMax = num(raw.tsnMax);
  const belongsToDatabase = raw.belongsToDatabase ?? false;
  return {
    fileName: raw.fileName ?? '',
    profileId: (raw.profileId as string | null | undefined) ?? null,
    isActive: raw.isActive ?? false,
    createdUtcTicks: num(raw.createdUtcTicks),
    durationTicks: num(raw.durationTicks),
    timestampFrequency: num(raw.timestampFrequency),
    tickCount: num(raw.tickCount),
    tsnMin: num(raw.tsnMin),
    tsnMax,
    databaseId: raw.databaseId ?? '',
    databaseName: raw.databaseName ?? '',
    multipleEnginesObserved: raw.multipleEnginesObserved ?? false,
    sizeBytes: num(raw.sizeBytes),
    isPinned: raw.isPinned ?? false,
    isReadable: raw.isReadable ?? false,
    belongsToDatabase,
    // No drift for a capture with no recorded window (no engine attached), and none for a foreign one: its TSNs come
    // from another database's sequence, so `databaseTsn - tsnMax` would be a confident number with no meaning.
    driftTransactions: tsnMax > 0 && belongsToDatabase ? Math.max(0, databaseTsn - tsnMax) : null,
  };
}

/**
 * The captures recorded against the database this session has open, and the attach/detach actions over them.
 *
 * Reads only the list endpoint, which serves trace headers — no sidecar cache is built to render the list (design D-5).
 */
export function useProfileList() {
  const sessionId = useSessionStore((s) => s.sessionId);
  // Profiles are a sub-resource OF a database, and the list endpoint 409s without one. Gating on the capability rather
  // than on merely having a session keeps a trace session from firing a request whose only possible answer is an error
  // — and matters more now the Context Bar consumes this hook, so it runs for every session rather than only when the
  // Profiles panel is open (#618 §4.6).
  const hasDatabase = useSessionCapability('database');
  const queryClient = useQueryClient();
  const queryKey = ['profiles', sessionId];

  const query = useQuery({
    queryKey,
    enabled: !!sessionId && hasDatabase,
    staleTime: 5_000,
    queryFn: () =>
      customFetch<Envelope<ProfileListDto>>(`/api/sessions/${sessionId}/profiles`, { method: 'GET' }),
  });

  const databaseTsn = num(query.data?.data?.databaseTsn);

  const profiles: Profile[] = useMemo(
    () => (query.data?.data?.profiles ?? []).map((p) => normalizeProfile(p, databaseTsn)),
    [query.data, databaseTsn],
  );

  // Both mutations re-read the session: attaching or detaching changes its capabilities server-side, and that is what
  // makes the profiler panels appear and disappear.
  //
  // It has to be an explicit GET + setSession, not a cache invalidation. The session is not backed by a TanStack query
  // — every entry point (ConnectDialog, useOpenDatabaseFile, the Dev Fixture panel, captureAndAnalyse) pushes the DTO
  // into the Zustand store imperatively — so there is no key to invalidate and a `invalidateQueries(['session'])` here
  // would silently do nothing.
  const refreshSession = async () => {
    const response = await customFetch<Envelope<SessionDto>>(`/api/sessions/${sessionId}`, { method: 'GET' });
    useSessionStore.getState().setSession(response.data);
  };

  const invalidate = async () => {
    await queryClient.invalidateQueries({ queryKey });

    // Everything under ['profiler'] describes the capture that WAS attached, and none of it is keyed in a way that
    // notices the swap: every profiler query keys on the session id alone (metadata, call tree, CPU frames, source
    // locations, trace status, sample density). Profiles are session SUB-resources — that id deliberately does not
    // change when you switch capture — so TanStack kept serving the previous recording. The top banner updated,
    // because it reads activeProfileId off the session store, while every view below it did not.
    //
    // removeQueries, not invalidateQueries: an invalidated query still hands back its cached data on the next render
    // and refetches behind it, so the new capture would open showing one frame of the old one.
    queryClient.removeQueries({ queryKey: ['profiler'] });

    await refreshSession();
  };

  const attach = useMutation({
    mutationFn: (fileName: string) =>
      customFetch<Envelope<ProfileDto>>(`/api/sessions/${sessionId}/profile`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileName }),
      }),
    onSuccess: invalidate,
  });

  const detach = useMutation({
    mutationFn: (profileId: string) =>
      customFetch<Envelope<void>>(`/api/sessions/${sessionId}/profile/${profileId}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });

  return {
    profiles,
    databaseTsn,
    profilingsDirectory: query.data?.data?.profilingsDirectory ?? '',
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
    refetch: query.refetch,
    attach,
    detach,
  };
}
