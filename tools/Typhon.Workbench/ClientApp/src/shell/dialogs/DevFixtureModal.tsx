import { useEffect } from 'react';
import type { IDockviewPanelProps } from 'dockview';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { useDevFixtureModalStore } from '@/stores/useDevFixtureModalStore';
import { useSessionStore } from '@/stores/useSessionStore';
import DevFixturePanel from '@/panels/DevFixture/DevFixturePanel';

/**
 * Modal fallback that hosts the {@link DevFixturePanel} content when no dockview is mounted — i.e. on the
 * empty-workspace / Welcome screen. Without this, clicking "Dev Fixture" anywhere on Welcome silently no-ops
 * (the dock-panel toggle checks <c>registeredApi</c>, which is null until a session opens).
 *
 * <p>Auto-close on session change: when a fixture finishes generating and auto-opens, the panel's
 * <c>handleOpenGenerated</c> calls <c>setSession(dto)</c> which flips <c>sessionKind</c> to <c>'open'</c>.
 * That's the natural cue to close the modal — the user got what they wanted, the dock host is about to
 * take over the main pane. The same effect handles the user clicking the X to dismiss manually.</p>
 *
 * <p>This component is mounted at the Shell level (above both Welcome and DockHost) so it's available in
 * either state. While a session is open the dock-panel registration handles "Dev Fixture" toggles directly
 * (the modal stays closed); on Welcome the modal handles them.</p>
 */
export default function DevFixtureModal(): React.JSX.Element {
  const isOpen = useDevFixtureModalStore((s) => s.isOpen);
  const close = useDevFixtureModalStore((s) => s.close);
  const sessionKind = useSessionStore((s) => s.kind);

  // Auto-close when a session opens — happens after a successful generate + auto-open, signaling the
  // user's task is complete and the dock view is about to render.
  useEffect(() => {
    if (sessionKind !== 'none' && isOpen) {
      close();
    }
  }, [sessionKind, isOpen, close]);

  // DevFixturePanel was designed for the dockview host (`IDockviewPanelProps`), but its body doesn't read
  // any of the panel props — we pass an empty stub to satisfy the type. If the panel ever starts using its
  // params, this stub will need a real value (or the body should be extracted into a shared inner component).
  const stubProps = {} as IDockviewPanelProps;

  return (
    <Dialog open={isOpen} onOpenChange={(open) => { if (!open) close(); }}>
      <DialogContent
        className="max-h-[90vh] max-w-3xl overflow-hidden p-0"
        data-testid="devfixture-modal"
      >
        <DialogHeader className="px-4 pt-4">
          <DialogTitle>Dev Fixture</DialogTitle>
          <DialogDescription className="text-fs-base">
            Generate (or reuse) a populated test database, then open it. DEBUG-only.
          </DialogDescription>
        </DialogHeader>
        <div className="max-h-[75vh] overflow-auto">
          <DevFixturePanel {...stubProps} />
        </div>
      </DialogContent>
    </Dialog>
  );
}
