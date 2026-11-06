// The "Server health" system timeline (claude/scratch/swg-dashboard-system-timeline.md): ten minutes of per-system CPU per tick, one bar a second, drawn
// on a hand-written canvas (D4) — no chart library, no Blazor diff of 600 × 11 segments every two seconds. Blazor sends the seconds that changed
// (SystemTimeline.CopySeconds) and, on a click, one second's ticks (Home.Ticks → SystemTimeline.CopyTicks).
//
// Stacked: the top 10 systems by CPU over the window + "other" (D1: CPU, not wall — spans overlap on a parallel schedule). Overlaid: the mean tick wall
// time (line), the worst tick of each second (marks: a mean hides spikes), the budget (dashed). Under it a strip: the pool's utilisation, Σ CPU ÷ (wall ×
// workers). Click a bar: that second's ticks, and a table per system — CPU, wall, effective cores (work ÷ span), workers touched.
//
// Bars have a fixed pitch — PITCH px, the last one the separator — whatever the card's width: thinner bars cannot be read segment by segment. Ten minutes
// are 3 000 px, so the bars sit in a horizontal scroller with the y-axis pinned beside it; the view follows "now" unless scrolled back, and then stays on
// the same seconds while new ones arrive.
(() => {
    const SECONDS = 600;
    const TOP = 10;
    const MAIN_H = 280;
    const STRIP_H = 12;
    const DRILL_H = 220;
    const PAD = { l: 52, r: 10, t: 8, b: 20 };
    const PITCH = 5;
    // The bars' canvas: no axis of its own (it is the pinned one), room on the right for the "now" label.
    const BARS = { l: 4, r: 18 };
    // Ten colours for the top systems, one for "other": distinct on the dark card, none of them the budget's accent.
    const PALETTE = ['#4e9af1', '#6fcf8a', '#e8744f', '#b48cf2', '#4fd1c5', '#f26da8', '#c9b458', '#7f8cff', '#9ad35b', '#ff9f43'];
    const OTHER = '#5b6470';
    const hosts = new WeakMap();

    function el(tag, cls, parent) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (parent) parent.appendChild(e);
        return e;
    }

    function canvas(parent, height) {
        const c = el('canvas', null, parent);
        c.style.height = height + 'px';
        return c;
    }

    /** The canvas's 2D context at the device's pixel ratio, its CSS size in w/h. */
    function context(c) {
        const ratio = window.devicePixelRatio || 1;
        const w = c.clientWidth, h = c.clientHeight;
        if (c.width !== Math.round(w * ratio) || c.height !== Math.round(h * ratio)) {
            c.width = Math.round(w * ratio);
            c.height = Math.round(h * ratio);
        }
        const g = c.getContext('2d');
        g.setTransform(ratio, 0, 0, ratio, 0, 0);
        g.clearRect(0, 0, w, h);
        return { g, w, h };
    }

    const fmtUs = us => us >= 1000 ? (us / 1000).toFixed(us >= 10000 ? 0 : 1) + ' ms' : Math.round(us) + ' µs';

    function init(host, dotnet) {
        host.innerHTML = '';
        const legend = el('div', 'tl-legend', host);
        const row = el('div', 'tl-row', host);
        const yAxis = canvas(row, MAIN_H);
        yAxis.style.width = PAD.l + 'px';
        const scroller = el('div', 'tl-scroll', row);
        const bars = el('div', 'tl-bars', scroller);
        const main = canvas(bars, MAIN_H);
        const strip = canvas(bars, STRIP_H);
        const empty = el('div', 'tl-empty', host);
        empty.textContent = 'waiting for the server’s system ticks…';
        const drill = el('div', 'tl-drill', host);
        const drillTitle = el('h4', null, drill);
        const drillCanvas = canvas(drill, DRILL_H);
        const drillTable = el('table', 'tl-table', drill);
        const tip = el('div', 'tl-tip', host);
        const st = {
            host, dotnet, legend, yAxis, scroller, main, strip, empty, drill, drillTitle, drillCanvas, drillTable, tip,
            names: [], S: 0, workers: 1, tps: 10, targetMs: 100, multiplier: 1,
            secId: null, secFirst: null, wallMean: null, wallMax: null, ticks: null, cpu: null, eff: null, touched: null,
            newest: -1, oldest: -1, first: -1, count: 0, top: [], colour: new Map(), selected: -1, drillTicks: null, hoverTick: -1,
        };
        hosts.set(host, st);
        main.addEventListener('mousemove', e => hover(st, e));
        main.addEventListener('mouseleave', () => { st.tip.style.display = 'none'; });
        main.addEventListener('click', e => select(st, secondAt(st, e)));
        drillCanvas.addEventListener('mousemove', e => drillHover(st, e));
        drillCanvas.addEventListener('mouseleave', () => { st.hoverTick = -1; drawDrill(st); });
        document.addEventListener('keydown', e => { if (e.key === 'Escape' && st.selected >= 0) select(st, st.selected); });
        new ResizeObserver(() => draw(st)).observe(host);
    }

    function names(host, list, workers, tps, targetMs) {
        const st = hosts.get(host);
        if (!st) return;
        st.names = Array.from(list || []);
        st.S = st.names.length;
        st.workers = Math.max(1, workers || 1);
        st.tps = Math.max(1, tps || 10);
        if (targetMs > 0) st.targetMs = targetMs;
        st.secId = new Float64Array(SECONDS).fill(-1);
        st.secFirst = new Float64Array(SECONDS);
        st.wallMean = new Float32Array(SECONDS);
        st.wallMax = new Float32Array(SECONDS);
        st.ticks = new Uint16Array(SECONDS);
        st.cpu = new Float32Array(SECONDS * st.S);
        st.eff = new Uint8Array(SECONDS * st.S);
        st.touched = new Uint8Array(SECONDS * st.S);
        st.newest = -1;
        st.oldest = -1;
        st.first = -1;
        st.count = 0;
        st.top = [];
        st.colour = new Map();
        st.selected = -1;
        st.drill.style.display = 'none';
        draw(st);
    }

    /** Seconds from SystemTimeline.CopySeconds: f64 second · f64 firstTick · f32 meanWall · f32 maxWall · u16 ticks · u16 pad · S × (f32 cpu · u8 eff · u8 touched). */
    function seconds(host, bytes, workers, targetMs, multiplier) {
        const st = hosts.get(host);
        if (!st || st.S === 0 || !bytes) return;
        if (workers > 0) st.workers = workers;
        if (targetMs > 0) st.targetMs = targetMs;
        st.multiplier = multiplier > 0 ? multiplier : 1;
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        const size = 28 + st.S * 6;
        for (let at = 0; at + size <= bytes.byteLength; at += size) {
            const sec = view.getFloat64(at, true);
            const s = sec % SECONDS;
            st.secId[s] = sec;
            st.secFirst[s] = view.getFloat64(at + 8, true);
            st.wallMean[s] = view.getFloat32(at + 16, true);
            st.wallMax[s] = view.getFloat32(at + 20, true);
            st.ticks[s] = view.getUint16(at + 24, true);
            for (let i = 0; i < st.S; i++) {
                const r = at + 28 + i * 6;
                st.cpu[s * st.S + i] = view.getFloat32(r, true);
                st.eff[s * st.S + i] = view.getUint8(r + 4);
                st.touched[s * st.S + i] = view.getUint8(r + 5);
            }
            st.newest = Math.max(st.newest, sec);
            st.oldest = st.oldest < 0 ? sec : Math.min(st.oldest, sec);
        }
        rank(st);
        draw(st);
    }

    /** The slot of a second still in the window, or -1. */
    function slotOf(st, sec) {
        if (sec < 0 || st.newest < 0 || sec > st.newest || sec <= st.newest - SECONDS) return -1;
        const s = sec % SECONDS;
        return st.secId[s] === sec ? s : -1;
    }

    /**
     * The top 10 by CPU over the window. Colours stay with their system for the page's life: re-ranked only when the set changes, a system leaving frees its
     * colour, one entering takes a free one.
     */
    function rank(st) {
        const total = new Float64Array(st.S);
        for (let k = 0; k < SECONDS; k++) {
            const s = slotOf(st, st.newest - k);
            if (s < 0) continue;
            for (let i = 0; i < st.S; i++) total[i] += st.cpu[s * st.S + i];
        }
        const order = [...Array(st.S).keys()].filter(i => total[i] > 0).sort((a, b) => total[b] - total[a]).slice(0, TOP);
        // The same set: as many coloured as ranked, every one of them coloured — a stale colour left from an earlier set counts as a change.
        const same = st.colour.size === order.length && order.every(i => st.colour.has(i));
        if (!same) {
            for (const i of [...st.colour.keys()]) if (!order.includes(i)) st.colour.delete(i);
            const used = new Set(st.colour.values());
            for (const i of order) {
                if (!st.colour.has(i)) {
                    const free = PALETTE.find(c => !used.has(c)) || OTHER;
                    st.colour.set(i, free);
                    used.add(free);
                }
            }
        }
        st.top = order;
        legend(st);
    }

    function legend(st) {
        st.legend.innerHTML = '';
        for (const i of st.top) {
            const item = el('span', null, st.legend);
            const sw = el('span', 'tl-sw', item);
            sw.style.background = st.colour.get(i);
            item.appendChild(document.createTextNode(st.names[i]));
        }
        const other = el('span', null, st.legend);
        const sw = el('span', 'tl-sw', other);
        sw.style.background = OTHER;
        other.appendChild(document.createTextNode('other'));
        const line = el('span', null, st.legend);
        line.textContent = `— mean tick wall · ▪ worst tick · ┄ budget ${fmtUs(st.targetMs * 1000)}`;
    }

    /** A second's stack: the top 10 in rank order, then "other". */
    function stack(st, cpuAt) {
        const parts = [];
        let sum = 0, rest = 0;
        for (let i = 0; i < st.S; i++) sum += cpuAt(i);
        for (const i of st.top) parts.push([st.colour.get(i), cpuAt(i)]);
        rest = sum - parts.reduce((a, p) => a + p[1], 0);
        parts.push([OTHER, Math.max(0, rest)]);
        return { parts, sum };
    }

    function plot(w, h) {
        return { x0: PAD.l, x1: w - PAD.r, y0: PAD.t, y1: h - PAD.b };
    }

    /** The y-axis top: the 98th percentile of the bars and of the mean wall, and the budget — one spike must not flatten the chart. */
    function yTop(st) {
        const totals = [], walls = [];
        for (let k = 0; k < SECONDS; k++) {
            const s = slotOf(st, st.newest - k);
            if (s < 0) continue;
            let sum = 0;
            for (let i = 0; i < st.S; i++) sum += st.cpu[s * st.S + i];
            totals.push(sum);
            walls.push(st.wallMean[s] * 1000);
        }
        const p98 = a => a.length ? a.sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * 0.98))] : 0;
        return Math.max(p98(totals), p98(walls), st.targetMs * 1000 * 0.25, 1000) * 1.12;
    }

    function axis(g, p, top, w) {
        g.strokeStyle = 'rgba(255,255,255,.07)';
        g.fillStyle = '#8b97a5';
        g.font = '10px system-ui, sans-serif';
        g.textAlign = 'right';
        g.textBaseline = 'middle';
        for (let k = 0; k <= 4; k++) {
            const v = top * k / 4;
            const y = p.y1 - (p.y1 - p.y0) * k / 4;
            g.beginPath(); g.moveTo(p.x0, y); g.lineTo(p.x1, y); g.stroke();
            g.fillText(fmtUs(v), p.x0 - 4, y);
        }
    }

    /**
     * Sizes the bars' canvases to the seconds held (PITCH each) and keeps the scroller where the reader left it: at "now" if it was there, else on the
     * same seconds — once the window is full, a new second shifts every bar left by PITCH.
     */
    function layout(st) {
        const count = Math.min(SECONDS, st.newest - st.oldest + 1);
        const first = st.newest - count + 1;
        const sc = st.scroller;
        const atNow = st.count === 0 || sc.scrollLeft + sc.clientWidth >= sc.scrollWidth - PITCH;
        const shift = st.count === 0 ? 0 : (first - st.first) * PITCH;
        const width = BARS.l + count * PITCH + BARS.r + 'px';
        if (st.main.style.width !== width) {
            st.main.style.width = width;
            st.strip.style.width = width;
        }
        st.first = first;
        st.count = count;
        sc.scrollLeft = atNow ? sc.scrollWidth : Math.max(0, sc.scrollLeft - shift);
    }

    function draw(st) {
        st.empty.style.display = st.newest < 0 ? 'block' : 'none';
        if (st.newest < 0) {
            context(st.main);
            context(st.strip);
            context(st.yAxis);
            return;
        }
        layout(st);
        const { g, w, h } = context(st.main);
        const p = { x0: BARS.l, x1: w - BARS.r, y0: PAD.t, y1: h - PAD.b };
        const top = yTop(st);
        st.yTop = top;
        yLabels(st, top);
        g.strokeStyle = 'rgba(255,255,255,.07)';
        for (let k = 0; k <= 4; k++) {
            const gy = p.y1 - (p.y1 - p.y0) * k / 4;
            g.beginPath(); g.moveTo(0, gy); g.lineTo(w, gy); g.stroke();
        }
        const bw = PITCH - 1;
        const y = v => p.y1 - (p.y1 - p.y0) * Math.min(v, top) / top;
        const wallPts = [];
        for (let k = 0; k < st.count; k++) {
            const sec = st.first + k;
            const s = slotOf(st, sec);
            if (s < 0) continue;
            const x = p.x0 + k * PITCH;
            const { parts, sum } = stack(st, i => st.cpu[s * st.S + i]);
            let acc = 0;
            for (const [colour, v] of parts) {
                if (v <= 0) continue;
                const ya = y(acc), yb = y(acc + v);
                g.fillStyle = colour;
                g.fillRect(x, yb, bw, ya - yb);
                acc += v;
            }
            if (sum > top) {
                // Clipped: a cap marker, so a bar past the scale is not read as its height.
                g.fillStyle = '#ffffff';
                g.fillRect(x, p.y0 - 3, bw, 2);
            }
            if (sec === st.selected) {
                g.strokeStyle = '#f0b84a';
                g.lineWidth = 1;
                g.strokeRect(x - 0.5, p.y0, bw + 1, p.y1 - p.y0);
            }
            wallPts.push([x + bw / 2, y(st.wallMean[s] * 1000)]);
            // The worst tick of the second.
            g.fillStyle = '#ff7a6b';
            g.fillRect(x, y(st.wallMax[s] * 1000) - 1, bw, 2);
        }
        g.strokeStyle = 'rgba(231,236,241,.85)';
        g.lineWidth = 1.2;
        g.beginPath();
        wallPts.forEach(([px, py], i) => i ? g.lineTo(px, py) : g.moveTo(px, py));
        g.stroke();
        // The budget, only when on the scale: clamped to the top, it would read as a budget that low.
        g.setLineDash([4, 4]);
        g.strokeStyle = '#f0b84a';
        if (st.targetMs * 1000 <= top) { g.beginPath(); g.moveTo(0, y(st.targetMs * 1000)); g.lineTo(w, y(st.targetMs * 1000)); g.stroke(); }
        g.setLineDash([]);
        // The time axis: a label a minute, a mark every 30 s.
        g.fillStyle = '#8b97a5';
        g.textAlign = 'center';
        g.textBaseline = 'top';
        for (let ago = 0; ago < st.count; ago += 30) {
            const x = p.x0 + (st.count - 1 - ago) * PITCH + bw / 2;
            g.fillRect(x, p.y1, 1, ago % 60 === 0 ? 4 : 2);
            if (ago % 60 === 0) g.fillText(ago === 0 ? 'now' : `-${ago / 60} min`, Math.max(14, x), p.y1 + 5);
        }
        drawStrip(st, p.x0, bw);
        if (st.selected >= 0) drawDrill(st);
    }

    /** The y-axis, pinned beside the scroller: the same scale and rows as the bars' gridlines. */
    function yLabels(st, top) {
        const { g, h } = context(st.yAxis);
        const y0 = PAD.t, y1 = h - PAD.b;
        g.fillStyle = '#8b97a5';
        g.font = '10px system-ui, sans-serif';
        g.textAlign = 'right';
        g.textBaseline = 'middle';
        for (let k = 0; k <= 4; k++) g.fillText(fmtUs(top * k / 4), PAD.l - 4, y1 - (y1 - y0) * k / 4);
    }

    /** The pool's utilisation per second, Σ CPU ÷ (mean wall × workers), on a ramp: dark is the pool idle behind serial work. */
    function drawStrip(st, x0, bw) {
        const { g } = context(st.strip);
        for (let k = 0; k < st.count; k++) {
            const s = slotOf(st, st.first + k);
            if (s < 0) continue;
            let sum = 0;
            for (let i = 0; i < st.S; i++) sum += st.cpu[s * st.S + i];
            const u = Math.min(1, sum / Math.max(1, st.wallMean[s] * 1000 * st.workers));
            g.fillStyle = `hsl(${210 - 170 * u}, 70%, ${18 + 40 * u}%)`;
            g.fillRect(x0 + k * PITCH, 1, bw, STRIP_H - 2);
        }
    }

    function secondAt(st, e) {
        const rect = st.main.getBoundingClientRect();
        const k = Math.floor((e.clientX - rect.left - BARS.l) / PITCH);
        if (k < 0 || k >= st.count) return -1;
        return st.first + k;
    }

    function rows(st, cpuAt, wallAt, effAt, touchedAt) {
        return [...Array(st.S).keys()].map(i => ({ i, cpu: cpuAt(i), wall: wallAt(i), eff: effAt(i), touched: touchedAt(i) }))
            .filter(r => r.cpu > 0 || r.wall > 0).sort((a, b) => b.cpu - a.cpu);
    }

    function hover(st, e) {
        const sec = secondAt(st, e);
        const s = slotOf(st, sec);
        if (s < 0) { st.tip.style.display = 'none'; return; }
        const list = rows(st, i => st.cpu[s * st.S + i], () => 0, i => st.eff[s * st.S + i] / 10, i => st.touched[s * st.S + i]);
        const ago = st.newest - sec;
        let html = `<span class="muted">CPU · eff cores / chunks</span><br><b>${ago === 0 ? 'now' : ago + ' s ago'}</b> · ticks ${st.secFirst[s]}… (${st.ticks[s]})` +
            `${st.multiplier > 1 ? ` · ×${st.multiplier} modulation` : ''}<br>wall mean ${fmtUs(st.wallMean[s] * 1000)} · worst ${fmtUs(st.wallMax[s] * 1000)}` +
            `<table>`;
        for (const r of list) {
            html += `<tr><td><span class="tl-sw" style="background:${st.colour.get(r.i) || OTHER}"></span>${st.names[r.i]}</td>` +
                `<td class="num">${fmtUs(r.cpu)}</td><td class="num">${r.eff.toFixed(1)} / ${r.touched}</td></tr>`;
        }
        st.tip.innerHTML = html + '</table>';
        const rect = st.host.getBoundingClientRect();
        st.tip.style.display = 'block';
        const left = e.clientX - rect.left + 14;
        st.tip.style.left = Math.min(left, rect.width - st.tip.offsetWidth - 4) + 'px';
        st.tip.style.top = (e.clientY - rect.top + 14) + 'px';
    }

    /** A click: that second's ticks below (from the dashboard's per-tick ring); the same bar again, or Esc, closes. */
    async function select(st, sec) {
        if (sec < 0 || sec === st.selected) {
            st.selected = -1;
            st.drill.style.display = 'none';
            draw(st);
            return;
        }
        st.selected = sec;
        st.drill.style.display = 'block';
        st.drillTicks = null;
        draw(st);
        try {
            const bytes = await st.dotnet.invokeMethodAsync('Ticks', sec);
            if (st.selected !== sec) return;
            st.drillTicks = parseTicks(st, bytes);
        } catch (err) {
            st.drillTicks = [];
        }
        drawDrill(st);
    }

    /** Ticks from SystemTimeline.CopyTicks: f64 tick · f32 wallMs · u32 pad · S × (f32 cpu · f32 wall · u8 eff · u8 touched). */
    function parseTicks(st, bytes) {
        const out = [];
        if (!bytes) return out;
        const u8 = bytes instanceof Uint8Array ? bytes : Uint8Array.from(atob(bytes), c => c.charCodeAt(0));
        const view = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
        const size = 16 + st.S * 10;
        for (let at = 0; at + size <= u8.byteLength; at += size) {
            const t = { tick: view.getFloat64(at, true), wallMs: view.getFloat32(at + 8, true), cpu: [], wall: [], eff: [], touched: [] };
            for (let i = 0; i < st.S; i++) {
                const r = at + 16 + i * 10;
                t.cpu.push(view.getFloat32(r, true));
                t.wall.push(view.getFloat32(r + 4, true));
                t.eff.push(view.getUint8(r + 8) / 10);
                t.touched.push(view.getUint8(r + 9));
            }
            out.push(t);
        }
        return out;
    }

    function drawDrill(st) {
        const ticks = st.drillTicks;
        const ago = st.newest - st.selected;
        st.drillTitle.innerHTML = `Ticks of the second ${ago === 0 ? 'now' : ago + ' s ago'} <span>— click the bar again or Esc to close</span>`;
        const { g, w, h } = context(st.drillCanvas);
        if (!ticks) { g.fillStyle = '#8b97a5'; g.fillText('loading…', 60, 30); return; }
        if (ticks.length === 0) { g.fillStyle = '#8b97a5'; g.fillText('no ticks held for this second', 60, 30); st.drillTable.innerHTML = ''; return; }
        // Two label lines under each bar: the tick number, its wall time.
        const p = { ...plot(w, h), y1: h - 32 };
        let top = st.targetMs * 1000 * 0.25;
        for (const t of ticks) top = Math.max(top, t.cpu.reduce((a, b) => a + b, 0), t.wallMs * 1000);
        top *= 1.1;
        axis(g, p, top, w);
        // Left-aligned, DRILL_GAP apart: as wide as 60 px, narrower only when the second's ticks would not fit.
        const DRILL_GAP = 10;
        const bw = Math.max(2, Math.min(60, (p.x1 - p.x0 - DRILL_GAP) / ticks.length - DRILL_GAP));
        const y = v => p.y1 - (p.y1 - p.y0) * Math.min(v, top) / top;
        st.drillBars = [];
        ticks.forEach((t, k) => {
            const x = p.x0 + DRILL_GAP + k * (bw + DRILL_GAP);
            const { parts } = stack(st, i => t.cpu[i]);
            let acc = 0;
            for (const [colour, v] of parts) {
                if (v <= 0) continue;
                g.fillStyle = colour;
                g.fillRect(x, y(acc + v), bw, y(acc) - y(acc + v));
                acc += v;
            }
            if (k === st.hoverTick) { g.strokeStyle = '#f0b84a'; g.strokeRect(x - 1, p.y0, bw + 2, p.y1 - p.y0); }
            // The tick's wall time.
            g.fillStyle = 'rgba(231,236,241,.9)';
            g.fillRect(x - 2, y(t.wallMs * 1000) - 1, bw + 4, 2);
            g.fillStyle = '#8b97a5';
            g.textAlign = 'center';
            g.textBaseline = 'top';
            // Labels only where they fit: a second of many ticks has bars too narrow for them.
            if (bw >= 30) {
                g.fillText('#' + t.tick, x + bw / 2, p.y1 + 4);
                g.fillStyle = 'rgba(231,236,241,.9)';
                g.fillText(fmtUs(t.wallMs * 1000), x + bw / 2, p.y1 + 17);
            }
            st.drillBars.push([x, x + bw]);
        });
        g.setLineDash([4, 4]);
        g.strokeStyle = '#f0b84a';
        if (st.targetMs * 1000 <= top) { g.beginPath(); g.moveTo(p.x0, y(st.targetMs * 1000)); g.lineTo(p.x1, y(st.targetMs * 1000)); g.stroke(); }
        g.setLineDash([]);
        table(st, ticks, st.hoverTick);
    }

    /** The per-system table: over the whole second (mean per tick), or the hovered tick alone. A high wall with low utilisation is a straggler or a serial step. */
    function table(st, ticks, only) {
        const chosen = only >= 0 && only < ticks.length ? [ticks[only]] : ticks;
        const n = chosen.length;
        const mean = (f, i) => chosen.reduce((a, t) => a + t[f][i], 0) / n;
        const list = rows(st, i => mean('cpu', i), i => mean('wall', i), i => mean('eff', i), i => Math.max(...chosen.map(t => t.touched[i])));
        // The tick's (or the mean tick's) own wall and Σ CPU: their ratio is the cores the tick kept busy on average.
        const wallUs = chosen.reduce((a, t) => a + t.wallMs, 0) / n * 1000;
        const cpuUs = chosen.reduce((a, t) => a + t.cpu.reduce((x, c) => x + c, 0), 0) / n;
        const summary = ` · wall ${fmtUs(wallUs)} · CPU ${fmtUs(cpuUs)} (${(cpuUs / Math.max(1, wallUs)).toFixed(1)} cores)`;
        let html = `<tr><th>${only >= 0 ? 'tick #' + ticks[only].tick : 'mean per tick over ' + n + ' ticks'}${summary}</th><th class="num">CPU</th>` +
            `<th class="num">wall</th><th class="num">eff cores</th><th class="num" title="SystemTelemetry.WorkersTouched — which the scheduler fills with the system’s chunk count">chunks</th><th>utilisation (eff ÷ ${st.workers} workers)</th></tr>`;
        for (const r of list) {
            const util = Math.min(100, r.eff / st.workers * 100);
            html += `<tr><td><span class="tl-sw" style="background:${st.colour.get(r.i) || OTHER}"></span>${st.names[r.i]}</td>` +
                `<td class="num">${fmtUs(r.cpu)}</td><td class="num">${fmtUs(r.wall)}</td><td class="num">${r.eff.toFixed(1)}</td>` +
                `<td class="num">${r.touched}</td><td><div class="tl-util"><div style="width:${util.toFixed(1)}%"></div></div></td></tr>`;
        }
        st.drillTable.innerHTML = html;
    }

    function drillHover(st, e) {
        if (!st.drillBars) return;
        const rect = st.drillCanvas.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const k = st.drillBars.findIndex(([a, b]) => x >= a - 4 && x <= b + 4);
        if (k !== st.hoverTick) { st.hoverTick = k; drawDrill(st); }
    }

    window.sysTimeline = { init, names, seconds };
})();
