#!/usr/bin/env python3
"""Replays a Typhon page trace against eviction policies (I7 of the huge-scan assessment).

A trace is one record per page request, hit or miss, captured by the page cache (DatabaseEngine.StartPageTrace / WritePageTrace, or
TYPHON_PAGE_TRACE_FILE with TYPHON__PROFILER__PAGETRACE__ENABLED=true). This replays its request sequence against:

  clock        today's policy, as PagedMMF runs it: a 0-5 counter per slot, bumped by every request but a cold read pin, a sweep that decrements
               until it finds 0, and the adjacency path that takes the slot after the previous file page's when its counter is 0 (B10)
  belady       the optimum: evict the resident page asked for again furthest in the future
  gate-ring    the spike's rules 1, 2 and 4: each operation (--op) loads up to N/16 pages normally; past that its misses recycle its own last RING
               slots
  probation    the shared budget proposed for I8: every first load enters one probation area of P slots, FIFO; a page asked for again by a different
               operation is promoted to the main clock; a ring of RING slots per operation is not needed, the area is the ring
  +ghost       either of the last two with rule 5: the pages it recycled, remembered up to one entry per slot; a miss on one from a different
               operation is a real re-reference, loaded as a hot page
  adaptive     PS-25 as shipped: probation and the clock share the slots, ARC's target from two ghost lists, CAR's reference bit (see adaptive())

Pinned, epoch-protected and dirty pages are not modelled: the trace does not carry them. A miss is weighted by who waited: a request from any thread but
the walk's, the thread with the most requests, costs --fg-weight; the walk's own cost 1. The request's kind cannot tell them apart: a walk opens its
rows through epoch-tagged accessors, like any point read.

    python3 scripts/page-trace-replay.py TRACE [--slots N,M] [--fg-weight 10] [--budgets 1/64,1/32,1/16,1/8] [--rings 32,256,1024]
                                         [--policies gate-ring,probation,adaptive]
    python3 scripts/page-trace-replay.py --self-test
"""
import argparse
import collections
import fractions
import heapq
import struct
import sys

MAGIC = 0x52545054
HEADER = struct.Struct("<IiiiqqQ")   # magic, version, slots, pages, records, dropped, stopwatch frequency
RECORD = struct.Struct("<qiiHBBi")   # timestamp, file page, operation, thread, kind, flags, pad
KIND_NAMES = ["Epoch", "EpochUnchecked", "ReadPinWarm", "ReadPinCold", "NoSweep"]
BUMPS = {0, 1, 2}                    # kinds that IncrementClockSweepCounter
FOREGROUND = {0, 1}                  # epoch-tagged requests
PAGE_TYPES = ["Unknown", "Free", "Root", "Occupancy", "Component", "Revision", "Index", "Cluster", "Vsbs", "StringTable", "Spatial", "EntityMap",
              "System"]


class Trace:
    def __init__(self, slots, pages, page_types, requests, recorded_misses, dropped, frequency):
        self.slots = slots
        self.pages = pages
        self.page_types = page_types
        self.requests = requests   # list of (page, kind, op) where op = (thread, epoch)
        self.recorded_misses = recorded_misses
        self.dropped = dropped
        self.frequency = frequency


def load(path, op_mode="operation"):
    with open(path, "rb") as f:
        data = f.read()
    magic, version, slots, pages, records, dropped, frequency = HEADER.unpack_from(data, 0)
    if magic != MAGIC or version != 1:
        sys.exit(f"{path}: not a page trace (magic {magic:#x}, version {version})")
    at = HEADER.size
    page_types = data[at:at + pages]
    at += pages
    requests = []
    misses = 0
    for ts, page, op, thread, kind, flags, _ in RECORD.iter_unpack(data[at:at + records * RECORD.size]):
        if kind == 4:
            continue   # introspection: never part of the working set
        requests.append((page, kind, (thread, 0 if op_mode == "thread" else op)))
        misses += flags & 1
    return Trace(slots, pages, page_types, requests, misses, dropped, frequency)


class Result:
    """Misses split by who waited: the walk's thread (background), or any other (the foreground: a tick, a commit, live traffic)."""
    walk_thread = None

    def __init__(self, name):
        self.name = name
        self.fg = 0
        self.bg = 0

    def miss(self, kind, op):
        if op[0] == Result.walk_thread:
            self.bg += 1
        else:
            self.fg += 1

    @property
    def total(self):
        return self.fg + self.bg

    def cost(self, fg_weight):
        return self.fg * fg_weight + self.bg


