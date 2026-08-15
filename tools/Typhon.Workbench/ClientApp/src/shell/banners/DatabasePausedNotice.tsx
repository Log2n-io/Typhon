import { PauseCircle } from 'lucide-react';

interface DatabasePausedNoticeProps {
  /**
   * What comes back when the database does, as a sentence subject — "Entities", "The file map", "Query results".
   * Panel-specific on purpose: "this content" reads as boilerplate the eye skips, and the panel is the only thing
   * that knows what the user was looking at when the database went away.
   *
   * The sentence is built with "will be available again" rather than a verb that has to agree, so a singular
   * subject ("The file map") and a plural one ("Entities") both read correctly without the caller thinking about it.
   */
  subject: string;
  /** Overrides the `data-testid`, so a panel's paused branch can be asserted distinctly from its neighbours'. */
  testId?: string;
}

/**
 * The in-panel counterpart to {@link PausedBanner} (#621, design §9.6).
 *
 * <p><b>Why panels need their own notice at all</b>, when a banner already explains the state globally: every
 * database-backed request 409s while paused, and TanStack surfaces that as `isError`. Rendered through a panel's
 * ordinary error branch it reads "Failed to load …" — which is a lie (nothing failed) and, worse, actionable advice
 * pointing the wrong way: it teaches the user to close and reopen the session, the exact dance pausing exists to
 * remove. The banner cannot fix that, because the banner is not what the user is looking at when they wonder why the
 * grid is empty.</p>
 *
 * <p><b>Usage.</b> Branch on {@link useDatabasePaused} *before* the error branch and suppress the error there —
 * the 409 is the paused state's symptom, not an independent failure:</p>
 *
 * <pre>
 * {databasePaused ? &lt;DatabasePausedNotice subject="Entities" /&gt; : isError &amp;&amp; &lt;p&gt;Failed to load entities.&lt;/p&gt;}
 * </pre>
 *
 * <p>Deliberately quiet — muted, no icon colour, no action button. Nothing is wrong and there is nothing to do; the
 * session repairs itself when the other process exits. An alarming empty state would misrepresent a normal handoff.</p>
 *
 * <p><b>It carries its own background</b> because it cannot assume it has one. Most panels render it into empty flow,
 * but the Db Map overlays it on a painted canvas (`DbMapPanel.tsx`, `absolute left-2 top-2`) — where bare muted text
 * over segment fills was simply unreadable. `bg-muted/85` plus a backdrop blur keeps it legible on any substrate while
 * staying quiet; opaque would read as an alert, and a colour would claim something is wrong.</p>
 */
export default function DatabasePausedNotice({ subject, testId = 'database-paused' }: DatabasePausedNoticeProps) {
  return (
    <div
      role="status"
      data-testid={testId}
      className="flex items-start gap-2 rounded-md border border-border/50 bg-muted/85 p-3 text-fs-base text-muted-foreground shadow-sm backdrop-blur-sm"
    >
      <PauseCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <p>{subject} will be available again when the other process using this database exits.</p>
    </div>
  );
}
