import { ArrowLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useBackupsStore } from '@/stores/useBackupsStore';
import BackupsPanel from './BackupsPanel';

/**
 * The Backups view full-bleed in the no-session shell, with a way back to the Welcome screen — the Integrity view's shape, for the same reason: the
 * restore wizard and a verify's findings can run long, and a scrolling dialog is the wrong container for them.
 */
export default function BackupsStandalone() {
  const closeStandalone = useBackupsStore((s) => s.closeStandalone);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background" data-testid="backups-standalone">
      <div className="flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
        <Button
          variant="ghost"
          onClick={closeStandalone}
          data-testid="backups-standalone-back"
          className="h-6 gap-1 px-1.5 text-fs-sm"
        >
          <ArrowLeft className="h-3 w-3" />
          Back
        </Button>
        <span className="text-fs-base font-medium text-foreground">Backups</span>
        <span className="text-fs-sm text-muted-foreground">
          No database needs to be open — a store is read directly.
        </span>
      </div>
      <div className="min-h-0 flex-1">
        <BackupsPanel />
      </div>
    </div>
  );
}
