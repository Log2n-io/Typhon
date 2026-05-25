// @vitest-environment jsdom
import { describe, it, expect } from 'vitest';
import { FetchError } from '@/api/client';
import { extractDetail } from '../connectErrors';

describe('extractDetail', () => {
  it('reads the ProblemDetails detail off a FetchError (the prior gap)', () => {
    const err = new FetchError(400, {
      title: 'unsupported_trace_version',
      detail: 'Unsupported trace file version: 9. This build reads versions 11..11. Re-record against a current build.',
      status: 400,
    });
    expect(extractDetail(err)).toContain('Re-record against a current build');
  });

  it('falls back to the FetchError title when there is no detail', () => {
    const err = new FetchError(409, { title: 'session_kind_mismatch', status: 409 });
    expect(extractDetail(err)).toBe('session_kind_mismatch');
  });

  it('reads a plain { detail } object', () => {
    expect(extractDetail({ detail: 'plain reason' })).toBe('plain reason');
  });

  it('falls back to a bare Error message', () => {
    expect(extractDetail(new Error('boom'))).toBe('boom');
  });

  it('returns empty for null / non-objects', () => {
    expect(extractDetail(null)).toBe('');
    expect(extractDetail(undefined)).toBe('');
    expect(extractDetail('nope')).toBe('');
  });
});
