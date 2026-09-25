"""Generates doc/in-depth-overview/assets/subscriptions-cell-geometry.svg — the cell classification is computed, not drawn by hand."""
import math
import random
import sys

S = 30          # px per cell
N = 11          # window cells per axis (h = ceil(R'/c) + 2 = 5 for R' = 3c)
R = 3.0         # R' in cells
PANEL_X = [40, 430, 820]
PANEL_Y = 118
W, H = 1200, 650

BLUE, BLUE_S = "#dbeafe", "#93c5fd"
ORANGE, ORANGE_S = "#ffedd5", "#fdba74"
GRID = "#d4d4d8"
INK = "#18181b"
GREEN, RED = "#15803d", "#b91c1c"

out = []


def emit(s):
    out.append(s)


def cell_min2(ax, ay, i, j):
    dx = max(i - ax, 0.0, ax - (i + 1))
    dy = max(j - ay, 0.0, ay - (j + 1))
    return dx * dx + dy * dy


def cell_max2(ax, ay, i, j):
    dx = max(abs(ax - i), abs(ax - (i + 1)))
    dy = max(abs(ay - j), abs(ay - (j + 1)))
    return dx * dx + dy * dy


def classify(ax, ay, i, j, r=R):
    if cell_max2(ax, ay, i, j) <= r * r:
        return "in"
    if cell_min2(ax, ay, i, j) > r * r:
        return "out"
    return "edge"


def px(p, x):
    return PANEL_X[p] + x * S


def py(y):
    return PANEL_Y + y * S


def rect(p, i, j, fill, stroke=GRID, extra=""):
    emit(f'<rect x="{px(p, i):.1f}" y="{py(j):.1f}" width="{S}" height="{S}" fill="{fill}" stroke="{stroke}" stroke-width="0.8" {extra}/>')


def circle(p, cx, cy, r, stroke, dash="", width=2.0, fill="none"):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    emit(f'<circle cx="{px(p, cx):.1f}" cy="{py(cy):.1f}" r="{r * S:.1f}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"{d}/>')


def dot(p, x, y, held):
    if held:
        emit(f'<circle cx="{px(p, x):.1f}" cy="{py(y):.1f}" r="3.6" fill="{INK}"/>')
    else:
        emit(f'<circle cx="{px(p, x):.1f}" cy="{py(y):.1f}" r="3.6" fill="#ffffff" stroke="#a1a1aa" stroke-width="1.4"/>')


def text(x, y, s, cls, anchor="start"):
    emit(f'<text class="{cls}" x="{x:.1f}" y="{y:.1f}" text-anchor="{anchor}">{s}</text>')


def anchor_mark(p, x, y, color=INK, label=None, dx=8, dy=-8):
    X, Y = px(p, x), py(y)
    emit(f'<path d="M{X:.1f},{Y - 6:.1f} L{X + 6:.1f},{Y:.1f} L{X:.1f},{Y + 6:.1f} L{X - 6:.1f},{Y:.1f} z" fill="{color}" stroke="#ffffff" stroke-width="1"/>')
    if label:
        text(X + dx, Y + dy, label, "lb")


def window_outline(p):
    emit(f'<rect x="{px(p, 0)}" y="{py(0)}" width="{N * S}" height="{N * S}" fill="none" stroke="#52525b" stroke-width="1.6"/>')


rng = random.Random(7)
entities = [(rng.uniform(0.3, N - 0.3), rng.uniform(0.3, N - 0.3)) for _ in range(70)]

emit(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" '
     'font-family="ui-sans-serif, -apple-system, \'Segoe UI\', Roboto, Helvetica, Arial, sans-serif">')
emit("""  <style>
    .t  { font-size: 21px; font-weight: 700; fill: #18181b; }
    .st { font-size: 13.5px; fill: #52525b; }
    .h  { font-size: 15px; font-weight: 700; fill: #18181b; }
    .l  { font-size: 12.5px; fill: #3f3f46; }
    .lb { font-size: 12.5px; font-weight: 700; fill: #18181b; }
    .lm { font-size: 11.5px; fill: #71717a; }
  </style>""")
emit(f'<rect width="{W}" height="{H}" fill="#ffffff"/>')
emit('<defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">'
     '<rect width="6" height="6" fill="#f4f4f5"/><line x1="0" y1="0" x2="0" y2="6" stroke="#a1a1aa" stroke-width="1.6"/></pattern>'
     + ''.join(f'<marker id="ar{n}" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="{c}"/></marker>'
             for n, c in (("b", "#2563eb"), ("g", GREEN), ("r", RED))) + '</defs>')

text(40, 40, "The replication cell grid, seen from one session", "t")
text(40, 64, "What a session holds is recomputed from geometry every frame: an anchor, a radius and one bit per cell of its window. "
     "Nothing is stored per entity.", "st")

