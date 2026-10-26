import { useState } from 'react';
import { Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { isLoopbackEndpoint, useAttachEndpointsStore } from '@/stores/useAttachEndpointsStore';

interface Props {
  onAttach: (endpoint: string, cherryPick: boolean) => void;
  isAttaching?: boolean;
}

// host:port — basic shape check only; the server validates hostname resolution + port range.
const ENDPOINT_REGEX = /^[^\s:]+(?::\d{1,5})?$/;

/**
 * Attach-mode dialog tab — endpoint input (host:port), validation, Attach button. The actual TCP connect happens
 * server-side in `AttachSessionRuntime.StartAsync`; it retries 3 × 2 s before returning 503. ConnectDialog surfaces
 * that error via a per-tab pill (same pattern as OpenTraceTab).
 *
 * W3 adds the two halves that made a remote attach practical rather than possible:
 *
 * - <b>Saved endpoints.</b> The field used to default to `localhost:9100` and remember nothing, so every attach to a
 *   deployed box meant retyping a host. They persist in `useAttachEndpointsStore` and can be named, because the useful
 *   label for a box is "tatooine demo" rather than its hostname.
 * - <b>A non-loopback badge.</b> `TcpExporter` has no authentication and no transport security and its stream is the
 *   engine's internals, so whether the bytes stay on this machine is worth stating rather than leaving to be inferred
 *   from the host. Loopback is the ordinary case AND the deployed case — `scripts/attach-remote.ps1` forwards a
 *   deployed port to 127.0.0.1 over SSM — which is exactly why the badge says which of the two this is.
 */
/** The host to name in the warning. Re-exported through a local helper so the JSX reads as prose. */
function hostLabel(endpoint: string): string {
  const colon = endpoint.lastIndexOf(':');
  return endpoint.startsWith('[') || colon <= 0 ? endpoint : endpoint.slice(0, colon);
}

export default function AttachTab({ onAttach, isAttaching }: Props) {
  const [endpoint, setEndpoint] = useState<string>('localhost:9100');
  // #805 — chosen per attach, deliberately not remembered: "show me everything this run does" and "let me grab the 100
  // ticks around that spike" are both legitimate on the same engine minutes apart, so a sticky default would be wrong
  // half the time. Defaults to capture-everything, which is the behaviour every attach had before #805.
  const [cherryPick, setCherryPick] = useState<boolean>(false);
  const saved = useAttachEndpointsStore((st) => st.entries);
  const removeSaved = useAttachEndpointsStore((st) => st.remove);

  const trimmed = endpoint.trim();
  const shapeValid = ENDPOINT_REGEX.test(trimmed);
  const portPart = trimmed.split(':')[1];
  const portValid = portPart === undefined || (Number(portPart) >= 1 && Number(portPart) <= 65535);
  const canAttach = shapeValid && portValid && !isAttaching;
  // Only asked of an endpoint that is shaped like one: `hostOf` on half-typed input would badge a prefix.
  const remote = shapeValid && portValid && trimmed !== '' && !isLoopbackEndpoint(trimmed);

  return (
    <div className="flex h-full flex-col gap-3">
      <div className="flex shrink-0 flex-col gap-1">
        <label className="text-fs-lg text-muted-foreground">Engine endpoint</label>
        <Input
          placeholder="localhost:9100"
          value={endpoint}
          onChange={(e) => setEndpoint(e.target.value)}
          spellCheck={false}
          autoComplete="off"
          className="font-mono text-fs-base"
        />
      </div>

      <p className="shrink-0 text-fs-xs text-muted-foreground">
        Enter <code>host:port</code> of the target Typhon app's profiler TCP exporter (default port <code>9100</code>).
        The Workbench retries the connect for up to 6 seconds before giving up.
      </p>

      {saved.length > 0 && (
        <div className="flex shrink-0 flex-col gap-1" data-testid="attach-saved">
          <span className="text-fs-sm text-muted-foreground">Saved endpoints</span>
          <ul className="flex max-h-32 flex-col gap-0.5 overflow-y-auto">
            {saved.map((e) => (
              <li key={e.endpoint} className="flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => setEndpoint(e.endpoint)}
                  className="flex flex-1 items-baseline gap-2 rounded px-2 py-1 text-left hover:bg-accent"
                  data-testid={`attach-saved-${e.endpoint}`}
                >
                  <span className="font-mono text-fs-base">{e.endpoint}</span>
                  {e.label && <span className="text-fs-xs text-muted-foreground">{e.label}</span>}
                  {!isLoopbackEndpoint(e.endpoint) && (
                    <span className="ml-auto text-fs-xs text-amber-600 dark:text-amber-500">remote</span>
                  )}
                </button>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => removeSaved(e.endpoint)}
                  aria-label={`Forget ${e.endpoint}`}
                >
                  <Trash2 className="size-3.5" />
                </Button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {remote && (
        <p
          className="shrink-0 rounded border border-amber-500/50 bg-amber-500/10 px-2 py-1 text-fs-sm text-amber-700 dark:text-amber-400"
          data-testid="attach-remote-warning"
        >
          <span className="font-medium">Not loopback.</span> The trace stream will cross the network to{' '}
          <code>{hostLabel(trimmed)}</code> in clear, and the profiler exporter has no authentication — anything that
          can reach that port can read this engine&apos;s internals. For a deployed box, forward the port instead:{' '}
          <code>scripts/attach-remote.ps1 -InstanceId i-…</code>, then attach to <code>127.0.0.1</code>.
        </p>
      )}

      {trimmed && !shapeValid && (
        <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
          Invalid endpoint — expected <code>host</code> or <code>host:port</code>.
        </p>
      )}
      {trimmed && shapeValid && !portValid && (
        <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
          Port must be between 1 and 65535.
        </p>
      )}

      <fieldset className="flex shrink-0 flex-col gap-2 rounded border border-border p-3">
        <legend className="px-1 text-fs-sm text-muted-foreground">What to capture</legend>

        <label className="flex cursor-pointer items-start gap-2">
          <input
            type="radio"
            name="capture-mode"
            className="mt-1"
            checked={!cherryPick}
            onChange={() => setCherryPick(false)}
            data-testid="attach-mode-everything"
          />
          <span className="flex flex-col">
            <span className="text-fs-base">Capture everything</span>
            <span className="text-fs-xs text-muted-foreground">
              Records every tick for as long as the session is attached. A busy engine can outrun the browser: measured
              at 50 Hz with ~7,600 events a tick, decoding the stream saturates the main thread and the window stops
              answering. Prefer cherry-pick above a few thousand events per tick.
            </span>
          </span>
        </label>

        <label className="flex cursor-pointer items-start gap-2">
          <input
            type="radio"
            name="capture-mode"
            className="mt-1"
            checked={cherryPick}
            onChange={() => setCherryPick(true)}
            data-testid="attach-mode-cherry-pick"
          />
          <span className="flex flex-col">
            <span className="text-fs-base">Cherry-pick ticks</span>
            <span className="text-fs-xs text-muted-foreground">
              Keeps the tick timeline and gauges, but records full detail only for the ticks you ask for with
              <span className="font-medium"> Record</span>. Much smaller sessions.
            </span>
          </span>
        </label>
      </fieldset>

      <div className="flex-1" />

      <div className="flex shrink-0 justify-end gap-2">
        <Button onClick={() => canAttach && onAttach(trimmed, cherryPick)} disabled={!canAttach} className="text-fs-lg">
          {isAttaching ? 'Attaching…' : 'Attach'}
        </Button>
      </div>
    </div>
  );
}
