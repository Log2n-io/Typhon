import { clsx, type ClassValue } from 'clsx';
import { extendTailwindMerge } from 'tailwind-merge';

// `text-fs-*` are custom Tailwind v4 font-size utilities — the DS-1 density ramp, backed by the runtime --fs-*
// vars (globals.css). Stock tailwind-merge doesn't know they're sizes, so it would lump them into the text-color
// group and silently drop a colliding `text-{color}` class (e.g. a Button losing `text-primary-foreground`).
// Registering them in the font-size group makes them conflict only with other sizes, leaving colors intact.
const twMerge = extendTailwindMerge({
  extend: { classGroups: { 'font-size': [{ text: ['fs-2xs', 'fs-xs', 'fs-sm', 'fs-base', 'fs-lg', 'fs-xl'] }] } },
});

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
