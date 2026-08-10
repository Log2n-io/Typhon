import { ArrowLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useIntegrityStore } from '@/stores/useIntegrityStore';
import IntegrityPanel from './IntegrityPanel';

/**
 * The Integrity view rendered full-bleed in the no-session shell, with a way back to the Welcome screen.
 *
 * The established pattern for a panel that must be reachable before a session exists is a modal fallback
 * (`DevFixtureModal`). This deviates from it, on purpose. Dev Fixture is a short form; the Integrity view
 * ends in a consent list that can run to thousands of rows, and a dialog that scrolls internally trains the
 * eye to reach past its content for the button beneath. Consent that was scrolled past is not consent. The
 * view therefore takes the full main area — the same real estate it gets when docked — rather than being
 * squeezed into a container whose shape works against the one interaction that matters most here.
 */
export default function IntegrityStandalone() {
  const closeStandalone = useIntegrityStore((s) => s.closeStandalone);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background" data-testid="integrity-standalone">
      <div className="flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
        <Button
          variant="ghost"
          onClick={closeStandalone}
          data-testid="integrity-standalone-back"
          className="h-6 gap-1 px-1.5 text-fs-sm"
        >
          <ArrowLeft className="h-3 w-3" />
          Back
        </Button>
        <span className="text-fs-base font-medium text-foreground">Database integrity</span>
        <span className="text-fs-sm text-muted-foreground">
          No database needs to be open — this reads the file directly.
        </span>
      </div>
      <div className="min-h-0 flex-1">
        <IntegrityPanel />
      </div>
    </div>
  );
}
