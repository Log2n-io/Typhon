import { describe, expect, it } from 'vitest';
import type { Descriptor } from '../src/assets/descriptor';
import { CameraInput, type CameraInputHandlers } from '../src/camera/camera-input';
import { EyeCamera } from '../src/camera/eye-camera';
import { MapCamera } from '../src/camera/map-camera';
import { ticketMenuItems, ticketTerminalOf } from '../src/app/ticket-terminal';
import { LONG_PRESS_MS, pressProgress } from '../src/render/press-ring';

describe('pressProgress', () => {
  it('fills over the long press and holds full after it', () => {
    expect(pressProgress(1000, 1000)).toBe(0);
    expect(pressProgress(1000, 1000 + LONG_PRESS_MS / 2)).toBeCloseTo(0.5, 9);
    expect(pressProgress(1000, 1000 + LONG_PRESS_MS)).toBe(1);
    expect(pressProgress(1000, 5000)).toBe(1);
    expect(LONG_PRESS_MS).toBe(500);
  });
});

describe('ticketMenuItems', () => {
  it('offers every other city, each queueing for its shuttle', () => {
    const items = ticketMenuItems(1, ['Port Sable', 'Whitestone', 'Dry Wells']);
    expect(items.map((i) => i.label)).toEqual(['Port Sable', 'Dry Wells']);
    expect(items.map((i) => i.action)).toEqual([
      { kind: 'board', city: 0 },
      { kind: 'board', city: 2 },
    ]);
  });
});

describe('ticketTerminalOf', () => {
  it("places the terminal's own box where it is drawn: the child's pose, through the X mirror", () => {
    const port = {
      id: 'tat_shuttleport',
      children: [{ asset: 'ticket_terminal', pos: [2, 0.3, 20], rot: [0, 0, 0, 1] }],
    } as unknown as Descriptor;
    const terminal = {
      id: 'ticket_terminal',
      interactions: [{ kind: 'shuttle_ticket', pos: [0, 0, 0.9], rot: [0, 0, 0, 1], forward: '-Z', radius: 1.5 }],
      bounds: {
        render: [
          [-0.4, 0, -0.3],
          [0.4, 1.8, 0.3],
        ],
        collision: null,
      },
    } as unknown as Descriptor;
    const use = ticketTerminalOf(port, new Map([['ticket_terminal', terminal]]), 1000, 50, 2000, 0);
    const corners = use?.corners ?? null;
    expect(corners).not.toBeNull();
    const xs = [0, 1, 2, 3, 4, 5, 6, 7].map((i) => corners![i * 3]);
    const ys = [0, 1, 2, 3, 4, 5, 6, 7].map((i) => corners![i * 3 + 1]);
    const zs = [0, 1, 2, 3, 4, 5, 6, 7].map((i) => corners![i * 3 + 2]);
    expect(Math.min(...xs)).toBeCloseTo(1000 - 2 - 0.4, 6);
    expect(Math.max(...xs)).toBeCloseTo(1000 - 2 + 0.4, 6);
    expect(Math.min(...ys)).toBeCloseTo(50.3, 6);
    expect(Math.max(...ys)).toBeCloseTo(52.1, 6);
    expect(Math.min(...zs)).toBeCloseTo(2000 + 20 - 0.3, 6);
    expect(Math.max(...zs)).toBeCloseTo(2000 + 20 + 0.3, 6);
  });
});

/** The pointer half of the long press: what the input tells its owner, in which order. */
describe('CameraInput press hooks', () => {
  type Listener = (event: unknown) => void;
  const setup = (): { fire: (type: string, event: Record<string, unknown>) => void; log: string[] } => {
    const listeners: { type: string; listener: Listener }[] = [];
    const target = globalThis as unknown as Record<string, unknown>;
    target['window'] = { addEventListener: () => {}, removeEventListener: () => {} };
    const absent = function Absent(): void {};
    target['HTMLInputElement'] = absent;
    target['HTMLSelectElement'] = absent;
    target['HTMLTextAreaElement'] = absent;
    const canvas = {
      addEventListener: (type: string, listener: Listener) => {
        listeners.push({ type, listener });
      },
      focus: () => {},
      setPointerCapture: () => {},
      releasePointerCapture: () => {},
    } as unknown as HTMLCanvasElement;
    const log: string[] = [];
    const handlers: CameraInputHandlers = {
      onClick: (x, y) => log.push(`click ${x},${y}`),
      onManualMove: () => {},
      onPress: (x, y) => log.push(`press ${x},${y}`),
      onRelease: () => log.push('release'),
      onDragStart: () => log.push('drag'),
    };
    new CameraInput(canvas, new MapCamera(), { width: 800, height: 600 }, handlers, new EyeCamera(), () => true);
    const fire = (type: string, event: Record<string, unknown>): void => {
      for (const l of listeners) {
        if (l.type === type) {
          l.listener({ pointerId: 1, preventDefault: () => {}, ...event });
        }
      }
    };
    return { fire, log };
  };

  it('reports the press, then the release before the click it turns out to be', () => {
    const { fire, log } = setup();
    fire('pointerdown', { button: 0, buttons: 1, offsetX: 100, offsetY: 50 });
    fire('pointerup', { button: 0, buttons: 0, offsetX: 101, offsetY: 50 });
    expect(log).toEqual(['press 100,50', 'release', 'click 100,50']);
  });

  it('reports a drag once the press moves past the threshold, and no click', () => {
    const { fire, log } = setup();
    fire('pointerdown', { button: 0, buttons: 1, offsetX: 100, offsetY: 50 });
    fire('pointermove', { buttons: 1, offsetX: 102, offsetY: 50 });
    fire('pointermove', { buttons: 1, offsetX: 140, offsetY: 50 });
    fire('pointermove', { buttons: 1, offsetX: 160, offsetY: 50 });
    fire('pointerup', { button: 0, buttons: 0, offsetX: 160, offsetY: 50 });
    expect(log).toEqual(['press 100,50', 'drag', 'release']);
  });

  it('does not report a right button as a press', () => {
    const { fire, log } = setup();
    fire('pointerdown', { button: 2, buttons: 2, offsetX: 100, offsetY: 50 });
    expect(log).toEqual([]);
  });
});
