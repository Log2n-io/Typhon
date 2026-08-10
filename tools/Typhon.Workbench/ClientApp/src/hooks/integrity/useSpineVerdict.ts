import { useQuery } from '@tanstack/react-query';
import { postApiIntegrityScan } from '@/api/generated/integrity/integrity';
import { normalizeReport, type Report } from '@/panels/Integrity/integrityModel';

/**
 * A cheap `Spine`-depth scan of one bundle, for the Storage Health verdict strip.
 *
 * Unlike the full scan this *is* modelled as a query, because the cost profile is completely different:
 * Spine follows the bootstrap and segment roots only, so it is bounded by segment count rather than
 * database size. It is the same tier the engine runs on every open. Re-running it when the panel mounts
 * costs a few dozen page reads, so it can behave like any other dashboard metric.
 *
 * The database is live when this runs (a session holds it), so findings come back with `Suspected`
 * confidence. That is why the strip links to the full view rather than pronouncing a verdict as final —
 * a scan of a database someone is writing to cannot confirm anything.
 */
export function useSpineVerdict(path: string | null) {
  return useQuery<Report>({
    queryKey: ['integrity', 'spine', path],
    enabled: !!path,
    // A verdict is not a live metric — the operator refreshes Storage Health when they want a new one.
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    retry: false,
    queryFn: async () => {
      const res = await postApiIntegrityScan({ path: path as string, depth: 'spine' });
      return normalizeReport(res.data);
    },
  });
}
