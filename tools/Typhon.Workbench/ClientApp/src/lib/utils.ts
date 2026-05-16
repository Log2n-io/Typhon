import { clsx, type ClassValue } from 'clsx';
import { extendTailwindMerge } from 'tailwind-merge';

// `text-density-sm` / `text-density-base` are custom Tailwind v4 @theme font-size tokens (--text-density-*).
// Stock tailwind-merge doesn't know they're sizes, so it lumps them into the text-color group and silently
// drops a colliding `text-{color}` class (e.g. a Button losing `text-primary-foreground`). Registering them
// in the font-size group makes them conflict only with other sizes, leaving text-color classes intact.
const twMerge = extendTailwindMerge({
  extend: { classGroups: { 'font-size': [{ text: ['density-sm', 'density-base'] }] } },
});

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
