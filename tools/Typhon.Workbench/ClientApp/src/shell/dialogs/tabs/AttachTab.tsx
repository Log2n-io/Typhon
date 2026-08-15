import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

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
 */
export default function AttachTab({ onAttach, isAttaching }: Props) {
  const [endpoint, setEndpoint] = useState<string>('localhost:9100');
  // #805 — chosen per attach, deliberately not remembered: "show me everything this run does" and "let me grab the 100
  // ticks around that spike" are both legitimate on the same engine minutes apart, so a sticky default would be wrong
  // half the time. Defaults to capture-everything, which is the behaviour every attach had before #805.
  const [cherryPick, setCherryPick] = useState<boolean>(false);

  const trimmed = endpoint.trim();
  const shapeValid = ENDPOINT_REGEX.test(trimmed);
  const portPart = trimmed.split(':')[1];
  const portValid = portPart === undefined || (Number(portPart) >= 1 && Number(portPart) <= 65535);
  const canAttach = shapeValid && portValid && !isAttaching;

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
              Records every tick for as long as the session is attached.
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
        <Button
          onClick={() => canAttach && onAttach(trimmed, cherryPick)}
          disabled={!canAttach}
          className="text-fs-lg"
        >
          {isAttaching ? 'Attaching…' : 'Attach'}
        </Button>
      </div>
    </div>
  );
}
