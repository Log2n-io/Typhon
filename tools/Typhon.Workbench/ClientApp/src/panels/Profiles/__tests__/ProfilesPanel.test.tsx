// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { Profile } from '@/hooks/profiles/useProfileList';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';

// Mutable holder so each test can swap what the (mocked) data hook returns. `vi.hoisted` lifts it above the
// hoisted `vi.mock` factory below.
const hoisted = vi.hoisted(() => ({
  profiles: [] as unknown[],
  attachError: null as Error | null,
  attached: [] as string[],
  detached: [] as string[],
  revealed: 0,
}));

// The panel switches to the Profiler view on a successful attach; stub the dock command so the assertion is about
// intent rather than about dockview being mounted.
vi.mock('@/shell/commands/profilerCommands', () => ({
  toggleViewProfiler: () => { hoisted.revealed++; },
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
      mutate: (f: string, opts?: { onSuccess?: () => void }) => {
        hoisted.attached.push(f);
        // A rejected attach must not reveal the profiler, so the mock only fires onSuccess when there is no error.
        if (!hoisted.attachError) opts?.onSuccess?.();
      },
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
    hoisted.revealed = 0;
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

  it('puts the open marker in the FIRST column', () => {
    // Which session is open is the one fact this list is scanned for. In the last column it sits off the right edge of
    // a left-docked navigator and is never seen.
    hoisted.profiles = [makeProfile({ profileId: 'pid-1', isActive: true })];
    renderPanel();

    const cells = screen.getAllByRole('row')[1].querySelectorAll('td');
    expect(cells[0].textContent).toBe('open');
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

  it('double-clicking an unattached row opens it, and an attached row closes it', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    const { rerender } = renderPanel();
    fireEvent.doubleClick(screen.getAllByRole('row')[1]);   // [0] is the header row
    expect(hoisted.attached).toEqual(['a.typhon-trace']);

    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace', profileId: 'pid-1', isActive: true })];
    rerender(<ProfilesPanel {...({} as IDockviewPanelProps)} />);
    fireEvent.doubleClick(screen.getAllByRole('row')[1]);
    expect(hoisted.detached).toEqual(['pid-1']);
  });

  // ProfileHost.Attach ADDS — it is plural by design, so two captures of one database can be compared later. The
  // "one at a time" policy therefore belongs here. Without it, opening a second capture left the first attached: a
  // TraceSessionRuntime alive with a decoded capture in memory and file handles open INSIDE the bundle, which is
  // exactly what has to be released before the database can be closed or yielded to an application.
  it('opening a second capture releases the one it replaces', () => {
    hoisted.profiles = [
      makeProfile({ fileName: 'open.typhon-trace', profileId: 'pid-open', isActive: true }),
      makeProfile({ fileName: 'next.typhon-trace' }),
    ];
    renderPanel();

    fireEvent.doubleClick(screen.getAllByRole('row')[2]); // [0] header, [1] the already-open capture

    expect(hoisted.attached).toEqual(['next.typhon-trace']);
    expect(hoisted.detached).toEqual(['pid-open']);
  });

  it('a rejected attach releases nothing — you keep the capture you had', () => {
    // The wrong-database guard fires on exactly the captures a user is most likely to try. Detaching first would turn
    // "that one does not belong here" into a session that also lost the capture it already had.
    hoisted.profiles = [
      makeProfile({ fileName: 'open.typhon-trace', profileId: 'pid-open', isActive: true }),
      makeProfile({ fileName: 'foreign.typhon-trace' }),
    ];
    hoisted.attachError = new Error('recorded against another database');
    renderPanel();

    fireEvent.doubleClick(screen.getAllByRole('row')[2]);

    expect(hoisted.detached).toEqual([]);
  });

  // Single click had a contract ("selects, does not act") and no observable result: it set a background colour, and
  // even that was suppressed on the open row. The capture's provenance — which database recorded it — was read,
  // typed and mapped into the row model and then displayed nowhere at all.
  it('single click publishes the capture to the Inspector', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace', databaseName: 'world-shard' })];
    renderPanel();

    fireEvent.click(screen.getAllByRole('row')[1]);

    const leaf = useSelectionStore.getState().leaf;
    expect(leaf?.type).toBe('capture');
    expect((leaf?.ref as Profile).fileName).toBe('a.typhon-trace');
    expect((leaf?.ref as Profile).databaseName).toBe('world-shard');
  });

  it('single click still does not attach or detach', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    renderPanel();

    fireEvent.click(screen.getAllByRole('row')[1]);

    expect(hoisted.attached).toEqual([]);
    expect(hoisted.detached).toEqual([]);
  });

  it('opening the first capture detaches nothing', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    renderPanel();

    fireEvent.doubleClick(screen.getAllByRole('row')[1]);

    expect(hoisted.attached).toEqual(['a.typhon-trace']);
    expect(hoisted.detached).toEqual([]);
  });

  it('opening a profile switches to the Profiler view', () => {
    // Opening a capture in order to then go hunting for the timeline is a step nobody wants; the reason to open it is
    // to look at it. This also covers the panel not existing yet: an Open session's dock layout is built before the
    // profiler capability is gained, so revealing it has to be able to create it.
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    renderPanel();

    fireEvent.doubleClick(screen.getAllByRole('row')[1]);

    expect(hoisted.attached).toEqual(['a.typhon-trace']);
    expect(hoisted.revealed).toBe(1);
  });

  it('a rejected attach does not switch away from the reason it was rejected', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    hoisted.attachError = new Error('recorded against another database');
    renderPanel();

    fireEvent.doubleClick(screen.getAllByRole('row')[1]);

    expect(hoisted.revealed).toBe(0);
  });

  it('single click only selects — it must not toggle', () => {
    // Attach and detach are the same gesture, so when a single click acted, a double click ran the toggle twice and
    // landed back where it started. The row looked dead to anyone who double-clicked it, which is what people try first.
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    renderPanel();

    fireEvent.click(screen.getAllByRole('row')[1]);

    expect(hoisted.attached).toEqual([]);
    expect(hoisted.detached).toEqual([]);
  });

  it('Enter activates the focused row, so the panel works without a pointer', () => {
    hoisted.profiles = [makeProfile({ fileName: 'a.typhon-trace' })];
    renderPanel();

    fireEvent.keyDown(screen.getAllByRole('row')[1], { key: 'Enter' });

    expect(hoisted.attached).toEqual(['a.typhon-trace']);
  });

  it('tells the user where captures would go when there are none', () => {
    hoisted.profiles = [];
    renderPanel();
    expect(screen.getByText(/No captures recorded against this database yet/)).toBeTruthy();
    expect(screen.getByText('C:/db/world.typhon/profilings')).toBeTruthy();
  });

  it('explains itself rather than showing an empty table when the session has no database', () => {
    useSessionStore.setState({ kind: 'open', capabilities: ['profiler'] });
    hoisted.profiles = [];
    renderPanel();
    expect(screen.getByText(/Profile sessions live with a database/)).toBeTruthy();
  });
});
