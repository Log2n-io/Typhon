/**
 * Helpers for the global query-cache / mutation-cache error logger in `main.tsx`. A call site
 * opts a query out of the Logs panel by setting `meta.silenceErrors`:
 *
 *   - `true` — always silence. Use when every failure is expected by design (e.g. `fs/stat` on a
 *     moved/deleted recent file).
 *   - `(error) => boolean` — predicate. Use when only certain failure modes are expected (e.g.
 *     typeName schema hooks: silence 404 when a URL-restored selection doesn't exist in the new
 *     session, but still log 500s so server faults reach the Logs panel).
 *
 * The QueryMeta type from TanStack Query is `Record<string, unknown>`, so callers and this
 * helper agree on the key at runtime — we don't have a typed handshake. Keep the predicate
 * signature tiny and stable.
 */
export type SilenceErrorsSetting = true | ((error: unknown) => boolean);

interface MetaShape {
  silenceErrors?: SilenceErrorsSetting;
}

export function shouldSilence(error: unknown, meta: MetaShape | Record<string, unknown> | undefined): boolean {
  if (!meta) return false;
  const setting = (meta as MetaShape).silenceErrors;
  if (setting === true) return true;
  if (typeof setting === 'function') return setting(error);
  return false;
}
