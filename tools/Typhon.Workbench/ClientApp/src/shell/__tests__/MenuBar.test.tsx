// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MenuBar from '@/shell/MenuBar';
import { useSessionStore, type SessionKind } from '@/stores/useSessionStore';

// IA §5.1 regression: the View menu shows ONLY the panels the current session kind can actually open — a view that
// can't run in this mode is ABSENT, never a greyed-out dead entry (the old disabled+tooltip, friction F8). These
// tests open the View menu in each mode and assert which items render. The real risk this guards is a mis-gated
// condition (e.g. a profiler view accidentally gated on the open session) — only a render check catches that.

// Radix Menubar drives open/close through pointer-capture + scroll-into-view APIs jsdom lacks; shim them so the
// menu can open without throwing.
beforeEach(() => {
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
});

afterEach(() => {
  cleanup();
  useSessionStore.setState({ kind: 'none', sessionId: null });
});

const OPEN_VIEWS = ['Schema', 'Data Browser', 'Database File Map', 'Storage Health', 'Query Console', 'Resource Tree'];
const PROFILER_VIEWS = [
  'Profiler',
  'Top Spans',
  'System DAG',
  'Critical Path',
  'Call Tree',
  'Source Preview',
  'Data Flow',
  'Query Analyzer',
  'Engine Health',
  'Systems & Queries',
];
const ALWAYS = ['Detail', 'Logs', 'Options'];

function setKind(kind: SessionKind) {
  useSessionStore.setState({ kind, sessionId: kind === 'none' ? null : 'sid' });
}

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MenuBar />
    </QueryClientProvider>,
  );
}

async function openViewMenu() {
  // Radix Menubar triggers open on pointer-down (button 0). fireEvent avoids the @testing-library/user-event dep.
  const trigger = screen.getByText('View');
  fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false, pointerType: 'mouse' });
  return screen.findByRole('menu'); // the opened MenubarContent (Radix portals it to document.body)
}

function expectPresent(menu: HTMLElement, labels: string[]) {
  for (const label of labels) {
    expect(within(menu).queryByText(label), `expected "${label}" to be present`).not.toBeNull();
  }
}

function expectAbsent(menu: HTMLElement, labels: string[]) {
  for (const label of labels) {
    expect(within(menu).queryByText(label), `expected "${label}" to be absent`).toBeNull();
  }
}

describe('View menu — session-kind visibility (IA §5.1)', () => {
  it('open (.typhon) session: database views shown, profiler views absent', async () => {
    setKind('open');
    mount();
    const menu = await openViewMenu();
    expectPresent(menu, OPEN_VIEWS);
    expectAbsent(menu, PROFILER_VIEWS);
    expectPresent(menu, ALWAYS);
  });

  it('trace/attach (profiler) session: profiler views shown, database views absent', async () => {
    setKind('attach');
    mount();
    const menu = await openViewMenu();
    expectPresent(menu, PROFILER_VIEWS);
    expectAbsent(menu, OPEN_VIEWS);
    expectPresent(menu, ALWAYS);
  });

  it('no session: only the always-on chrome — neither database nor profiler views', async () => {
    setKind('none');
    mount();
    const menu = await openViewMenu();
    expectAbsent(menu, [...OPEN_VIEWS, ...PROFILER_VIEWS]);
    expectPresent(menu, ALWAYS);
  });
});
