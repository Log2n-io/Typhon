import { usePostApiSessionsFile } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useRecentFilesStore, type RecentFileState } from '@/stores/useRecentFilesStore';
import { logError, logInfo, logWarn } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import type { SessionDto } from '@/api/generated/model';

/**
 * Opens a `.typhon` database file into a new Workbench session and wires the result into app state (session
 * store + recent-files list + Logs panel). This is the single source of the "open a database file" flow, shared
 * by the Connect dialog and the `typhon ui <db>` startup auto-open (#429) so both behave identically.
 *
 * The returned {@link mutation} exposes the underlying request state (`isPending`, `isError`, `error`) for
 * callers that render spinners / error pills. `openDatabaseFile` rethrows on failure (after logging) so a caller
 * can react; startup auto-open simply swallows it.
 */
export function useOpenDatabaseFile() {
  const setSession = useSessionStore((s) => s.setSession);
  const recordRecent = useRecentFilesStore((s) => s.record);
  const mutation = usePostApiSessionsFile();

  const openDatabaseFile = async (filePath: string, schemaDllPaths: string[]): Promise<SessionDto> => {
    logInfo(`Opening database: ${filePath}`, { filePath, explicitSchemaDlls: schemaDllPaths });
    try {
      const response = await mutation.mutateAsync({
        data: {
          filePath,
          schemaDllPaths: schemaDllPaths.length > 0 ? schemaDllPaths : undefined,
        },
      });
      const dto = response.data;
      setSession(dto);
      recordRecent({
        filePath: dto.filePath ?? filePath,
        schemaDllPaths: (dto.schemaDllPaths as string[] | null | undefined) ?? schemaDllPaths,
        lastOpenedAt: new Date().toISOString(),
        lastState: (dto.state as RecentFileState) ?? 'Ready',
        kind: 'db',
      });

      const resolvedDlls = (dto.schemaDllPaths as string[] | null | undefined) ?? [];
      const diagnostics = (dto.schemaDiagnostics ?? []) as Array<{
        componentName?: string | null;
        kind?: string | null;
        detail?: string | null;
      }>;
      const sessionState = dto.state ?? 'Ready';
      const logLevel = sessionState === 'Incompatible' ? logError : sessionState === 'Ready' ? logInfo : logWarn;
      logLevel(
        `Session opened — state: ${sessionState} (${dto.loadedComponentTypes ?? 0} component types loaded)`,
        {
          sessionId: dto.sessionId,
          filePath: dto.filePath ?? filePath,
          schemaStatus: dto.schemaStatus,
          schemaDllPaths: resolvedDlls,
          diagnostics: diagnostics.length > 0 ? diagnostics : undefined,
        },
      );

      return dto;
    } catch (err) {
      logError(`Failed to open database: ${filePath}`, {
        filePath,
        error: extractDetail(err) || String(err),
      });
      throw err;
    }
  };

  return { openDatabaseFile, mutation };
}
