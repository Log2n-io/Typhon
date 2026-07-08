import Shell from '@/shell/Shell';
import ThemeProvider from '@/shell/ThemeProvider';
import { useInitialDbAutoOpen } from '@/hooks/useInitialDbAutoOpen';

export default function App() {
  // `typhon ui <db>` auto-opens the given database on first load (#429). No-op otherwise.
  useInitialDbAutoOpen();

  return (
    <ThemeProvider>
      <Shell />
    </ThemeProvider>
  );
}
