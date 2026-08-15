// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { Profile } from '@/hooks/profiles/useProfileList';
import { useSessionStore } from '@/stores/useSessionStore';
import { CaptureDetailCard } from '../CaptureDetailCard';

afterEach(cleanup);

function makeProfile(over: Partial<Profile> = {}): Profile {
  return {
    fileName: '20260814-220312-943.typhon-trace',
    profileId: null,
    isActive: false,
    createdUtcTicks: 638_900_000_000_000_000,
    durationTicks: 10_000_000,
    timestampFrequency: 10_000_000,
    tickCount: 200,
    tsnMin: 1,
    tsnMax: 5_000,
    databaseId: 'db-guid-1',
    databaseName: 'world-shard',
    multipleEnginesObserved: false,
    sizeBytes: 22_937_035,
    isPinned: false,
    isReadable: true,
    belongsToDatabase: true,
    driftTransactions: 0,
    ...over,
  };
}

describe('CaptureDetailCard', () => {
  // The reason the card exists: databaseId/databaseName ride in every capture header (#617 D-2) and were mapped all
  // the way into the row model, then rendered nowhere.
  it('names the database the capture was recorded against', () => {
    render(<CaptureDetailCard profile={makeProfile()} />);

    expect(screen.getByText('world-shard')).toBeTruthy();
    expect(screen.getByText('db-guid-1')).toBeTruthy();
  });

  it('says plainly when a capture belongs to a different database', () => {
    // This is the state that renders dimmed in the list, has its drift withheld, and is refused by the wrong-database
    // guard on attach — while nothing anywhere named the database it actually came from.
    useSessionStore.setState({ filePath: 'C:/dev/other.typhon' });
    render(<CaptureDetailCard profile={makeProfile({ belongsToDatabase: false, databaseName: 'elsewhere' })} />);

    expect(screen.getByText('elsewhere')).toBeTruthy();
    expect(screen.getByText(/recorded elsewhere and cannot be attached/i)).toBeTruthy();
    expect(screen.getByText('other.typhon')).toBeTruthy();
  });

  it('does not report a drift figure it cannot compute', () => {
    // null is "not comparable", not zero. Printing "current" for a foreign or unclosed capture would be a guess.
    render(<CaptureDetailCard profile={makeProfile({ driftTransactions: null, belongsToDatabase: false })} />);

    expect(screen.getByText(/not comparable/i)).toBeTruthy();
    expect(screen.queryByText('current')).toBeNull();
  });

  it('reports drift in transactions when it is comparable', () => {
    render(<CaptureDetailCard profile={makeProfile({ driftTransactions: 845_331 })} />);

    expect(screen.getByText(/845,331 transactions behind/)).toBeTruthy();
  });

  it('warns when more than one engine wrote to the capture', () => {
    render(<CaptureDetailCard profile={makeProfile({ multipleEnginesObserved: true })} />);

    expect(screen.getByText(/more than one engine/i)).toBeTruthy();
  });

  it('flags an unreadable capture rather than rendering blank figures', () => {
    render(<CaptureDetailCard profile={makeProfile({ isReadable: false })} />);

    expect(screen.getByText(/could not be read as a capture/i)).toBeTruthy();
  });

  it('shows an em dash for absent header fields instead of year 1 or 0 B', () => {
    render(
      <CaptureDetailCard
        profile={makeProfile({ createdUtcTicks: 0, sizeBytes: 0, databaseName: '', databaseId: '', tsnMax: 0 })}
      />,
    );

    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
  });
});
