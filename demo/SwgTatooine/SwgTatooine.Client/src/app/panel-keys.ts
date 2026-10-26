import { isTyping } from '../camera/camera-input';

/** Which panel a key press toggles, or `null` for one that is not a panel key — typed into a field, or with a modifier. */
export function panelKey(e: KeyboardEvent): 'journal' | 'inventory' | null {
  if (e.repeat || e.isComposing || e.ctrlKey || e.altKey || e.metaKey || isTyping(e)) {
    return null;
  }

  switch (e.key.toLowerCase()) {
    case 'j':
      return 'journal';
    case 'i':
      return 'inventory';
    default:
      return null;
  }
}
