/**
 * Canonical byte-size formatter for the whole SPA. Uses **IEC binary units** (KiB/MiB/GiB/TiB) — the
 * sizes are computed as powers of 1024, so the labels must be the binary ones. Three panels previously
 * carried private copies; two of them divided by 1024 yet labelled the result `KB`/`MB` (decimal units),
 * so the same byte count rendered differently across the app. This is the single source of truth.
 *
 * Precision: integer bytes, one decimal at KiB, two decimals at MiB and above — enough to distinguish
 * close sizes without noise (e.g. `512 B`, `2.0 KiB`, `12.40 MiB`, `1.25 GiB`).
 */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KiB`;
  }
  if (bytes < 1024 * 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(2)} MiB`;
  }
  if (bytes < 1024 * 1024 * 1024 * 1024) {
    return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GiB`;
  }
  return `${(bytes / 1024 / 1024 / 1024 / 1024).toFixed(2)} TiB`;
}
