import { useKeyboardShortcuts } from '@/hooks/useKeyboardShortcuts';
import { useSessionStore } from '@/stores/useSessionStore';
import DockHost from './DockHost';
import MenuBar from './MenuBar';
import StatusBar from './StatusBar';
import WelcomeScreen from './WelcomeScreen';

export default function Shell() {
  const kind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);

  useKeyboardShortcuts();

  return (
    <div className="flex h-full flex-col bg-background text-foreground">
      <MenuBar />
      <main className="min-h-0 flex-1">
        {kind === 'none' ? <WelcomeScreen /> : <DockHost key={sessionId ?? 'none'} />}
      </main>
      <StatusBar />
    </div>
  );
}
