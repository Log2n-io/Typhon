import { beforeEach, describe, expect, it } from 'vitest';

import {
  applyWatchAction,
  beginWatch,
  stepWatch,
  watchFromMessage,
  watchFromParam,
  watchFromUrl,
  watchLabel,
  watchOriginAllowed,
  WATCH_APPROACH_DISTANCE_M,
  WATCH_TIMEOUT_MS,
  type WatchState,
  type WatchTarget,
  type WatchView,
} from '../src/app/watch-link';
import { useUi } from '../src/state/ui-store';

/** "Watch this bot" (bot-telemetry.md § 7): the dashboard names a traced bot's body, the god camera follows it. */

const BOT: WatchTarget = { netId: 3456, realm: 0, x: 120.5, z: -40, bot: 12 };

describe('the watch URL', () => {
  it('reads follow, realm, x, z and bot', () => {
    expect(watchFromUrl('?as=god&follow=3456&realm=7&x=120.5&z=-40&bot=12')).toEqual({ ...BOT, realm: 7 });
  });

  it('is no watch without a netId, or with nonsense', () => {
    expect(watchFromUrl('?as=god')).toBeNull();
    expect(watchFromUrl('?follow=0')).toBeNull();
    expect(watchFromUrl('?follow=abc')).toBeNull();
    expect(watchFromUrl('?follow=-3')).toBeNull();
    expect(watchFromUrl('?follow=1.5')).toBeNull();
  });

  it('defaults the realm to the planet and drops half a position', () => {
    expect(watchFromUrl('?follow=9')).toEqual({ netId: 9, realm: 0, x: null, z: null, bot: null });
    expect(watchFromUrl('?follow=9&x=10')).toEqual({ netId: 9, realm: 0, x: null, z: null, bot: null });
    expect(watchFromUrl('?follow=9&x=10&z=nope')?.x).toBeNull();
  });

  it('reads the extra origin', () => {
    expect(watchFromParam('?watchFrom=http%3A%2F%2Fdash.lan%3A5090')).toBe('http://dash.lan:5090');
    expect(watchFromParam('?as=god')).toBeNull();
  });
});

describe('the follow message', () => {
  it('is read from the dashboard shape', () => {
    expect(watchFromMessage({ kind: 'swg-follow', netId: 3456, realm: 0, x: 120.5, z: -40, bot: 12 })).toEqual(BOT);
  });

  it('ignores anything else a page may post', () => {
    expect(watchFromMessage(null)).toBeNull();
    expect(watchFromMessage('swg-follow')).toBeNull();
    expect(watchFromMessage({ kind: 'other', netId: 1 })).toBeNull();
    expect(watchFromMessage({ kind: 'swg-follow' })).toBeNull();
    expect(watchFromMessage({ kind: 'swg-follow', netId: Number.NaN })).toBeNull();
  });
});

describe('the origin rule', () => {
  it('accepts the same host on any port', () => {
    expect(watchOriginAllowed('http://loicr9:5090', 'loicr9', null)).toBe(true);
    expect(watchOriginAllowed('http://192.168.1.65:5090', '192.168.1.65', null)).toBe(true);
    expect(watchOriginAllowed('https://loicr9', 'loicr9', null)).toBe(true);
  });

  it('refuses a foreign host and garbage', () => {
    expect(watchOriginAllowed('http://evil.example:5090', 'loicr9', null)).toBe(false);
    expect(watchOriginAllowed('null', 'loicr9', null)).toBe(false);
    expect(watchOriginAllowed('', 'loicr9', null)).toBe(false);
  });

  it('honours watchFrom, by exact origin', () => {
    expect(watchOriginAllowed('http://dash.lan:5090', 'loicr9', 'http://dash.lan:5090')).toBe(true);
    expect(watchOriginAllowed('http://dash.lan:5090', 'loicr9', 'http://dash.lan:5090/bots')).toBe(true);
    expect(watchOriginAllowed('http://dash.lan:5091', 'loicr9', 'http://dash.lan:5090')).toBe(false);
    expect(watchOriginAllowed('http://dash.lan:5090', 'loicr9', 'not a url')).toBe(false);
  });
});

describe('the watch step', () => {
  const view = (v: Partial<WatchView>): WatchView => ({ realmId: 0, canViewRealm: true, found: false, ...v });

  it('asks for the realm once, flies over the last position, then follows the body when it is served', () => {
    let s: WatchState = beginWatch({ ...BOT, realm: 4 }, 0);

    let step = stepWatch(s, view({ realmId: null }), 10);
    expect(step.action.kind).toBe('none');
    expect(step.state).toBe(s);

    step = stepWatch(s, view({ realmId: 0 }), 20);
    expect(step.action).toEqual({ kind: 'viewRealm', realm: 4 });
    s = step.state;

    step = stepWatch(s, view({ realmId: 0 }), 30);
    expect(step.action.kind).toBe('none');
    expect(step.state).toBe(s);

    step = stepWatch(s, view({ realmId: 4 }), 40);
    expect(step.action).toEqual({ kind: 'flyTo', x: 120.5, z: -40 });
    s = step.state;
    expect(s.phase).toBe('acquiring');

    step = stepWatch(s, view({ realmId: 4 }), 50);
    expect(step.action.kind).toBe('none');
    expect(step.state).toBe(s);

    step = stepWatch(s, view({ realmId: 4, found: true }), 60);
    expect(step.action).toEqual({ kind: 'follow', netId: 3456 });
    expect(step.state.phase).toBe('following');
  });

  it('follows at once when the body is already in view', () => {
    const step = stepWatch(beginWatch(BOT, 0), view({ found: true }), 1);
    expect(step.action).toEqual({ kind: 'follow', netId: 3456 });
  });

  it('gives up when the realm cannot be asked for, or the body never comes', () => {
    expect(stepWatch(beginWatch({ ...BOT, realm: 4 }, 0), view({ canViewRealm: false }), 1).state.phase).toBe('lost');
    const s = stepWatch(beginWatch(BOT, 0), view({}), 1).state;
    expect(stepWatch(s, view({}), WATCH_TIMEOUT_MS + 1).state.phase).toBe('lost');
  });
});

describe('a follow, through the store', () => {
  beforeEach(() => {
    useUi.setState({ cameraMode: 'eye', selectedNetId: 77, follow: false, playing: false, watch: null });
  });

  it('selects the body and turns follow on in the god camera', () => {
    useUi.getState().watchBot(BOT);
    expect(useUi.getState().watch?.target).toEqual(BOT);

    const step = stepWatch(useUi.getState().watch!, { realmId: 0, canViewRealm: true, found: true }, 0);
    useUi.getState().setWatch(step.state);
    applyWatchAction(step.action, useUi.getState());

    const ui = useUi.getState();
    expect(ui.selectedNetId).toBe(3456);
    expect(ui.follow).toBe(true);
    expect(ui.cameraMode).toBe('god');
    expect(watchLabel(ui.watch, ui.selectedNetId)).toBe('Watching bot #12 (netId 3456)');
    expect(watchLabel(ui.watch, 1)).toBeNull();
  });

  it('flies the camera to the last position while the body is not served', () => {
    useUi.getState().watchBot(BOT);
    const step = stepWatch(useUi.getState().watch!, { realmId: 0, canViewRealm: true, found: false }, 0);
    applyWatchAction(step.action, useUi.getState());
    expect(useUi.getState().cameraRequest).toMatchObject({ x: 120.5, z: -40, distance: WATCH_APPROACH_DISTANCE_M });
  });
});
