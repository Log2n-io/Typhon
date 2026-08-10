import Shell from '@/shell/Shell';
import ThemeProvider from '@/shell/ThemeProvider';
import { useInitialDbAutoOpen } from '@/hooks/useInitialDbAutoOpen';
import { useInitialTraceAutoOpen } from '@/hooks/useInitialTraceAutoOpen';
import { useInitialIntegrityOpen } from '@/hooks/useInitialIntegrityOpen';

export default function App() {
  // `typhon ui <db>` auto-opens the given database on first load (#429). No-op otherwise.
  useInitialDbAutoOpen();
  // `typhon ui --trace <path>` / `--open-latest` auto-opens the given profiler trace (#543). No-op otherwise.
  useInitialTraceAutoOpen();
  // `#integrity=<bundle>` opens the Integrity view WITHOUT opening a session (#729). No-op otherwise.
  useInitialIntegrityOpen();

  return (
    <ThemeProvider>
      <Shell />
    </ThemeProvider>
  );
}
