import { describe, expect, it } from 'vitest';
import { dashboardPortOf, dashboardUrl } from '../src/data/server-config';

/** The HUD's link to the demo's dashboard: on the host this page came from, at the port the server announces. */
describe('the dashboard link', () => {
  it('is the page host at the announced port', () => {
    expect(dashboardUrl(5090, { protocol: 'http:', hostname: 'loicr9' })).toBe('http://loicr9:5090/');
    expect(dashboardUrl(5090, { protocol: 'https:', hostname: '192.168.1.65' })).toBe('https://192.168.1.65:5090/');
  });

  it('is absent when the server announces none, or nonsense', () => {
    expect(dashboardUrl(null, { protocol: 'http:', hostname: 'localhost' })).toBeNull();
    expect(dashboardPortOf(undefined)).toBeNull();
    expect(dashboardPortOf(0)).toBeNull();
    expect(dashboardPortOf(70000)).toBeNull();
    expect(dashboardPortOf('5090')).toBeNull();
    expect(dashboardPortOf(5090)).toBe(5090);
  });
});
