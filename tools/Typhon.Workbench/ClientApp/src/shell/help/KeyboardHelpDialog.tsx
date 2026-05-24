import { useEffect } from 'react';
import { createPortal } from 'react-dom';

/**
 * Quick Doc → "Keyboard navigation" modal. A portaled, click-outside / Esc-dismissable overlay that documents
 * every app-wide keyboard affordance — the discoverable counterpart to the per-view "?" legends (the two
 * documentation channels mandated by platform-conventions PC-10). Same overlay shape as the Critical Path
 * legend: portaled to `document.body` so it escapes any dockview transform/overflow ancestor.
 */
export default function KeyboardHelpDialog({ onClose }: { onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return createPortal(
    <div
      className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/60"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label="Keyboard navigation"
    >
      <div
        className="max-h-[80vh] w-[640px] max-w-[90vw] overflow-auto rounded-lg border border-border bg-card p-5 text-[12px] text-foreground shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-[14px] font-semibold">Keyboard navigation</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="rounded px-2 py-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            ✕
          </button>
        </div>

        <Section title="Command & search">
          <Keys rows={[
            [['Ctrl', 'K'], 'Open the command palette'],
            [['Shift', 'Shift'], 'Open the command palette (double-tap Shift)'],
          ]} />
        </Section>

        <Section title="Panels & focus">
          <Keys rows={[
            [['F6'], 'Cycle focus to the next docked panel'],
            [['Shift', 'F6'], 'Cycle focus to the previous docked panel'],
            [['Esc'], 'Back out of a dialog, picker, or transient overlay'],
          ]} />
        </Section>

        <Section title="Navigation history">
          <Keys rows={[
            [['Alt', '←'], 'Go back through your navigation'],
            [['Alt', '→'], 'Go forward'],
            [['Mouse 3 / 4'], 'Back / forward (thumb buttons)'],
          ]} />
        </Section>

        <Section title="Jump to a view — focus chord">
          <p className="mb-2 text-muted-foreground">
            Press <Kbd>g</Kbd>, then the view’s key within ~1 second. While armed, the status bar shows
            “waiting for a second key chord”.
          </p>
          <Keys
            sep="then"
            rows={[
              [['g', 'c'], 'Component Inspector'],
              [['g', 'a'], 'Archetype Inspector'],
              [['g', 's'], 'Schema Explorer'],
              [['g', 'd'], 'Data Browser'],
              [['g', 'm'], 'Database File Map'],
            ]}
          />
        </Section>

        <Section title="Within the Component Inspector">
          <Keys rows={[
            [['['], 'Previous tab'],
            [[']'], 'Next tab'],
          ]} />
        </Section>

        <Section title="Appearance">
          <Keys rows={[
            [['Ctrl', '/'], 'Toggle the Resource Tree sidebar'],
            [['Alt', 'Shift', 'T'], 'Toggle light / dark theme'],
          ]} />
          <p className="mt-2 text-muted-foreground">
            Show/hide inline “?” help glyphs via <strong>Help → Toggle legend</strong>.
          </p>
        </Section>

        <p className="mt-4 border-t border-border pt-3 text-[11px] text-muted-foreground">
          Some views add their own keys while focused — see that view’s “?” legend (visible when legends are on).
        </p>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mb-4">
      <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      <div className="leading-snug">{children}</div>
    </div>
  );
}

function Kbd({ children }: { children: React.ReactNode }) {
  return <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px]">{children}</kbd>;
}

function Keys({ rows, sep = '+' }: { rows: Array<[string[], string]>; sep?: string }) {
  return (
    <table className="w-full">
      <tbody>
        {rows.map(([keys, desc]) => (
          <tr key={keys.join('+') + desc} className="align-top">
            <td className="w-44 py-0.5 pr-3">
              <span className="inline-flex flex-wrap items-center gap-1">
                {keys.map((k, i) => (
                  // composite key: a combo can repeat a key (e.g. Shift Shift), so the value alone isn't unique
                  <span key={`${k}-${i}`} className="inline-flex items-center gap-1">
                    {i > 0 && <span className="text-muted-foreground">{sep}</span>}
                    <Kbd>{k}</Kbd>
                  </span>
                ))}
              </span>
            </td>
            <td className="py-0.5 text-muted-foreground">{desc}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
