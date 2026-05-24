/**
 * True when the focused element is a text input, textarea, or contenteditable — used to guard plain-letter
 * shortcuts (chord leaders, panel hotkeys) from firing while the user is typing. Modifier-key shortcuts
 * (Ctrl+K, Alt+Shift+T, …) don't need this check; they don't collide with typing.
 */
export function isTypingInText(): boolean {
  const el = document.activeElement;
  if (!el) {
    return false;
  }
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
    return true;
  }
  if (el instanceof HTMLElement && el.isContentEditable) {
    return true;
  }
  return false;
}
