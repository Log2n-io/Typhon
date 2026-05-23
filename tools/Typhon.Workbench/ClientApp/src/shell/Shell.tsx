import { useEffect } from 'react';
import { useKeyboardShortcuts } from '@/hooks/useKeyboardShortcuts';
import { useSelectionBootstrap } from '@/hooks/useSelectionBootstrap';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDensityStore } from '@/stores/useDensityStore';
import ContextBar from './ContextBar';
import DockHost from './DockHost';
import MenuBar from './MenuBar';
import StatusBar from './StatusBar';
import WelcomeScreen from './WelcomeScreen';

export default function Shell() {
  const kind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const density = useDensityStore((s) => s.mode);

  useKeyboardShortcuts();
  useSelectionBootstrap();

  // DS-1: reflect the active density onto the document root so token overrides + CSS apply app-wide.
  useEffect(() => {
    document.documentElement.dataset.density = density;
  }, [density]);

  return (
    <div className="flex h-full flex-col bg-background text-foreground">
      <MenuBar />
      {kind !== 'none' && <ContextBar />}
      <main className="min-h-0 flex-1">
        {kind === 'none' ? <WelcomeScreen /> : <DockHost key={sessionId ?? 'none'} />}
      </main>
      <StatusBar />
    </div>
  );
}