def clock(requests, slots, name="clock", adjacency=True):
    """Today's policy (PagedMMF): counters 0-5, a sweep, and the adjacency path that takes the next slot when its counter is 0."""
    res = Result(name)
    page_of = [-1] * slots
    counter = [0] * slots
    where = {}
    hand = 0
    used = 0
    for page, kind, op in requests:
        s = where.get(page)
        if s is not None:
            if kind in BUMPS and counter[s] < 5:
                counter[s] += 1
            continue
        res.miss(kind, op)
        if used < slots:
            s = used
            used += 1
        else:
            prev = where.get(page - 1) if adjacency else None
            s = None
            if prev is not None:
                nxt = (prev + 1) % slots
                if counter[nxt] == 0:
                    s = nxt
            if s is None:
                while counter[hand] > 0:
                    counter[hand] -= 1
                    hand = (hand + 1) % slots
                s = hand
                hand = (hand + 1) % slots
            del where[page_of[s]]
        page_of[s] = page
        where[page] = s
        counter[s] = 1 if kind in BUMPS else 0
    return res


def belady(requests, slots):
    """The optimum: on a miss with the cache full, evict the resident page whose next request is furthest away."""
    res = Result("belady")
    n = len(requests)
    nxt = [0] * n
    last = {}
    for i in range(n - 1, -1, -1):
        page = requests[i][0]
        nxt[i] = last.get(page, n + i)   # never again: beyond any real position, distinct per page
        last[page] = i
    resident = {}   # page -> its next use
    heap = []       # (-next use, page); stale entries skipped
    for i, (page, kind, op) in enumerate(requests):
        if page in resident:
            resident[page] = nxt[i]
            heapq.heappush(heap, (-nxt[i], page))
            continue
        res.miss(kind, op)
        if len(resident) >= slots:
            while True:
                neg, victim = heapq.heappop(heap)
                if resident.get(victim) == -neg:
                    del resident[victim]
                    break
        resident[page] = nxt[i]
        heapq.heappush(heap, (-nxt[i], page))
    return res