# ── Panel 1: a session's view ──────────────────────────────────────────────────────────────────────────────────────────────────────────────
p = 0
ax, ay = 5.45, 5.6
acx, acy = int(ax), int(ay)
delivered = set()
budget_cells = 9  # ring 3 is only partly delivered: the enter budget ran out
for ring in range(0, 6):
    for dy in range(-ring, ring + 1):
        full = abs(dy) == ring
        step = 1 if full else max(2 * ring, 1)
        for dx in range(-ring, ring + 1, step):
            i, j = acx + dx, acy + dy
            if not (0 <= i < N and 0 <= j < N) or cell_min2(ax, ay, i, j) > R * R:
                continue
            if ring <= 2 or (ring == 3 and budget_cells > 0):
                if ring == 3:
                    budget_cells -= 1
                delivered.add((i, j))
for j in range(N):
    for i in range(N):
        k = classify(ax, ay, i, j)
        if k == "out":
            rect(p, i, j, "#ffffff")
        elif (i, j) not in delivered:
            rect(p, i, j, "url(#hatch)")
        elif k == "in":
            rect(p, i, j, BLUE, BLUE_S)
        else:
            rect(p, i, j, ORANGE, ORANGE_S)
window_outline(p)
circle(p, ax, ay, R, "#1d4ed8")
for (x, y) in entities:
    inside = (x - ax) ** 2 + (y - ay) ** 2 <= R * R
    dot(p, x, y, inside and (int(x), int(y)) in delivered)
anchor_mark(p, ax, ay, "#1d4ed8", "a", 7, -7)
text(px(p, 0), PANEL_Y - 16, "1 · What the session holds", "h")
text(px(p, 0), py(N) + 22, "known(s, e) ⟺ |a − v̂ₑ| ≤ R′ and cell(v̂ₑ) is delivered", "lb")
text(px(p, 0), py(N) + 40, "The window is (2h + 1) cells per axis, h = ⌈R′/c⌉ + 2.", "l")
text(px(p, 0), py(N) + 56, "Cells are delivered nearest first, under the enter budget:", "l")
text(px(p, 0), py(N) + 72, "a hatched cell is in range but not sent yet, so its", "l")
text(px(p, 0), py(N) + 88, "entities are not held yet. The geometry stays exact.", "l")

# ── Panel 2: the anchor moves — the crescent ──────────────────────────────────────────────────────────────────────────────────────────────
p = 1
ox, oy = 5.2, 5.5
nx, ny = 6.0, 5.2
for j in range(N):
    for i in range(N):
        ka, kn = classify(ox, oy, i, j), classify(nx, ny, i, j)
        if ka == "in" and kn == "in":
            rect(p, i, j, BLUE, BLUE_S)
        elif ka == "out" and kn == "out":
            rect(p, i, j, "#ffffff")
        else:
            rect(p, i, j, ORANGE, ORANGE_S)
window_outline(p)
circle(p, ox, oy, R, "#71717a", "6 4", 1.8)
circle(p, nx, ny, R, "#1d4ed8")
for (x, y) in entities:
    was = (x - ox) ** 2 + (y - oy) ** 2 <= R * R
    now = (x - nx) ** 2 + (y - ny) ** 2 <= R * R
    X, Y = px(p, x), py(y)
    if now and not was:
        emit(f'<path d="M{X - 5:.1f},{Y:.1f} L{X + 5:.1f},{Y:.1f} M{X:.1f},{Y - 5:.1f} L{X:.1f},{Y + 5:.1f}" stroke="{GREEN}" stroke-width="2.6"/>')
    elif was and not now:
        emit(f'<path d="M{X - 5:.1f},{Y:.1f} L{X + 5:.1f},{Y:.1f}" stroke="{RED}" stroke-width="2.6"/>')
    else:
        dot(p, x, y, now)
anchor_mark(p, ox, oy, "#71717a")
anchor_mark(p, nx, ny, "#1d4ed8")
emit(f'<line x1="{px(p, ox):.1f}" y1="{py(oy):.1f}" x2="{px(p, nx) - 7:.1f}" y2="{py(ny) + 2:.1f}" stroke="#2563eb" stroke-width="1.6" marker-end="url(#arb)"/>')
text(px(p, 0), PANEL_Y - 16, "2 · The anchor moves: sweep the crescent", "h")
text(px(p, 0), py(N) + 22, "Only cells straddling either sphere are swept.", "lb")
text(px(p, 0), py(N) + 40, "The anchor moves only past a slack of min(R′/48, c/2),", "l")
text(px(p, 0), py(N) + 56, "so a still or slow viewer costs nothing. The move here is", "l")
text(px(p, 0), py(N) + 72, "exaggerated. A move of more than one cell is a teleport:", "l")
text(px(p, 0), py(N) + 88, "a RESET, and the view is delivered again.", "l")

