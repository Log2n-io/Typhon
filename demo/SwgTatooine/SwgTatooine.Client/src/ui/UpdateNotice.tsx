import { useBuild } from '../state/build-store';

/** The game was updated under this page (client.md C41): what it loads next is gone from the server, so it asks to be reloaded. */
export function UpdateNotice() {
  const stale = useBuild((s) => s.stale);
  if (!stale) {
    return null;
  }

  return (
    <div className="panel update-notice" role="alert">
      <span>The game was updated while this page was open: reload to get the new version.</span>
      <button
        type="button"
        onClick={() => {
          window.location.reload();
        }}
      >
        Reload
      </button>
    </div>
  );
}
