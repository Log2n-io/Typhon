import { useState } from 'react';
import { Circle, Square, TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';
import { logError, logInfo } from '@/stores/useLogStore';
import { useProfilerSessionStore, type CaptureState } from '@/stores/useProfilerSessionStore';

interface Props {
  sessionId: string | null;
}

const DEFAULT_TICKS = 100;

/**
 * On-demand tick capture control (#805) — Record / Stop plus the tick budget, for live attach sessions.
 *
 * The unit is ticks rather than milliseconds on purpose: the timeline draws one bar per tick, so "100 ticks" is
 * exactly 100 bars you can select, whereas a duration buys an unpredictable number of them — Typhon throttles its
 * tick rate under overload, which is precisely when someone is recording.
 *
 * Only rendered for cherry-pick sessions: in capture-everything mode there is no window to arm, and offering a
 * disabled Record button would imply the mode can be changed after attaching, which it cannot.
 */
export default function CaptureControl({ sessionId }: Props) {
  const captureState = useProfilerSessionStore((s) => s.captureState);
  const [tickCount, setTickCount] = useState<string>(String(DEFAULT_TICKS));
  const [busy, setBusy] = useState(false);

  if (!sessionId || !captureState || captureState.mode !== 'CherryPick') {
    return null;
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
    </div>
  );
}

/** Progress / status pill: "recording 37/100", "idle", or a numbering warning. */
function CaptureBadge({ state }: { state: CaptureState }) {
  if (state.tickNumberingSuspect) {
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
