// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { Profile } from '@/hooks/profiles/useProfileList';
import { useSessionStore } from '@/stores/useSessionStore';

// Mutable holder so each test can swap what the (mocked) data hook returns. `vi.hoisted` lifts it above the
// hoisted `vi.mock` factory below.
const hoisted = vi.hoisted(() => ({
  profiles: [] as unknown[],
  attachError: null as Error | null,
  attached: [] as string[],
  detached: [] as string[],
}));

vi.mock('@/hooks/profiles/useProfileList', () => ({
  useProfileList: () => ({
    profiles: hoisted.profiles,
    databaseTsn: 1_000,
    profilingsDirectory: 'C:/db/world.typhon/profilings',
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: () => undefined,
    attach: {
      isPending: false,
      error: hoisted.attachError,
      reset: () => undefined,
      mutate: (f: string) => hoisted.attached.push(f),
    },
    detach: {
      isPending: false,
      error: null,
      reset: () => undefined,
      mutate: (id: string) => hoisted.detached.push(id),
    },
  }),
}));

// Imported after the mock is registered (vi.mock calls are hoisted above all imports by vitest).
import ProfilesPanel from '../ProfilesPanel';

function makeProfile(over: Partial<Profile> = {}): Profile {
  return {
    fileName: '20260101-000000-000.typhon-trace',
    profileId: null,
    isActive: false,
    createdUtcTicks: 638_000_000_000_000_000,
    durationTicks: 2_000_000,
    timestampFrequency: 1_000_000,
    tickCount: 120,
    tsnMin: 1,
    tsnMax: 900,
    databaseId: '11111111-1111-1111-1111-111111111111',
    databaseName: 'world',
    multipleEnginesObserved: false,
    sizeBytes: 144_000,
    isPinned: false,
    isReadable: true,
    belongsToDatabase: true,
    driftTransactions: 100,
    ...over,
  };
}

function renderPanel() {
  return render(<ProfilesPanel {...({} as IDockviewPanelProps)} />);
}

describe('ProfilesPanel (#617)', () => {
  beforeEach(() => {
    hoisted.profiles = [];
    hoisted.attachError = null;
    hoisted.attached = [];
    hoisted.detached = [];
    useSessionStore.setState({ kind: 'open', sessionId: 'sid', capabilities: ['database'], activeProfileId: null });
  });
  afterEach(cleanup);

  it('reports drift in transactions, which is the readout §4.6 asks for', () => {
    hoisted.profiles = [makeProfile({ driftTransactions: 124 })];
    renderPanel();
    expect(screen.getByText('124 txns behind')).toBeTruthy();
  });

  it('says "current" when the capture closed at the database\'s present transaction', () => {
    hoisted.profiles = [makeProfile({ driftTransactions: 0 })];
    renderPanel();
    expect(screen.getByText('current')).toBeTruthy();
  });

  it('withholds the drift figure for a capture belonging to another database', () => {
    // The case D-1 warns about: co-location is not provenance. Subtracting this capture's TsnMax from THIS database's
    // transaction number would render a confident number drawn from two unrelated sequences.
    hoisted.profiles = [makeProfile({ belongsToDatabase: false, driftTransactions: null, databaseName: 'other' })];
    renderPanel();

    expect(screen.getByText('other database')).toBeTruthy();
    expect(screen.queryByText(/txns behind/)).toBeNull();
  });

  it('does not call an unreadable capture "other database" — it cannot know that', () => {
    // The server reports belongsToDatabase false for a file it could not parse, because it cannot vouch for it. That is
    // not the same claim as "this belongs to someone else"; a truncated capture of THIS database is the likelier cause.
    hoisted.profiles = [makeProfile({ isReadable: false, belongsToDatabase: false, driftTransactions: null, tsnMax: 0 })];
    renderPanel();

    expect(screen.queryByText('other database')).toBeNull();
    expect(screen.getByText(/unreadable/)).toBeTruthy();
  });

  it('surfaces a rejected attach instead of looking like the click did nothing', () => {
    hoisted.profiles = [makeProfile()];
    hoisted.attachError = new Error('This capture was recorded against database 2c37…, not the one this session has open.');
    renderPanel();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('not the one this session has open');
  });

  it('clicking an unattached row attaches it and an attached row detaches it', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    const { rerender } = renderPanel();
    fireEvent.click(screen.getAllByRole('row')[1]);   // [0] is the header row
    expect(hoisted.attached).toEqual(['a.typhon-trace']);

    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace', profileId: 'pid-1', isActive: true })];
    rerender(<ProfilesPanel {...({} as IDockviewPanelProps)} />);
    fireEvent.click(screen.getByText('open'));
    expect(hoisted.detached).toEqual(['pid-1']);
  });

  it('tells the user where captures would go when there are none', () => {
    hoisted.profiles = [];
    renderPanel();
    expect(screen.getByText(/No captures recorded against this database yet/)).toBeTruthy();
    expect(screen.getByText('C:/db/world.typhon/profilings')).toBeTruthy();
  });

  it('explains itself rather than showing an empty table when the session has no database', () => {
    useSessionStore.setState({ kind: 'trace', capabilities: ['profiler'] });
    hoisted.profiles = [];
    renderPanel();
    expect(screen.getByText(/Profiles live with a database/)).toBeTruthy();
  });
});
