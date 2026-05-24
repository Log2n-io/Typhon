// Inversion seam between the nav-history store (stores/) and the dock command layer (shell/commands).
//
// View-transition restore (IA §3.2 / §5.3, conformance B.2) needs two things the dock layer owns: read
// which panel currently holds focus, and move focus into a panel by id. The nav-history store must not
// import the shell, so the shell *registers* concrete implementations on dock ready ({@link registerDockApi})
// and the store consumes them through this seam. Until registration (unit tests, pre-mount) the defaults
// are inert no-ops, so the store works headless.

export type ActivePanelReader = () => string | undefined;
export type PanelFocuser = (panelId: string) => void;

let readActivePanelId: ActivePanelReader = () => undefined;
let movePanelFocus: PanelFocuser = () => { /* inert until the dock layer registers */ };

/** Dock layer registers how to read/move panel focus. Pass `null`s to reset to inert defaults (on unmount). */
export function registerNavFocus(read: ActivePanelReader | null, focus: PanelFocuser | null): void {
  readActivePanelId = read ?? (() => undefined);
  movePanelFocus = focus ?? (() => { /* inert */ });
}

/** Id of the panel that currently holds focus, or `undefined` headless / pre-mount. */
export function currentActivePanelId(): string | undefined {
  return readActivePanelId();
}

/** Move DOM focus into a panel by id (surfacing it if collapsed). No-op headless / pre-mount / unknown id. */
export function focusPanelById(panelId: string): void {
  movePanelFocus(panelId);
}
