// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import SystemAccessSummary from '@/panels/schemaCommon/SystemAccessSummary';

// Stage 3 Phase 3 (3C) — the shared declared-access rendering used by both the Inspector System card and the
// System DAG side panel. Presentational, so a props-only render covers it.
const EMPTY: Omit<React.ComponentProps<typeof SystemAccessSummary>, 'hasAccess'> = {
  reads: [], readsFresh: [], readsSnapshot: [], writes: [], sideWrites: [],
  readsEvents: [], writesEvents: [], readsResources: [], writesResources: [],
};

afterEach(cleanup);

describe('SystemAccessSummary', () => {
  it('renders a chip per item under its labelled group; empty groups are omitted', () => {
    render(<SystemAccessSummary {...EMPTY} reads={['Position']} writes={['Velocity', 'Health']} hasAccess />);
    expect(screen.getByText('reads')).toBeTruthy();
    expect(screen.getByText('Position')).toBeTruthy();
    expect(screen.getByText('writes')).toBeTruthy();
    expect(screen.getByText('Velocity')).toBeTruthy();
    expect(screen.getByText('Health')).toBeTruthy();
    expect(screen.queryByText('side-writes')).toBeNull(); // empty → no label
  });

  it('renders event and resource access on their own producer/consumer sides', () => {
    render(<SystemAccessSummary {...EMPTY} writesEvents={['AntDied']} readsResources={['SpatialIndex']} hasAccess />);
    expect(screen.getByText('writes events')).toBeTruthy();
    expect(screen.getByText('AntDied')).toBeTruthy();
    expect(screen.getByText('reads resources')).toBeTruthy();
    expect(screen.getByText('SpatialIndex')).toBeTruthy();
  });

  it('defaults to the trace-version note when hasAccess is false and no reason is given', () => {
    render(<SystemAccessSummary {...EMPTY} hasAccess={false} />);
    expect(screen.getByText(/No RFC 07 access declarations on the wire/i)).toBeTruthy();
    expect(screen.getByText(/predate wire v6/i)).toBeTruthy();
  });

  it('shows the engine-internal note (no trace-version excuse) for an engine system', () => {
    render(<SystemAccessSummary {...EMPTY} hasAccess={false} noAccessReason="engine-internal" />);
    expect(screen.getByText(/Engine-internal system/i)).toBeTruthy();
    expect(screen.getByText(/by design/i)).toBeTruthy();
    expect(screen.queryByText(/predate wire v6/i)).toBeNull();
  });

  it('shows the declares-none note for a user system that simply declares nothing', () => {
    render(<SystemAccessSummary {...EMPTY} hasAccess={false} noAccessReason="declares-none" />);
    expect(screen.getByText(/declares no RFC 07 access/i)).toBeTruthy();
    expect(screen.queryByText(/predate wire v6/i)).toBeNull();
  });
});