# ── Panel 3: this tick's events ───────────────────────────────────────────────────────────────────────────────────────────────────────────
p = 2
ax3, ay3 = 5.5, 5.5
for j in range(N):
    for i in range(N):
        k = classify(ax3, ay3, i, j)
        rect(p, i, j, {"in": BLUE, "edge": ORANGE, "out": "#ffffff"}[k], {"in": BLUE_S, "edge": ORANGE_S, "out": GRID}[k])
window_outline(p)
circle(p, ax3, ay3, R, "#1d4ed8")
anchor_mark(p, ax3, ay3, "#1d4ed8")


def move(p, x0, y0, x1, y1, label, lx, ly, color, m):
    # old v̂ hollow, new v̂ filled; the arrow stops short of the new dot
    X0, Y0, X1, Y1 = px(p, x0), py(y0), px(p, x1), py(y1)
    L = math.hypot(X1 - X0, Y1 - Y0)
    ex, ey = X1 - (X1 - X0) * 6 / L, Y1 - (Y1 - Y0) * 6 / L
    emit(f'<line x1="{X0:.1f}" y1="{Y0:.1f}" x2="{ex:.1f}" y2="{ey:.1f}" stroke="{color}" stroke-width="2" marker-end="url(#ar{m})"/>')
    emit(f'<circle cx="{X0:.1f}" cy="{Y0:.1f}" r="3.6" fill="#ffffff" stroke="{color}" stroke-width="1.6"/>')
    emit(f'<circle cx="{X1:.1f}" cy="{Y1:.1f}" r="3.6" fill="{color}"/>')
    text(px(p, lx), py(ly), label, "lb")


move(p, 4.15, 4.3, 4.8, 4.75, "update, no test", 5.1, 4.1, "#1d4ed8", "b")
move(p, 8.9, 7.8, 7.7, 7.3, "enter", 8.3, 8.75, GREEN, "g")
move(p, 3.4, 3.4, 2.4, 2.6, "leave", 1.2, 2.3, RED, "r")
# the v̂ slack around the updated entity
circle(p, 4.8, 4.75, 0.5, "#1d4ed8", "3 3", 1.2)
text(px(p, 3.55), py(5.55), "h_A", "lm")
text(px(p, 0), PANEL_Y - 16, "3 · This tick's events, cell by cell", "h")
text(px(p, 0), py(N) + 22, "was = the old v̂ held, is = the new v̂ held.", "lb")
text(px(p, 0), py(N) + 40, "A blue cell (inside the sphere, delivered in both windows)", "l")
text(px(p, 0), py(N) + 56, "needs no test. v̂ moves only past the slack h_A, so a", "l")
text(px(p, 0), py(N) + 72, "mover whose motion segment still holds makes no event.", "l")
text(px(p, 0), py(N) + 88, "A cell change files the event under both cells.", "l")

# ── Legend ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
ly = 604
items = [
    ("anchor", None, None, "anchor"),
    ("rect", BLUE, BLUE_S, "interior cell"),
    ("rect", ORANGE, ORANGE_S, "straddling cell: tested per entity"),
    ("rect", "url(#hatch)", GRID, "in range, not delivered yet"),
    ("dot", INK, None, "held entity"),
    ("hollow", None, None, "not held"),
    ("plus", GREEN, None, "enter"),
    ("minus", RED, None, "leave"),
]
x = 40
for kind, fill, stroke, label in items:
    if kind == "anchor":
        emit(f'<path d="M{x + 7},{ly - 10} L{x + 13},{ly - 4} L{x + 7},{ly + 2} L{x + 1},{ly - 4} z" fill="#1d4ed8"/>')
    elif kind == "rect":
        emit(f'<rect x="{x}" y="{ly - 11}" width="14" height="14" fill="{fill}" stroke="{stroke}"/>')
    elif kind == "dot":
        emit(f'<circle cx="{x + 7}" cy="{ly - 4}" r="4" fill="{INK}"/>')
    elif kind == "hollow":
        emit(f'<circle cx="{x + 7}" cy="{ly - 4}" r="4" fill="#fff" stroke="#a1a1aa" stroke-width="1.4"/>')
    elif kind == "plus":
        emit(f'<path d="M{x + 2},{ly - 4} L{x + 12},{ly - 4} M{x + 7},{ly - 9} L{x + 7},{ly + 1}" stroke="{GREEN}" stroke-width="2.6"/>')
    else:
        emit(f'<path d="M{x + 2},{ly - 4} L{x + 12},{ly - 4}" stroke="{RED}" stroke-width="2.6"/>')
    text(x + 20, ly, label, "l")
    x += 20 + len(label) * 6.6 + 26
text(40, ly + 26, "Cells: side c = ReplicationCellM. Here R′ = 3c, the rule of thumb, so h = 5 and the window is 11 × 11. "
     "In a deep (3D) grid the same holds with cubes, spheres and a shell.", "lm")
emit("</svg>")

open(sys.argv[1], "w", encoding="utf-8", newline="\n").write("\n".join(out))
