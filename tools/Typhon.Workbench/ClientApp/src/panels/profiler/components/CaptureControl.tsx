import { useState } from 'react';
import { Circle, Save, Square, TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';
import { openSaveReplayDialog } from '@/shell/commands/profilerCommands';
import { logError, logInfo } from '@/stores/useLogStore';
import { useProfilerSessionStore, type CaptureState } from '@/stores/useProfilerSessionStore';

interface Props {
  sessionId: string | null;
}

const DEFAULT_TICKS = 100;

/** Share of the cache limit at which the operator is warned, the design's "approaching". */
const CACHE_WARNING_RATIO = 0.8;

const CAP_REASONS: Record<string, string> = {
  size: 'the session cache reached its size limit',
  disk_low: 'the disk is nearly full',
  write_failed: 'writing to the session cache failed',
};

const formatGiB = (bytes: number) => `${(bytes / 1024 ** 3).toFixed(1)} GiB`;

const cacheIsFilling = (state: CaptureState) =>
  state.cacheBytesLimit > 0 && state.cacheBytes >= state.cacheBytesLimit * CACHE_WARNING_RATIO;

/**
 * On-demand tick capture control (#805) — Record / Stop plus the tick budget, for live attach sessions.
 *
 * The unit is ticks rather than milliseconds on purpose: the timeline draws one bar per tick, so "100 ticks" is
 * exactly 100 bars you can select, whereas a duration buys an unpredictable number of them — Typhon throttles its
 * tick rate under overload, which is precisely when someone is recording.
 *
 * Only rendered for cherry-pick sessions: in capture-everything mode there is no window to arm, and offering a
 * disabled Record button would imply the mode can be changed after attaching, which it cannot. The cache warning and the
 * capped banner (#822) show in both modes: a session that silently stopped retaining is the failure they exist to prevent.
 */
export default function CaptureControl({ sessionId }: Props) {
  const captureState = useProfilerSessionStore((s) => s.captureState);
  const [tickCount, setTickCount] = useState<string>(String(DEFAULT_TICKS));
  const [busy, setBusy] = useState(false);

  if (!sessionId || !captureState) {
    return null;
  }

  if (captureState.state === 'Capped') {
    return <CappedBanner state={captureState} />;
  }

  if (captureState.mode !== 'CherryPick') {
    return cacheIsFilling(captureState) ? <CacheWarning state={captureState} /> : null;
  }

  const parsed = Number(tickCount);
  const countValid = Number.isInteger(parsed) && parsed > 0 && parsed <= 1_000_000;
  const recording = captureState.state === 'Recording';

  const send = async (ticks: number) => {
    setBusy(true);
    try {
      const headers = applyWorkbenchAuthHeaders(new Headers({ 'Content-Type': 'application/json' }), sessionId);
      const resp = await fetch(`/api/sessions/${sessionId}/profiler/capture`, {
        method: 'POST',
        headers,
        body: JSON.stringify({ tickCount: ticks }),
      });
      if (!resp.ok) {
        throw new Error(`capture request failed: ${resp.status}`);
      }
      logInfo(ticks > 0 ? `Recording next ${ticks} tick(s)` : 'Capture stopped', { sessionId, ticks });
    } catch (err) {
      logError('Capture control failed', { sessionId, ticks, error: String(err) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex items-center gap-2" data-testid="capture-control">
      {recording ? (
        <Button
          size="sm"
          variant="destructive"
          disabled={busy}
          onClick={() => void send(0)}
          data-testid="capture-stop"
        >
          <Square className="mr-1 size-3" />
          Stop
        </Button>
      ) : (
        <>
          <Input
            value={tickCount}
            onChange={(e) => setTickCount(e.target.value)}
            className="h-7 w-20 text-fs-sm"
            aria-label="Ticks to record"
            data-testid="capture-tick-count"
          />
          <Button
            size="sm"
            disabled={busy || !countValid}
            onClick={() => void send(parsed)}
            data-testid="capture-record"
          >
            <Circle className="mr-1 size-3 fill-current" />
            Record
          </Button>
        </>
      )}

      <CaptureBadge state={captureState} />
      {cacheIsFilling(captureState) && <CacheWarning state={captureState} />}
    </div>
  );
}

/** Persistent and not dismissable: from here on the timeline silently stops growing, and only this says why. */
function CappedBanner({ state }: { state: CaptureState }) {
  const reason = CAP_REASONS[state.capReason ?? ''] ?? state.capReason;
  return (
    <div
      className="flex items-center gap-2 rounded bg-destructive/10 px-2 py-0.5 text-fs-xs text-destructive"
      data-testid="capture-capped"
    >
      <TriangleAlert className="size-3" />
      <span>
        Recording stopped after tick {state.capTick}: {reason} ({formatGiB(state.cacheBytes)}). Save to keep what this session
        holds, and re-attach to record more.
      </span>
      {state.tickNumberingSuspect && <NumberingWarning />}
      <Button size="sm" variant="outline" onClick={openSaveReplayDialog} data-testid="capture-capped-save">
        <Save className="mr-1 size-3" />
        Save
      </Button>
    </div>
  );
}

function CacheWarning({ state }: { state: CaptureState }) {
  return (
    <span
      className="flex items-center gap-1 rounded bg-destructive/10 px-2 py-0.5 text-fs-xs text-destructive"
      title="Past the limit the session stops retaining ticks. It stays connected, and what it holds can still be saved."
      data-testid="capture-cache-warning"
    >
      <TriangleAlert className="size-3" />
      cache {formatGiB(state.cacheBytes)} of {formatGiB(state.cacheBytesLimit)}
    </span>
  );
}

function NumberingWarning() {
  return (
    <span
      className="flex items-center gap-1 rounded bg-destructive/10 px-2 py-0.5 text-fs-xs text-destructive"
      title="A TickStart record was lost, so reported tick numbers no longer match the engine's. Re-attach to recover."
      data-testid="capture-numbering-warning"
    >
      <TriangleAlert className="size-3" />
      tick numbers unreliable
    </span>
  );
}

/** Progress / status pill: "recording 37/100", "idle", or a numbering warning. */
function CaptureBadge({ state }: { state: CaptureState }) {
  if (state.tickNumberingSuspect) {
    return <NumberingWarning />;
  }

  if (state.state === 'Recording') {
    const captured = Math.max(state.recordedTicks, 0);
    return (
      <span className="rounded bg-primary/10 px-2 py-0.5 text-fs-xs text-primary" data-testid="capture-progress">
        recording — {state.remaining} tick{state.remaining === 1 ? '' : 's'} left ({captured} captured)
      </span>
    );
  }

  return (
    <span className="rounded bg-muted px-2 py-0.5 text-fs-xs text-muted-foreground" data-testid="capture-idle">
      {state.recordedTicks > 0 ? `idle — ${state.recordedTicks} tick(s) captured` : 'idle — not recording'}
      {!state.tickNumbersAbsolute && ' · ticks relative to attach'}
    </span>
  );
}
