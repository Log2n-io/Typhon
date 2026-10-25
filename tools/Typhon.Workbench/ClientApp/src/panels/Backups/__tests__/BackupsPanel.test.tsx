// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetchError } from '@/api/client';
import type { BackupJobDto, BackupListingDto, BackupStatusDto } from '@/api/generated/model';
import { useBackupsStore } from '@/stores/useBackupsStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * #1417 task 7: the Backups panel's states (PC-2) and its affordances (PC-6) over stubbed endpoints — no store named, an empty store, an error, a route
 * that reaches nothing, and the restore result's settle outcomes (WB-10: a 7012 is "needs the schema", never damage, and no "Open restored database"
 * is offered for an unsettled bundle).
 */

const api = vi.hoisted(() => ({
  status: vi.fn(),
  list: vi.fn(),
  archive: vi.fn(),
  jobs: vi.fn(),
  job: vi.fn(),
}));

vi.mock('@/api/generated/backups/backups', () => ({
  postApiBackupsStatus: api.status,
  postApiBackupsList: api.list,
  postApiBackupsArchive: api.archive,
  getApiBackupsJobs: api.jobs,
  getApiBackupsJobsJobId: api.job,
  postApiBackupsJobsCreate: vi.fn(),
  postApiBackupsJobsVerify: vi.fn(),
  postApiBackupsJobsDrill: vi.fn(),
  postApiBackupsJobsCompact: vi.fn(),
  postApiBackupsJobsRetention: vi.fn(),
  postApiBackupsJobsRestore: vi.fn(),
  postApiBackupsRestorePlan: vi.fn(),
  postApiBackupsRetentionPlan: vi.fn(),
  postApiBackupsArchiveResolveTime: vi.fn(),
  postApiBackupsArchiveLog: vi.fn(),
  deleteApiBackupsJobsJobId: vi.fn(),
}));
// jsdom has no EventSource; the job hook's stream is not what these cases exercise.
vi.mock('@/hooks/streams/useEventSource', () => ({ useEventSource: () => 'closed' }));

import BackupsPanel from '../BackupsPanel';

const status = (patch: Partial<BackupStatusDto> = {}): { data: BackupStatusDto } => ({
  data: {
    store: 'D:/store',
    bundle: 'C:/db/world.typhon',
    storeStatus: {
      health: 'Failing',
      reason: 'the store holds no point',
      points: 0,
      archive: [],
    } as unknown as BackupStatusDto['storeStatus'],
    storeError: null,
    route: {
      kind: 'offline',
      reason: 'Nobody holds the database: the point is taken from the closed bundle.',
      sessionId: null,
    },
    runningJobId: null,
    defaultScratch: 'C:/scratch',
    ...patch,
  } as BackupStatusDto,
});

const emptyListing: { data: BackupListingDto } = {
  data: {
    storeId: 's',
    databaseId: 'd',
    points: [],
    chains: [],
    unreadablePacks: [],
    temporaryFiles: [],
    restorablePoints: [],
    drills: [],
  },
};

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BackupsPanel />
    </QueryClientProvider>,
  );
}

const restoreJob = (settle: Record<string, unknown>): { data: BackupJobDto } => ({
  data: {
    jobId: 'j1',
    kind: 'restore',
    store: 'D:/store',
    state: 'done',
    phase: 'settle',
    completed: 0,
    total: 0,
    startedUtc: '2026-10-21T10:00:00Z',
    restore: {
      target: 'C:/restored/world.typhon',
      pagesWritten: 64,
      chainDepth: 1,
      tailSegments: 1,
      effectiveLsn: 150,
      settle,
    },
  } as unknown as BackupJobDto,
});

