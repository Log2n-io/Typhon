import type { ReactNode } from 'react';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

export type StatusTone = 'info' | 'success' | 'warn' | 'error' | 'neutral';

// All tones use a solid-colored background + darker matching border, with text forced to a
// maximum-contrast value (slate-900 on light tones, foreground on the neutral tone). Matches the
// Logs panel level chips so every colored badge across the Workbench reads the same way.
const TONE_CLASSES: Record<StatusTone, string> = {
  info: 'bg-sky-400 text-slate-900 border-sky-500',
  success: 'bg-emerald-400 text-slate-900 border-emerald-500',
  warn: 'bg-amber-400 text-slate-900 border-amber-500',
  error: 'bg-red-400 text-slate-900 border-red-500',
  neutral: 'bg-muted text-foreground border-border',
};

interface StatusBadgeProps {
  tone: StatusTone;
  children: ReactNode;
  className?: string;
  title?: string;
  onClick?: () => void;
  // When false, renders at ~40% opacity to signal a toggle-off state (used by filter chips).
  muted?: boolean;
}

/**
 * Color-coded badge with consistent contrast rules. Prefer this over raw <Badge> + ad-hoc Tailwind
 * classes for any chip that conveys status (severity, storage mode, runtime flags, etc.).
 */
export function StatusBadge({ tone, children, className, title, onClick, muted = false }: StatusBadgeProps) {
  return (
    <Badge
      variant="outline"
      title={title}
      onClick={onClick}
      className={cn(
        'px-2 py-0 text-fs-xs font-medium',
        TONE_CLASSES[tone],
        muted && 'opacity-40',
        onClick && 'cursor-pointer select-none',
        className,
      )}
    >
      {children}
    </Badge>
  );
}