def gate_ring(requests, slots, ring, ghost=0):
    """
    The spike: an allowance of N/16 normal loads per operation, then the operation recycles its own last `ring` slots. Hits never cost allowance. With
    `ghost`, rule 5: the last `ghost` pages a ring recycled are remembered, and a miss on one from a different operation is a real re-reference — a normal
    load, outside the allowance.
    """
    res = Result(f"gate-ring {ring}{' +ghost' if ghost else ''}")
    allowance = max(1, slots // 16)
    page_of = [-1] * slots
    counter = [0] * slots
    where = {}
    loads = collections.Counter()
    rings = {}   # op -> deque of (slot, page) it loaded past its allowance
    ghosts = collections.OrderedDict()   # page -> the operation whose ring recycled it
    hand = 0
    used = 0
    for page, kind, op in requests:
        s = where.get(page)
        if s is not None:
            if kind in BUMPS and counter[s] < 5:
                counter[s] += 1
            continue
        res.miss(kind, op)
        recycledBy = ghosts.pop(page, None) if ghost else None
        rereference = recycledBy is not None and recycledBy != op
        if not rereference:
            loads[op] += 1
        transient = not rereference and loads[op] > allowance
        r = rings.setdefault(op, collections.deque())
        # A ring entry whose slot the sweep gave to another page since is no longer this operation's to recycle.
        while r and page_of[r[0][0]] != r[0][1]:
            r.popleft()
        if used < slots:
            s = used
            used += 1
        elif transient and len(r) >= ring:
            s, old = r.popleft()
            del where[old]
            if ghost:
                ghosts[old] = op
                if len(ghosts) > ghost:
                    ghosts.popitem(last=False)
        else:
            # The gate (rule 1): the adjacency path only at counter 0; then the sweep.
            prev = where.get(page - 1)
            s = None
            if prev is not None and counter[(prev + 1) % slots] == 0:
                s = (prev + 1) % slots
            if s is None:
                while counter[hand] > 0:
                    counter[hand] -= 1
                    hand = (hand + 1) % slots
                s = hand
                hand = (hand + 1) % slots
            del where[page_of[s]]
        page_of[s] = page
        where[page] = s
        if transient:
            counter[s] = 0   # transient: its own walk reuses the slot (rule 4: a walk's hit neither promotes nor demotes)
            r.append((s, page))
        else:
            counter[s] = 1 if kind in BUMPS else 0
    return res


def probation(requests, slots, area, ghost=0):
    """
    One shared probation area of `area` slots, FIFO, for every first load; promotion to the main clock when a different operation asks again. With
    `ghost`, rule 5: the last `ghost` pages the area let go are remembered, and a miss on one from a different operation loads it into the main clock.
    """
    res = Result(f"probation {area}{' +ghost' if ghost else ''}")
    ghosts = collections.OrderedDict()   # page -> the operation that loaded it
    main_slots = max(1, slots - area)
    fifo = collections.OrderedDict()   # page -> loading op
    page_of = [-1] * main_slots
    counter = [0] * main_slots
    where = {}
    hand = 0
    used = 0

    def to_main(page, kind):
        nonlocal hand, used
        if used < main_slots:
            s = used
            used += 1
        else:
            while counter[hand] > 0:
                counter[hand] -= 1
                hand = (hand + 1) % main_slots
            s = hand
            hand = (hand + 1) % main_slots
            del where[page_of[s]]
        page_of[s] = page
        where[page] = s
        counter[s] = 1 if kind in BUMPS else 0

    for page, kind, op in requests:
        s = where.get(page)
        if s is not None:
            if kind in BUMPS and counter[s] < 5:
                counter[s] += 1
            continue
        loader = fifo.get(page)
        if loader is not None:
            if loader != op:
                del fifo[page]
                to_main(page, kind)
            continue
        res.miss(kind, op)
        loadedBy = ghosts.pop(page, None) if ghost else None
        if loadedBy is not None and loadedBy != op:
            to_main(page, kind)
            continue
        if len(fifo) >= area:
            gone, goneOp = fifo.popitem(last=False)
            if ghost:
                ghosts[gone] = goneOp
                if len(ghosts) > ghost:
                    ghosts.popitem(last=False)
        fifo[page] = op
    return res


def adaptive(requests, slots, floor_div=256, cold_first=False, sample=0, promote_two_laps=False):
    """
    PS-25 as PagedMMF runs it (PagedMMF.Probation.cs): probation and the clock share the slots; a first load inside an operation goes to probation, FIFO,
    with a reference bit its own operation's request again sets (one more lap, at most 64 a take); a request from a different operation, or from outside
    any, promotes it. Two ghost lists of one entry per slot: a miss on a page probation let go grows the target p by max(1, |B2|/|B1|) and goes to the
    clock when another operation asks; one on a page the clock let go shrinks p and goes to the clock. p in [N/256, N/2], from N/16. Probation gives up
    its oldest page while it holds more than p, the clock otherwise. An operation is its id alone, as in the engine — a lease's chunks on several
    workers are one operation — and 0 is none. The adjacency path is not modelled.
    """
    res = Result("adaptive (PS-25)" + (f" floor N/{floor_div}" if floor_div != 256 else "") + (" cold-first" if cold_first else "")
                 + (" promote-2-laps" if promote_two_laps else ""))
    res.p_trace = []
    floor, ceiling = max(1, slots // floor_div), max(1, slots // 2)
    p = float(min(max(slots // 16, floor), ceiling))
    fifo = collections.OrderedDict()   # probation: page -> [loading op, requests by it: 1 the load's, 2 the bit]
    main = collections.deque()         # the clock's order: the left end is the hand
    counter = {}                       # clock pages -> 0..5
    b1 = collections.OrderedDict()     # ghosts probation let go: page -> loading op
    b2 = collections.OrderedDict()     # ghosts the clock let go

    def ghost(lst, page, value):
        lst.pop(page, None)
        lst[page] = value
        if len(lst) > slots:
            lst.popitem(last=False)

    def evict_one():
        # cold_first (a variant, not PS-25 as shipped): a clock page the sweep would take at once — counter 0, not asked for since the hand last passed —
        # goes before probation's oldest, so a walk can use the clock's idle pages instead of only its half.
        if cold_first and counter and fifo and len(fifo) > p and counter.get(main[0], 1) == 0:
            page = main.popleft()
            del counter[page]
            ghost(b2, page, 0)
            return
        if fifo and (len(fifo) > p or not counter):
            chances = min(len(fifo), 64)
            while True:
                page, entry = next(iter(fifo.items()))
                if promote_two_laps and entry[1] == 4:
                    del fifo[page]   # re-read by its own operation in two laps: the clock's
                    main.append(page)
                    counter[page] = 2
                    if not fifo or len(fifo) <= p:
                        break
                    continue
                if entry[1] in (2, 4) and chances > 0:
                    chances -= 1
                    entry[1] = 3 if promote_two_laps else 1
                    fifo.move_to_end(page)
                    continue
                del fifo[page]
                ghost(b1, page, entry[0])
                return
        while True:
            page = main.popleft()
            c = counter[page]
            if c > 0:
                counter[page] = c - 1
                main.append(page)
                continue
            del counter[page]
            ghost(b2, page, 0)
            return

    def to_main(page, kind, count=None):
        if count is None and len(fifo) + len(counter) >= slots:
            evict_one()
        main.append(page)
        counter[page] = count if count is not None else (1 if kind in BUMPS else 0)

    for i, (page, kind, (_, op)) in enumerate(requests):
        if sample and i % sample == 0:
            res.p_trace.append((i, int(p), len(fifo)))
        c = counter.get(page)
        if c is not None:
            if kind in BUMPS and c < 5:
                counter[page] = c + 1
            continue
        entry = fifo.get(page)
        if entry is not None:
            if kind in BUMPS:
                if op != 0 and entry[0] == op:
                    entry[1] = (2 if entry[1] < 2 else 4 if entry[1] == 3 else entry[1]) if promote_two_laps else min(2, entry[1] + 1)
                else:
                    del fifo[page]
                    to_main(page, kind, min(5, entry[1] + 1))   # a promotion takes no slot: the page only changes set
            continue
        res.miss(kind, (_, op))
        # The ghost decision first, then the eviction, as the engine's Place does (ARC's order).
        to_probation = op != 0
        if op == 0:
            b1.pop(page, None)
            b2.pop(page, None)
        elif page in b1:
            loader = b1.pop(page)
            p = min(ceiling, p + max(1.0, len(b2) / max(1, len(b1))))
            to_probation = loader == op
        elif page in b2:
            del b2[page]
            p = max(floor, p - max(1.0, len(b1) / max(1, len(b2))))
            to_probation = False
        if len(fifo) + len(counter) >= slots:
            evict_one()
        if to_probation:
            fifo[page] = [op, 1 if kind in BUMPS else 0]
        else:
            main.append(page)
            counter[page] = 1 if kind in BUMPS else 0
    res.target = int(p)
    return res


def replay(trace, slots, budgets, rings, fg_weight, ghosts=(0, 1), out=print, policies=None):
    reqs = trace.requests
    by_thread = collections.Counter(op[0] for _, _, op in reqs)
    Result.walk_thread = by_thread.most_common(1)[0][0] if by_thread else None
    fg = sum(1 for _, k, _ in reqs if k in FOREGROUND)
    out(f"{len(reqs):,} requests ({fg:,} epoch-tagged, {len(reqs) - fg:,} read pins), {trace.dropped:,} dropped, {slots:,} slots, "
        f"{len({p for p, _, _ in reqs}):,} distinct pages")
    out("requests by thread: " + ", ".join(f"{t}{' (walk)' if t == Result.walk_thread else ''} {n:,}" for t, n in by_thread.most_common(6)))
    types = collections.Counter(trace.page_types[p] if p < len(trace.page_types) else 0 for p, _, _ in reqs)
    out("requests by page type: " + ", ".join(f"{PAGE_TYPES[t] if t < len(PAGE_TYPES) else t} {n:,}" for t, n in types.most_common()))
    policies = policies or {"clock", "belady", "gate-ring", "probation", "adaptive"}
    results = [clock(reqs, slots), belady(reqs, slots)]   # both always: the first is the baseline, the second the reference
    if "gate-ring" in policies:
        for g in ghosts:
            results += [gate_ring(reqs, slots, r, g * slots) for r in rings]
    if "probation" in policies:
        for g in ghosts:
            for b in budgets:
                results.append(probation(reqs, slots, max(1, int(slots * b)), g * slots))
    if "adaptive" in policies:
        results.append(adaptive(reqs, slots))
        out(f"adaptive: final target p {results[-1].target:,} of {slots:,} slots")
    opt = results[1].cost(fg_weight)
    out(f"recorded misses {trace.recorded_misses:,}; today's clock replayed {results[0].total:,} "
        f"({(results[0].total - trace.recorded_misses) / max(1, trace.recorded_misses) * 100:+.1f} %)")
    out(f"{'policy':22}{'misses':>12}{'foreground':>12}{'walk':>12}{'cost':>14}{'vs belady':>11}")
    for r in results:
        out(f"{r.name:22}{r.total:>12,}{r.fg:>12,}{r.bg:>12,}{r.cost(fg_weight):>14,}{r.cost(fg_weight) / max(1, opt):>10.2f}x")
    return results


def self_test():
    """Hand-worked cases. Belady on the classic reference string; the clock on a string where its counters and the adjacency path decide."""
    # Belady, 3 frames, 1 2 3 4 1 2 5 1 2 3 4 5: the textbook answer is 7 misses.
    seq = [(p, 0, (1, 1)) for p in [1, 2, 3, 4, 1, 2, 5, 1, 2, 3, 4, 5]]
    assert belady(seq, 3).total == 7, belady(seq, 3).total

    # Clock, 2 slots, epoch requests on pages 10, 20, 10, 30, 10:
    #   10 miss -> slot 0 c=1; 20 miss -> slot 1 c=1; 10 hit -> c=2;
    #   30 miss: no page 29, sweep: slot 0 c=2->1, slot 1 c=1->0, slot 0 c=1->0, slot 1 is 0 -> take slot 1 (evicts 20), c=1;
    #   10 hit. Total 3 misses.
    seq = [(p, 0, (1, 1)) for p in [10, 20, 10, 30, 10]]
    assert clock(seq, 2).total == 3, clock(seq, 2).total

    # Adjacency: 2 slots, cold read pins on pages 5, 6, then 7. Page 6 is in slot 1, its next slot is slot 0 at counter 0: 7 takes it (evicts 5).
    # Then 6 is still resident: a hit. 3 misses.
    seq = [(p, 3, (1, 1)) for p in [5, 6, 7, 6]]
    assert clock(seq, 2).total == 3, clock(seq, 2).total

    # Probation, 1-slot area, 1 main slot: op A loads 1; op B asks 1 again -> promoted; op A loads 2 (area), op A asks 2 (same op: stays), op B asks 1 (main
    # hit). Misses: 1, 2 = 2.
    seq = [(1, 0, "A"), (1, 0, "B"), (2, 0, "A"), (2, 0, "A"), (1, 0, "B")]
    assert probation(seq, 2, 1).total == 2, probation(seq, 2, 1).total
    # Rule 5: 1-slot area and 1 main slot, a ghost list. Op A loads 1 then 2 (1 leaves the area for the ghosts); op B misses 1, a ghost loaded by
    # another op: straight to the main clock; B asks 1 again: a hit. Misses 1, 2, 1 = 3. Without the ghost, B's miss puts 1 back in the area, which
    # evicts 2, and B's second request is a re-reference by the same op: still in the area, a hit. Also 3, but 1 never reaches the main clock.
    seq = [(1, 0, "A"), (2, 0, "A"), (1, 0, "B"), (1, 0, "B")]
    assert probation(seq, 2, 1, ghost=4).total == 3

    # Adaptive, 4 slots (p = 1, ceiling 2), operation 7 then 8. Op 7 loads 1-4 (probation fills the cache) and 5 (evicts 1, the oldest); asks 2 again
    # (its bit); loads 6: 2 is the oldest but has its bit, goes round, 3 goes instead; 2 is still a hit. Op 8 misses 3, a ghost op 7 loaded: to the clock,
    # p grows; asks it again: a hit. Misses 1-6 and 3 = 7.
    a, b = (1, 7), (1, 8)
    seq = [(p, 0, a) for p in [1, 2, 3, 4, 5, 2, 6, 2]] + [(3, 0, b), (3, 0, b)]
    r = adaptive(seq, 4)
    assert (r.total, r.target) == (7, 2), (r.total, r.target)
    print("self-test: ok")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("trace", nargs="?")
    parser.add_argument("--slots", help="cache sizes to replay at, in slots, comma-separated (default: the captured cache's)")
    parser.add_argument("--fg-weight", type=int, default=10, help="cost of a foreground miss against the walk's 1")
    parser.add_argument("--op", choices=["operation", "thread"], default="operation",
                        help="what makes an operation: the recorded one (default) — the transaction the thread last created, a lease, else its pinned "
                             "epoch — or only the thread")
    parser.add_argument("--budgets", default="1/64,1/32,1/16,1/8", help="probation area sizes, as fractions of the cache")
    parser.add_argument("--rings", default="32,256", help="ring sizes for gate-ring")
    parser.add_argument("--ghosts", default="0,1", help="rule 5's memory of recycled pages, in caches: 0 = none, 1 = one entry per slot")
    parser.add_argument("--policies", default="gate-ring,probation,adaptive",
                        help="policies beside the clock and Belady, which always run: gate-ring, probation, adaptive (PS-25)")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.trace:
        parser.error("a trace file, or --self-test")
    trace = load(args.trace, args.op)
    budgets = [float(fractions.Fraction(b)) for b in args.budgets.split(",")]   # fractions like 1/16
    rings = [int(r) for r in args.rings.split(",")]
    for slots in [int(s) for s in str(args.slots).split(",")] if args.slots else [trace.slots]:
        replay(trace, slots, budgets, rings, args.fg_weight, [int(g) for g in args.ghosts.split(",")], policies=set(args.policies.split(",")))
        print()


if __name__ == "__main__":
    main()