beforeEach(() => {
  useSessionStore.setState({ kind: 'none', filePath: null, sessionId: null });
  useBackupsStore.setState({
    bundlePath: '',
    storePath: '',
    activeJobId: null,
    storeByBundle: {},
    wizardOpen: false,
    retentionOpen: false,
  });
  api.status.mockResolvedValue(status());
  api.list.mockResolvedValue(emptyListing);
  api.archive.mockResolvedValue({ data: { timelines: [] } });
  api.jobs.mockResolvedValue({ data: [] });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('BackupsPanel', () => {
  it('with no store named, says what to name — never a blank panel', () => {
    mount();
    expect(screen.getByTestId('backups-no-store').textContent).toContain('backup store');
    expect(api.status).not.toHaveBeenCalled();
  });

  it('an empty store: the verdict, Back up now by its route, and no verify, retention or restore to offer', async () => {
    useBackupsStore.setState({ bundlePath: 'C:/db/world.typhon', storePath: 'D:/store' });
    mount();
    expect((await screen.findByTestId('backups-health-reason')).textContent).toBe('the store holds no point');
    expect(screen.getByTestId('backups-backup-now')).toBeTruthy();
    expect(screen.queryByTestId('backups-verify')).toBeNull();
    expect(screen.queryByTestId('backups-restore')).toBeNull();
    expect((await screen.findByTestId('backups-timeline')).textContent).toContain('holds no point yet');
  });

  it('a route that reaches nothing: Back up now is absent, its reason shown', async () => {
    api.status.mockResolvedValue(
      status({ route: { kind: 'unavailable', reason: 'No database bundle: enter the path.', sessionId: null } }),
    );
    useBackupsStore.setState({ storePath: 'D:/store' });
    mount();
    expect((await screen.findByTestId('backups-backup-unavailable')).textContent).toContain('No database bundle');
    expect(screen.queryByTestId('backups-backup-now')).toBeNull();
  });

  it('an error reads its problem’s sentence, with a retry', async () => {
    api.status.mockRejectedValue(
      new FetchError(400, { title: 'invalid_request', detail: 'A store path is required.' }),
    );
    useBackupsStore.setState({ storePath: 'D:/store' });
    mount();
    expect(await screen.findByText('A store path is required.')).toBeTruthy();
    expect(screen.getByRole('button', { name: /retry/i })).toBeTruthy();
  });

  it('a settled restore offers to open it; one that needs the schema says so and offers nothing', async () => {
    useBackupsStore.setState({ storePath: 'D:/store', activeJobId: 'j1' });
    api.job.mockResolvedValue(
      restoreJob({
        ran: true,
        crashRecoveryRan: true,
        recovery: { recordsReplayed: 3 },
        error: null,
        needsSchema: false,
      }),
    );
    mount();
    expect(await screen.findByTestId('backups-open-restored')).toBeTruthy();
    cleanup();

    api.job.mockResolvedValue(
      restoreJob({ ran: true, crashRecoveryRan: false, error: 'records of CompAArch…', needsSchema: true }),
    );
    mount();
    expect((await screen.findByTestId('backups-settle-needs-schema')).textContent).toContain('not damage');
    expect(screen.queryByTestId('backups-open-restored')).toBeNull();
  });
  it('a typed bundle brings back its remembered store, and the memory keeps it (PC-1)', async () => {
    useBackupsStore.setState({
      bundlePath: 'C:/db/a.typhon',
      storePath: 'D:/store-a',
      storeByBundle: { 'c:/db/a.typhon': 'D:/store-a', 'c:/db/b.typhon': 'D:/store-b' },
    });
    mount();
    const input = (await screen.findByTestId('backups-bundle')) as HTMLInputElement;

    fireEvent.change(input, { target: { value: 'C:/db/b.typhon' } });
    fireEvent.blur(input);

    const s = useBackupsStore.getState();
    expect(s.bundlePath).toBe('C:/db/b.typhon');
    expect(s.storePath).toBe('D:/store-b');
    expect(s.storeByBundle['c:/db/b.typhon']).toBe('D:/store-b');
    expect(s.storeByBundle['c:/db/a.typhon']).toBe('D:/store-a');
  });

  it('a bundle set from elsewhere (the Integrity handoff) shows in the field, and a blur does not put the old one back', async () => {
    useBackupsStore.setState({ bundlePath: 'C:/db/a.typhon', storePath: 'D:/store' });
    mount();
    const input = (await screen.findByTestId('backups-bundle')) as HTMLInputElement;

    act(() => useBackupsStore.getState().setBundle('C:/db/b.typhon'));
    expect(input.value).toBe('C:/db/b.typhon');
    fireEvent.blur(input);
    expect(useBackupsStore.getState().bundlePath).toBe('C:/db/b.typhon');
  });

  it('a path field losing focus with nothing changed re-reads nothing', async () => {
    useBackupsStore.setState({ bundlePath: 'C:/db/world.typhon', storePath: 'D:/store' });
    mount();
    const input = await screen.findByTestId('backups-store');
    await screen.findByTestId('backups-health-reason');
    const reads = api.status.mock.calls.length;

    fireEvent.blur(input);
    fireEvent.blur(screen.getByTestId('backups-bundle'));

    expect(api.status.mock.calls.length).toBe(reads);
  });

  it('a cancelled retention says it stopped between deletions, not that nothing was published', async () => {
    useBackupsStore.setState({ storePath: 'D:/store', activeJobId: 'j2' });
    api.job.mockResolvedValue({
      data: {
        jobId: 'j2',
        kind: 'retention',
        store: 'D:/store',
        state: 'cancelled',
        phase: 'compact',
        completed: 0,
        total: 0,
        startedUtc: '2026-10-21T10:00:00Z',
      } as unknown as BackupJobDto,
    });
    mount();
    const card = await screen.findByTestId('backups-job-cancelled');
    expect(card.textContent).not.toContain('nothing was published');
    expect(card.textContent).toContain('between');
  });
});
