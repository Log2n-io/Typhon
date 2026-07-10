// Typhon brand config + behaviours for the DocFX "modern" template.
//
// 1) defaultTheme: 'dark' — match the typhondb.io apex (dark-only). First-visit
//    default only; the navbar switcher still works and remembers the choice.
//
// 2) Diagram/image lightbox — Typhon's architecture diagrams are LARGE SVGs, and a
//    plain click just navigates to the raw file (no zoom, no pan). This opens them in
//    an in-page overlay with true VECTOR pan/zoom via svg-pan-zoom, which drives the
//    SVG's own viewBox/matrix — so it stays crisp at any zoom. (CSS-transform scaling,
//    which every generic lightbox/panzoom uses, rasterises the SVG and goes blurry —
//    unacceptable for diagrams where you zoom in to read small labels.) Raster images
//    get a plain bitmap zoom (they can't be crisper than their source anyway).

const SPZ_URL = new URL('vendor/svg-pan-zoom.min.js', import.meta.url).href;

let _spzLoading = null;
function loadSvgPanZoom() {
  if (window.svgPanZoom) { return Promise.resolve(window.svgPanZoom); }
  if (_spzLoading) { return _spzLoading; }
  _spzLoading = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = SPZ_URL;
    s.onload = () => resolve(window.svgPanZoom);
    s.onerror = () => reject(new Error('svg-pan-zoom failed to load'));
    document.head.appendChild(s);
  });
  return _spzLoading;
}

let overlay, stage, origLink, pz, refit;

function buildOverlay() {
  overlay = document.createElement('div');
  overlay.className = 'tphn-lb';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');
  overlay.innerHTML =
    '<div class="tphn-lb-bar">' +
      '<span class="tphn-lb-hint">scroll = zoom · drag = pan · Esc = close</span>' +
      '<a class="tphn-lb-orig" target="_blank" rel="noopener">open original ↗</a>' +
      '<button class="tphn-lb-x" type="button" aria-label="Close">×</button>' +
    '</div>' +
    '<div class="tphn-lb-stage"></div>';
  stage = overlay.querySelector('.tphn-lb-stage');
  origLink = overlay.querySelector('.tphn-lb-orig');
  overlay.querySelector('.tphn-lb-x').addEventListener('click', closeLb);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) { closeLb(); } });
  document.body.appendChild(overlay);
}

function onKey(e) { if (e.key === 'Escape') { closeLb(); } }

function openLb(url) {
  if (!overlay) { buildOverlay(); }
  origLink.href = url;
  stage.innerHTML = '';
  overlay.classList.add('is-open');
  document.body.classList.add('tphn-lb-lock');
  document.addEventListener('keydown', onKey);
}

function closeLb() {
  if (pz) { try { pz.destroy(); } catch (_) { /* ignore */ } pz = null; }
  if (refit) { window.removeEventListener('resize', refit); refit = null; }
  if (overlay) { overlay.classList.remove('is-open'); }
  document.body.classList.remove('tphn-lb-lock');
  if (stage) { stage.innerHTML = ''; }
  document.removeEventListener('keydown', onKey);
}

async function openSvg(url) {
  openLb(url);
  stage.innerHTML = '<div class="tphn-lb-msg">Loading…</div>';
  let svgText;
  try {
    const res = await fetch(url);
    if (!res.ok) { throw new Error(String(res.status)); }
    svgText = await res.text();
  } catch (_) {
    window.open(url, '_blank', 'noopener'); closeLb(); return;   // fetch failed → just open the file
  }
  const holder = document.createElement('div');
  holder.innerHTML = svgText.trim();
  const svg = holder.querySelector('svg');
  if (!svg) { window.open(url, '_blank', 'noopener'); closeLb(); return; }
  // Hand sizing to svg-pan-zoom: fill the stage, keep the viewBox for a crisp fit.
  svg.removeAttribute('width');
  svg.removeAttribute('height');
  stage.innerHTML = '';
  stage.appendChild(svg);
  try {
    const spz = await loadSvgPanZoom();
    pz = spz(svg, {
      zoomEnabled: true, controlIconsEnabled: true, fit: true, center: true,
      minZoom: 0.2, maxZoom: 50, zoomScaleSensitivity: 0.35,
    });
    refit = () => { if (pz) { pz.resize(); pz.fit(); pz.center(); } };
    window.addEventListener('resize', refit);
  } catch (_) { /* lib failed to load — the static SVG still shows */ }
}

function openRaster(url) {
  openLb(url);
  const img = document.createElement('img');
  img.className = 'tphn-lb-img';
  img.src = url;
  img.alt = '';
  stage.appendChild(img);
  // Minimal bitmap pan/zoom (CSS transform — raster can't stay crisp regardless).
  let scale = 1, tx = 0, ty = 0, dragging = false, ox = 0, oy = 0;
  const apply = () => { img.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + scale + ')'; };
  stage.addEventListener('wheel', (e) => {
    e.preventDefault();
    scale = Math.min(20, Math.max(0.2, scale * (e.deltaY < 0 ? 1.15 : 1 / 1.15)));
    apply();
  }, { passive: false });
  img.addEventListener('mousedown', (e) => { dragging = true; ox = e.clientX - tx; oy = e.clientY - ty; e.preventDefault(); });
  window.addEventListener('mousemove', (e) => { if (dragging) { tx = e.clientX - ox; ty = e.clientY - oy; apply(); } });
  window.addEventListener('mouseup', () => { dragging = false; });
}

function isSvg(url) { return /\.svg(\?|#|$)/i.test(url); }

function onDocClick(e) {
  // Diagram thumbnails authored as <a href="...svg"><img></a> in the article content.
  const link = e.target.closest('article a[href], .content a[href]');
  if (link && isSvg(link.getAttribute('href') || '')) {
    e.preventDefault(); openSvg(link.href); return;
  }
  // Standalone content images not wrapped in a link.
  const img = e.target.closest('article img, .content img');
  if (img && !e.target.closest('a')) {
    const src = img.currentSrc || img.src;
    e.preventDefault();
    if (isSvg(src)) { openSvg(src); } else { openRaster(src); }
  }
}

let installed = false;
function installLightbox() {
  if (installed || typeof document === 'undefined') { return; }
  installed = true;
  document.addEventListener('click', onDocClick);
}

// Install on load. Use the template's start() hook AND a DOM-ready fallback so a
// template-version change to the hook can't silently disable the lightbox.
if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', installLightbox);
  } else {
    installLightbox();
  }
}

export default {
  defaultTheme: 'dark',
  start: () => { installLightbox(); },
};
