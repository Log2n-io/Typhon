#!/usr/bin/env python3
"""
Typhon Benchmark Report Generator

Converts BenchmarkDotNet JSON results into a JSONL history file, detects
regressions/improvements, generates SVG trend charts, and produces a
Markdown summary report.

Uses only the Python 3 standard library (no pip dependencies).

Usage:
    python3 benchmark/scripts/report_generator.py \
        --bdn-results <dir> \
        --history <file> \
        --config <file> \
        --output-dir <dir> \
        --last-n <N> \
        --exit-code
"""

import argparse
import glob
import json
import math
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(description="Typhon Benchmark Report Generator")
    p.add_argument("--bdn-results", default=None,
                   help="BenchmarkDotNet results directory containing *-report-full.json files")
    p.add_argument("--history", default="benchmark/history/results.jsonl",
                   help="JSONL history file (default: benchmark/history/results.jsonl)")
    p.add_argument("--config", default="benchmark/config.json",
                   help="Threshold config file (default: benchmark/config.json)")
    p.add_argument("--output-dir", default="benchmark/reports",
                   help="Output directory (default: benchmark/reports)")
    p.add_argument("--last-n", type=int, default=0,
                   help="Only show last N runs in charts (0 = all)")
    p.add_argument("--exit-code", action="store_true",
                   help="Return non-zero exit code if regressions detected")
    p.add_argument("--no-category-filter", action="store_true",
                   help="Disable Regression category filter (include all benchmarks from BDN JSON)")
    return p.parse_args()


# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

def load_config(path):
    """Load threshold config, returning sensible defaults if file is missing."""
    defaults = {
        "defaults": {
            "regression_pct": 15.0,
            "improvement_pct": 15.0,
            "min_runs_for_trend": 1,
            "min_measurable_ns": 1.0,
            "max_cov_pct": 30.0,
            "min_delta_ns": 0.5,
        },
        "categories": {},
        "benchmarks": {},
    }
    if not os.path.isfile(path):
        return defaults
    with open(path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    for key in defaults:
        cfg.setdefault(key, defaults[key])
    for key in defaults["defaults"]:
        cfg["defaults"].setdefault(key, defaults["defaults"][key])
    return cfg


def resolve_threshold(config, benchmark_type, method, category, field, parameters=""):
    """Resolve a threshold value using: benchmark(params) > benchmark > category > defaults."""
    benchmarks = config.get("benchmarks", {})
    # Try parameter-specific key first: Type.Method(Param=Value)
    if parameters:
        param_key = f"{benchmark_type}.{method}({parameters})"
        param_cfg = benchmarks.get(param_key, {})
        if field in param_cfg:
            return param_cfg[field]
    # Fall back to method-level key: Type.Method
    key = f"{benchmark_type}.{method}"
    bench_cfg = benchmarks.get(key, {})
    if field in bench_cfg:
        return bench_cfg[field]
    cat_cfg = config.get("categories", {}).get(category, {})
    if field in cat_cfg:
        return cat_cfg[field]
    return config["defaults"].get(field)


def resolve_target_ns(config, benchmark_type, method, parameters=""):
    """Return target_ns for a benchmark, or None if not configured."""
    benchmarks = config.get("benchmarks", {})
    # Try parameter-specific key first: Type.Method(Param=Value)
    if parameters:
        param_key = f"{benchmark_type}.{method}({parameters})"
        param_cfg = benchmarks.get(param_key, {})
        if "target_ns" in param_cfg:
            return param_cfg["target_ns"]
    # Fall back to method-level key: Type.Method
    key = f"{benchmark_type}.{method}"
    bench_cfg = benchmarks.get(key, {})
    return bench_cfg.get("target_ns")


# ---------------------------------------------------------------------------
# Git helpers
# ---------------------------------------------------------------------------

def _run_git(*args):
    try:
        result = subprocess.run(
            ["git"] + list(args),
            capture_output=True, text=True, timeout=10,
        )
        return result.stdout.strip() if result.returncode == 0 else ""
    except Exception:
        return ""


def get_git_commit():
    return _run_git("rev-parse", "--short", "HEAD") or "unknown"


def get_git_branch():
    return _run_git("rev-parse", "--abbrev-ref", "HEAD") or "unknown"


# ---------------------------------------------------------------------------
# BDN JSON Converter
# ---------------------------------------------------------------------------

def convert_bdn_results(results_dir, regression_only=True, type_categories=None):
    """Read all BDN *-report-full*.json files and produce a single run_data dict.

    Matches both *-report-full.json and *-report-full-compressed.json since
    newer BDN versions may only emit compressed variants.

    When regression_only is True (default) and a benchmark entry has a
    Categories field, entries without "Regression" are filtered out. BDN does
    not always export categories — when the field is absent the benchmark is
    included (rely on artifact cleanup to ensure only regression JSON files
    exist in the results directory).

    type_categories is an optional dict mapping Type name to category string,
    used as fallback since BDN does not export [BenchmarkCategory] to JSON.
    """
    # Match both compressed and uncompressed BDN JSON reports
    pattern_full = os.path.join(results_dir, "*-report-full.json")
    pattern_comp = os.path.join(results_dir, "*-report-full-compressed.json")
    full_files = set(glob.glob(pattern_full))
    comp_files = set(glob.glob(pattern_comp))

    # Prefer uncompressed when both exist for the same class; otherwise use compressed
    # Build base name -> filepath map (strip -compressed suffix for dedup)
    file_map = {}
    for fp in comp_files:
        base = os.path.basename(fp).replace("-report-full-compressed.json", "")
        file_map[base] = fp
    for fp in full_files:
        base = os.path.basename(fp).replace("-report-full.json", "")
        file_map[base] = fp  # overwrites compressed if both exist

    files = sorted(file_map.values())
    if not files:
        print(f"No *-report-full*.json files found in {results_dir}")
        sys.exit(1)

    environment = _extract_environment(files[0])
    benchmarks = []
    skipped = 0
    for filepath in files:
        with open(filepath, "r", encoding="utf-8") as f:
            data = json.load(f)
        for bm in data.get("Benchmarks", []):
            categories = bm.get("Categories")
            if regression_only and categories is not None and "Regression" not in categories:
                skipped += 1
                continue
            benchmarks.append(_map_benchmark(bm, type_categories))

    if skipped:
        print(f"Filtered out {skipped} non-regression benchmark(s)")

    return {
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "gitCommit": get_git_commit(),
        "gitBranch": get_git_branch(),
        "environment": environment,
        "results": benchmarks,
    }


def _extract_environment(filepath):
    """Extract host environment info from the first BDN JSON file."""
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)
    info = data.get("HostEnvironmentInfo", {})
    runtime = info.get("RuntimeVersion", "")
    # Extract version number like "10.0.1" from ".NET 10.0.1 (10.0.1, ...)"
    dot_net_ver = runtime.split("(")[0].replace(".NET", "").strip() if runtime else "unknown"
    return {
        "cpu": info.get("ProcessorName", "unknown"),
        "cores": info.get("LogicalCoreCount", 0),
        "os": info.get("OsVersion", "unknown"),
        "dotnetVersion": dot_net_ver,
        "configuration": info.get("Configuration", "RELEASE"),
    }


def _map_benchmark(bm, type_categories=None):
    """Map a single BDN benchmark entry to our result format.

    BDN does not export [BenchmarkCategory] to JSON, so the category is
    resolved from the config's type_categories mapping (Type -> Category)
    when the BDN Categories field is absent.
    """
    stats = bm.get("Statistics") or {}
    memory = bm.get("Memory") or {}
    categories = bm.get("Categories") or []

    # Try BDN Categories first (first entry that is NOT "Regression")
    category = ""
    for cat in categories:
        if cat != "Regression":
            category = cat
            break

    # Fallback: resolve from type_categories config mapping
    bm_type = bm.get("Type", "")
    if not category and type_categories:
        category = type_categories.get(bm_type, "")

    return {
        "fullName": bm.get("FullName", ""),
        "type": bm_type,
        "method": bm.get("Method", ""),
        "category": category,
        "mean_ns": stats.get("Mean", 0.0),
        "error_ns": stats.get("StandardError", 0.0),
        "stddev_ns": stats.get("StandardDeviation", stats.get("StdDev", 0.0)),
        "median_ns": stats.get("Median", 0.0),
        "allocated_bytes": memory.get("BytesAllocatedPerOperation", 0),
        "parameters": bm.get("Parameters", ""),
    }


# ---------------------------------------------------------------------------
# History (JSONL)
# ---------------------------------------------------------------------------

def append_to_history(run_data, history_path):
    """Append a run_data dict as a single JSON line to the history file."""
    os.makedirs(os.path.dirname(history_path) or ".", exist_ok=True)
    with open(history_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(run_data, separators=(",", ":")) + "\n")
    print(f"Appended run ({len(run_data['results'])} benchmarks) to {history_path}")


def load_history(history_path):
    """Load all runs from the JSONL history file."""
    if not os.path.isfile(history_path):
        return []
    runs = []
    with open(history_path, "r", encoding="utf-8") as f:
        for line_num, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                runs.append(json.loads(line))
            except json.JSONDecodeError as exc:
                print(f"Warning: skipping malformed line {line_num} in {history_path}: {exc}")
    return runs


def _backfill_categories(history, type_categories):
    """Fill in missing category fields on historical results using type_categories map.

    This handles history entries recorded before category resolution was added.
    Modifies runs in place (memory only — does not rewrite the JSONL file).
    """
    if not type_categories:
        return
    for run in history:
        for result in run.get("results", []):
            if not result.get("category"):
                result["category"] = type_categories.get(result.get("type", ""), "")


# ---------------------------------------------------------------------------
# Regression Detection
# ---------------------------------------------------------------------------

def _benchmark_key(result):
    """Unique key for a benchmark: fullName + parameters."""
    params = result.get("parameters", "")
    return (result["fullName"], params)


def analyze_regressions(latest_run, all_history, config):
    """Analyze each benchmark in the latest run against the immediately previous run.

    Noise filtering suppresses false positives from:
    - Sub-nanosecond measurements (below BDN's resolution)
    - High coefficient-of-variation benchmarks (inherently noisy)
    """
    # Build per-benchmark history (excluding the latest run itself)
    prior_runs = all_history[:-1] if len(all_history) > 1 else []

    # Index: key -> list of {mean_ns, stddev_ns} in chronological order
    history_index = {}
    for run in prior_runs:
        for result in run.get("results", []):
            key = _benchmark_key(result)
            history_index.setdefault(key, []).append({
                "mean_ns": result["mean_ns"],
                "stddev_ns": result.get("stddev_ns", 0.0),
            })

    analysis = []
    for result in latest_run.get("results", []):
        key = _benchmark_key(result)
        bm_type = result.get("type", "")
        method = result.get("method", "")
        category = result.get("category", "")
        parameters = result.get("parameters", "")
        current = result["mean_ns"]
        current_stddev = result.get("stddev_ns", 0.0)

        min_runs = int(resolve_threshold(config, bm_type, method, category, "min_runs_for_trend", parameters))
        regression_pct = float(resolve_threshold(config, bm_type, method, category, "regression_pct", parameters))
        improvement_pct = float(resolve_threshold(config, bm_type, method, category, "improvement_pct", parameters))
        min_measurable = float(resolve_threshold(config, bm_type, method, category, "min_measurable_ns", parameters))
        max_cov = float(resolve_threshold(config, bm_type, method, category, "max_cov_pct", parameters))
        min_delta = float(resolve_threshold(config, bm_type, method, category, "min_delta_ns", parameters))

        past_entries = history_index.get(key, [])

        entry = {
            "fullName": result["fullName"],
            "type": bm_type,
            "method": method,
            "category": category,
            "parameters": result.get("parameters", ""),
            "current_ns": current,
            "error_ns": result.get("error_ns", 0.0),
            "stddev_ns": current_stddev,
            "allocated_bytes": result.get("allocated_bytes", 0),
        }

        if len(past_entries) < min_runs:
            entry.update({
                "status": "insufficient_data",
                "previous_ns": None,
                "change_pct": None,
                "threshold_pct": regression_pct,
            })
        else:
            # Compare against the immediately previous run (trend, not averaged baseline)
            previous = past_entries[-1]["mean_ns"]
            change_pct = ((current - previous) / previous * 100) if previous != 0 else 0.0

            # Noise detection: CoV of current measurement, below resolution, or sub-ns absolute change
            cov_pct = (current_stddev / current * 100) if current > 0 else 0.0
            abs_delta = abs(current - previous)
            is_noisy = current < min_measurable or cov_pct > max_cov or abs_delta < min_delta

            if is_noisy:
                if current < min_measurable:
                    noise_reason = "below measurement resolution"
                elif cov_pct > max_cov:
                    noise_reason = f"high variance (CoV {cov_pct:.0f}%)"
                else:
                    noise_reason = f"abs delta {abs_delta:.2f}ns < {min_delta:.1f}ns threshold"
                status = "noisy"
            elif change_pct > regression_pct:
                status = "regression"
                noise_reason = None
            elif change_pct < -improvement_pct:
                status = "improvement"
                noise_reason = None
            else:
                status = "stable"
                noise_reason = None

            entry.update({
                "status": status,
                "previous_ns": previous,
                "change_pct": change_pct,
                "threshold_pct": regression_pct,
                "cov_pct": cov_pct,
                "noise_reason": noise_reason,
            })

        analysis.append(entry)

    return analysis


# ---------------------------------------------------------------------------
# Format helpers
# ---------------------------------------------------------------------------

def format_time(ns):
    """Format a time in nanoseconds with auto-selected unit."""
    if ns is None:
        return "N/A"
    abs_ns = abs(ns)
    if abs_ns < 1_000:
        return f"{ns:.2f} ns"
    elif abs_ns < 1_000_000:
        return f"{ns / 1_000:.2f} us"
    else:
        return f"{ns / 1_000_000:.2f} ms"


def _time_unit(ns_value):
    """Return (divisor, unit_label) for a nanosecond value."""
    abs_v = abs(ns_value) if ns_value else 0
    if abs_v < 1_000:
        return 1.0, "ns"
    elif abs_v < 1_000_000:
        return 1_000.0, "us"
    else:
        return 1_000_000.0, "ms"


def _safe_filename(text):
    """Replace characters unsafe in a filename OR in the Markdown link that points at it.

    `&` (and `#?%`) matter beyond the filesystem: the report embeds each chart as a Markdown
    image link, and DocFX resolves that link as a URL — an unescaped `&` splits it at a query
    string, so the chart 404s on doc.typhondb.io even though the file exists on disk.
    """
    return re.sub(r'[<>()=,&#?%\s]+', '_', text).strip('_')


def _display_name(entry):
    """Human-readable benchmark name, including parameters if present."""
    name = f"{entry['type']}.{entry['method']}"
    params = entry.get("parameters", "")
    if params:
        name += f" ({params})"
    return name


def _chart_filename(entry):
    """Generate a safe SVG filename for a benchmark."""
    params = entry.get("parameters", "")
    if params:
        return _safe_filename(f"{entry['type']}.{entry['method']}_{params}") + ".svg"
    return _safe_filename(f"{entry['type']}.{entry['method']}") + ".svg"


# ---------------------------------------------------------------------------
# SVG Trend Chart Generator
# ---------------------------------------------------------------------------

# Chart constants
CHART_W, CHART_H = 720, 360
MARGIN_TOP, MARGIN_RIGHT, MARGIN_BOTTOM, MARGIN_LEFT = 40, 30, 80, 80
PLOT_W = CHART_W - MARGIN_LEFT - MARGIN_RIGHT
PLOT_H = CHART_H - MARGIN_TOP - MARGIN_BOTTOM

COLOR_BLUE = "#4A90D9"
COLOR_RED = "#D94A4A"
COLOR_GREEN = "#4AD94A"
COLOR_ORANGE = "#FFA500"
COLOR_GRID = "#E0E0E0"
COLOR_BG = "#FFFFFF"
FONT_FAMILY = '"Segoe UI", Arial, sans-serif'


def _nice_number(value, round_up=False):
    """Find a 'nice' number close to value (1, 2, 5 multiples of powers of 10)."""
    if value <= 0:
        return 1.0
    exponent = math.floor(math.log10(value))
    fraction = value / (10 ** exponent)
    if round_up:
        if fraction <= 1.0:
            nice = 1.0
        elif fraction <= 2.0:
            nice = 2.0
        elif fraction <= 5.0:
            nice = 5.0
        else:
            nice = 10.0
    else:
        if fraction < 1.5:
            nice = 1.0
        elif fraction < 3.5:
            nice = 2.0
        elif fraction < 7.5:
            nice = 5.0
        else:
            nice = 10.0
    return nice * (10 ** exponent)


def _nice_ticks(data_min, data_max, max_ticks=6):
    """Generate nice tick values for the Y axis."""
    if data_min == data_max:
        data_min = data_min * 0.9 if data_min > 0 else -1.0
        data_max = data_max * 1.1 if data_max > 0 else 1.0
    data_range = data_max - data_min
    if data_range <= 0:
        data_range = 1.0
    tick_spacing = _nice_number(data_range / max(max_ticks - 1, 1))
    axis_min = math.floor(data_min / tick_spacing) * tick_spacing
    axis_max = math.ceil(data_max / tick_spacing) * tick_spacing
    # Ensure we don't go below zero for timing data
    if data_min >= 0:
        axis_min = max(0, axis_min)
    ticks = []
    val = axis_min
    while val <= axis_max + tick_spacing * 0.001:
        ticks.append(val)
        val += tick_spacing
    return ticks


def generate_charts(all_history, analysis, config, output_dir, last_n):
    """Generate one SVG chart per unique benchmark method."""
    charts_dir = os.path.join(output_dir, "charts")
    os.makedirs(charts_dir, exist_ok=True)

    # Wipe stale charts before regenerating: renamed/removed benchmarks must not leave orphaned
    # SVGs behind, since the charts/ dir is now tracked in git (see ci-merge-gate design doc).
    for _stale in os.listdir(charts_dir):
        if _stale.endswith(".svg"):
            os.remove(os.path.join(charts_dir, _stale))

    # Build analysis lookup for status coloring of last point
    analysis_by_key = {}
    for entry in analysis:
        key = (entry["fullName"], entry.get("parameters", ""))
        analysis_by_key[key] = entry

    # Discover all unique benchmarks across history
    benchmark_keys = {}  # key -> representative entry
    for run in all_history:
        for result in run.get("results", []):
            key = _benchmark_key(result)
            if key not in benchmark_keys:
                benchmark_keys[key] = result

    for key, representative in benchmark_keys.items():
        full_name, params = key
        # Gather time series from history
        series = []
        for run in all_history:
            for result in run.get("results", []):
                if _benchmark_key(result) == key:
                    series.append({
                        "mean_ns": result["mean_ns"],
                        "stddev_ns": result.get("stddev_ns", 0.0),
                        "commit": run.get("gitCommit", "?"),
                    })
                    break

        if not series:
            continue

        # Apply --last-n
        if last_n > 0 and len(series) > last_n:
            series = series[-last_n:]

        # Determine status for the last point
        a_entry = analysis_by_key.get(key)
        last_status = a_entry["status"] if a_entry else "stable"

        # Target line
        target_ns = resolve_target_ns(config, representative["type"], representative["method"], representative.get("parameters", ""))

        title = _display_name(representative)
        filename = _chart_filename(representative)
        svg_path = os.path.join(charts_dir, filename)

        _render_svg(series, title, svg_path, last_status, target_ns)

    print(f"Generated {len(benchmark_keys)} chart(s) in {charts_dir}")


def _render_svg(series, title, svg_path, last_status, target_ns):
    """Render a single SVG trend chart."""
    n = len(series)
    means = [s["mean_ns"] for s in series]
    stddevs = [s["stddev_ns"] for s in series]
    commits = [s["commit"] for s in series]

    # Determine Y range including error bars and target
    y_vals = []
    for m, sd in zip(means, stddevs):
        y_vals.append(m + sd)
        y_vals.append(m - sd)
    if target_ns is not None:
        y_vals.append(target_ns)

    y_min_data = max(0, min(y_vals)) if y_vals else 0
    y_max_data = max(y_vals) if y_vals else 1

    # Auto-select unit
    divisor, unit_label = _time_unit(y_max_data)

    # Convert to display units
    means_d = [m / divisor for m in means]
    stddevs_d = [s / divisor for s in stddevs]
    target_d = target_ns / divisor if target_ns is not None else None

    y_min_d = y_min_data / divisor
    y_max_d = y_max_data / divisor

    # Nice ticks
    ticks = _nice_ticks(y_min_d, y_max_d)
    axis_min = ticks[0]
    axis_max = ticks[-1]
    y_range = axis_max - axis_min if axis_max != axis_min else 1.0

    def y_pos(val):
        return MARGIN_TOP + PLOT_H - ((val - axis_min) / y_range * PLOT_H)

    def x_pos(idx):
        if n == 1:
            return MARGIN_LEFT + PLOT_W / 2
        return MARGIN_LEFT + idx / (n - 1) * PLOT_W

    # Build SVG
    svg = ET.Element("svg", {
        "xmlns": "http://www.w3.org/2000/svg",
        "width": str(CHART_W),
        "height": str(CHART_H),
        "viewBox": f"0 0 {CHART_W} {CHART_H}",
    })

    # Background
    ET.SubElement(svg, "rect", {
        "x": "0", "y": "0",
        "width": str(CHART_W), "height": str(CHART_H),
        "fill": COLOR_BG,
    })

    # Title
    title_el = ET.SubElement(svg, "text", {
        "x": str(CHART_W / 2), "y": str(MARGIN_TOP / 2 + 4),
        "text-anchor": "middle",
        "font-family": FONT_FAMILY,
        "font-size": "14",
        "font-weight": "bold",
        "fill": "#333333",
    })
    title_el.text = title

    # Grid lines
    for tick in ticks:
        ty = y_pos(tick)
        ET.SubElement(svg, "line", {
            "x1": str(MARGIN_LEFT), "y1": str(ty),
            "x2": str(CHART_W - MARGIN_RIGHT), "y2": str(ty),
            "stroke": COLOR_GRID, "stroke-width": "1",
        })

    # Y-axis tick labels
    for tick in ticks:
        ty = y_pos(tick)
        label = ET.SubElement(svg, "text", {
            "x": str(MARGIN_LEFT - 8), "y": str(ty + 4),
            "text-anchor": "end",
            "font-family": FONT_FAMILY,
            "font-size": "11",
            "fill": "#666666",
        })
        label.text = f"{tick:.2f}"

    # Y-axis unit label
    unit_el = ET.SubElement(svg, "text", {
        "x": str(MARGIN_LEFT - 8), "y": str(MARGIN_TOP - 8),
        "text-anchor": "end",
        "font-family": FONT_FAMILY,
        "font-size": "11",
        "fill": "#999999",
    })
    unit_el.text = f"({unit_label})"

    # Target line (dashed orange)
    if target_d is not None:
        ty = y_pos(target_d)
        if MARGIN_TOP <= ty <= MARGIN_TOP + PLOT_H:
            ET.SubElement(svg, "line", {
                "x1": str(MARGIN_LEFT), "y1": f"{ty:.1f}",
                "x2": str(CHART_W - MARGIN_RIGHT), "y2": f"{ty:.1f}",
                "stroke": COLOR_ORANGE, "stroke-width": "1.5",
                "stroke-dasharray": "6,4",
            })
            tgt_label = ET.SubElement(svg, "text", {
                "x": str(CHART_W - MARGIN_RIGHT - 4),
                "y": f"{ty - 4:.1f}",
                "text-anchor": "end",
                "font-family": FONT_FAMILY,
                "font-size": "10",
                "fill": COLOR_ORANGE,
            })
            tgt_label.text = f"target: {target_d:.2f} {unit_label}"

    # Data line
    if n > 1:
        points_str = " ".join(f"{x_pos(i):.1f},{y_pos(means_d[i]):.1f}" for i in range(n))
        ET.SubElement(svg, "polyline", {
            "points": points_str,
            "fill": "none",
            "stroke": COLOR_BLUE,
            "stroke-width": "2",
        })

    # Error bars and data points
    for i in range(n):
        cx = x_pos(i)
        cy = y_pos(means_d[i])

        # Error bar (vertical line: mean +/- stddev)
        if stddevs_d[i] > 0:
            err_top = y_pos(means_d[i] + stddevs_d[i])
            err_bot = y_pos(means_d[i] - stddevs_d[i])
            ET.SubElement(svg, "line", {
                "x1": f"{cx:.1f}", "y1": f"{err_top:.1f}",
                "x2": f"{cx:.1f}", "y2": f"{err_bot:.1f}",
                "stroke": COLOR_BLUE, "stroke-width": "1",
                "stroke-opacity": "0.5",
            })

        # Point color: only the last point gets status color
        if i == n - 1:
            if last_status == "regression":
                color = COLOR_RED
            elif last_status == "improvement":
                color = COLOR_GREEN
            else:
                color = COLOR_BLUE
        else:
            color = COLOR_BLUE

        ET.SubElement(svg, "circle", {
            "cx": f"{cx:.1f}", "cy": f"{cy:.1f}",
            "r": "4",
            "fill": color,
        })

    # X-axis commit labels (rotated 45 degrees)
    for i in range(n):
        cx = x_pos(i)
        label_y = MARGIN_TOP + PLOT_H + 14
        lbl = ET.SubElement(svg, "text", {
            "x": f"{cx:.1f}", "y": f"{label_y:.1f}",
            "text-anchor": "end",
            "font-family": FONT_FAMILY,
            "font-size": "10",
            "fill": "#666666",
            "transform": f"rotate(-45, {cx:.1f}, {label_y:.1f})",
        })
        lbl.text = commits[i][:7]

    # Axes border lines
    # Left axis
    ET.SubElement(svg, "line", {
        "x1": str(MARGIN_LEFT), "y1": str(MARGIN_TOP),
        "x2": str(MARGIN_LEFT), "y2": str(MARGIN_TOP + PLOT_H),
        "stroke": "#333333", "stroke-width": "1",
    })
    # Bottom axis
    ET.SubElement(svg, "line", {
        "x1": str(MARGIN_LEFT), "y1": str(MARGIN_TOP + PLOT_H),
        "x2": str(CHART_W - MARGIN_RIGHT), "y2": str(MARGIN_TOP + PLOT_H),
        "stroke": "#333333", "stroke-width": "1",
    })

    # Write SVG
    tree = ET.ElementTree(svg)
    ET.indent(tree, space="  ")
    tree.write(svg_path, encoding="unicode", xml_declaration=True)


# ---------------------------------------------------------------------------
# Markdown Report Generator
# ---------------------------------------------------------------------------

def generate_report(latest_run, analysis, config, output_dir):
    """Generate latest.md Markdown report."""
    os.makedirs(output_dir, exist_ok=True)
    report_path = os.path.join(output_dir, "latest.md")

    timestamp = latest_run.get("timestamp", "unknown")
    commit = latest_run.get("gitCommit", "unknown")
    branch = latest_run.get("gitBranch", "unknown")
    env = latest_run.get("environment", {})
    cpu = env.get("cpu", "unknown")
    os_name = env.get("os", "unknown")
    dotnet = env.get("dotnetVersion", "unknown")

    regressions = [a for a in analysis if a["status"] == "regression"]
    improvements = [a for a in analysis if a["status"] == "improvement"]
    stable = [a for a in analysis if a["status"] == "stable"]
    noisy = [a for a in analysis if a["status"] == "noisy"]
    insufficient = [a for a in analysis if a["status"] == "insufficient_data"]

    lines = []
    w = lines.append

    w("# Benchmark Regression Report")
    w("")
    w(f"**Date:** {timestamp}")
    w(f"**Commit:** {commit} ({branch})")
    w(f"**Environment:** {cpu} | {os_name} | .NET {dotnet}")
    w("")

    # Summary table
    w("## Summary")
    w("")
    w("| Status | Count |")
    w("|--------|-------|")
    w(f"| Regression | {len(regressions)} |")
    w(f"| Improvement | {len(improvements)} |")
    w(f"| Stable | {len(stable)} |")
    w(f"| Noisy (filtered) | {len(noisy)} |")
    w(f"| Insufficient Data | {len(insufficient)} |")
    w("")

    # Regressions section
    w("## Regressions")
    w("")
    if regressions:
        w("> [!WARNING]")
        w(f"> {len(regressions)} benchmark(s) show performance regression")
        w("")
        w("| Benchmark | Current | Previous | Change | Threshold |")
        w("|-----------|---------|----------|--------|-----------|")
        for entry in _sorted_by_change(regressions, reverse=True):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            threshold = f"{entry.get('threshold_pct', 0):.0f}%"
            w(f"| {name} | {current} | {previous} | {change} | {threshold} |")
        w("")
        # Inline charts for regressions
        for entry in _sorted_by_change(regressions, reverse=True):
            chart_file = _chart_filename(entry)
            w(f"![{_display_name(entry)}](charts/{chart_file})")
            w("")
    else:
        w("No regressions detected.")
        w("")

    # Improvements section
    w("## Improvements")
    w("")
    if improvements:
        w("| Benchmark | Current | Previous | Change |")
        w("|-----------|---------|----------|--------|")
        for entry in _sorted_by_change(improvements, reverse=False):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            w(f"| {name} | {current} | {previous} | {change} |")
        w("")
    else:
        w("No improvements detected.")
        w("")

    # Stable (collapsed)
    w("<details>")
    w(f"<summary>Stable Benchmarks ({len(stable)})</summary>")
    w("")
    if stable:
        w("| Benchmark | Current | Previous | Change |")
        w("|-----------|---------|----------|--------|")
        for entry in sorted(stable, key=lambda e: _display_name(e)):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            w(f"| {name} | {current} | {previous} | {change} |")
        w("")
    else:
        w("No stable benchmarks.")
        w("")
    w("</details>")
    w("")

    # Noisy (collapsed)
    w("<details>")
    w(f"<summary>Noisy Benchmarks ({len(noisy)}) — filtered from regression detection</summary>")
    w("")
    if noisy:
        w("| Benchmark | Current | Previous | Change | Reason |")
        w("|-----------|---------|----------|--------|--------|")
        for entry in sorted(noisy, key=lambda e: abs(e.get("change_pct", 0) or 0), reverse=True):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            reason = entry.get("noise_reason", "")
            w(f"| {name} | {current} | {previous} | {change} | {reason} |")
        w("")
    else:
        w("No noisy benchmarks.")
        w("")
    w("</details>")
    w("")

    # Insufficient data (collapsed)
    w("<details>")
    w(f"<summary>Insufficient Data ({len(insufficient)})</summary>")
    w("")
    if insufficient:
        w("| Benchmark | Current |")
        w("|-----------|---------|")
        for entry in sorted(insufficient, key=lambda e: _display_name(e)):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            w(f"| {name} | {current} |")
        w("")
    else:
        w("No benchmarks with insufficient data.")
        w("")
    w("</details>")
    w("")

    # Trend charts section, grouped by category
    w("## Trend Charts")
    w("")
    categories = {}
    for entry in analysis:
        cat = entry.get("category", "") or "Uncategorized"
        categories.setdefault(cat, []).append(entry)

    # Use configured category order, with any unconfigured categories appended alphabetically
    category_order = config.get("category_order", [])
    ordered_cats = [c for c in category_order if c in categories]
    ordered_cats += sorted(c for c in categories if c not in category_order)

    for cat_name in ordered_cats:
        entries = sorted(categories[cat_name], key=lambda e: _display_name(e))
        w(f"### Category: {cat_name}")
        for entry in entries:
            chart_file = _chart_filename(entry)
            display = _display_name(entry)
            w(f"![{display}](charts/{chart_file})")
            w("")

    with open(report_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print(f"Report written to {report_path}")


def _format_change(pct):
    """Format a percentage change with sign."""
    if pct is None:
        return "N/A"
    sign = "+" if pct >= 0 else ""
    return f"{sign}{pct:.1f}%"


def _sorted_by_change(entries, reverse=False):
    """Sort analysis entries by absolute change percentage."""
    return sorted(entries, key=lambda e: abs(e.get("change_pct", 0) or 0), reverse=reverse)


# ---------------------------------------------------------------------------
# Summary Printer
# ---------------------------------------------------------------------------

def print_summary(analysis):
    """Print a concise console summary."""
    regressions = [a for a in analysis if a["status"] == "regression"]
    improvements = [a for a in analysis if a["status"] == "improvement"]
    stable = [a for a in analysis if a["status"] == "stable"]
    noisy = [a for a in analysis if a["status"] == "noisy"]
    insufficient = [a for a in analysis if a["status"] == "insufficient_data"]

    print("")
    print("=" * 60)
    print("  BENCHMARK REGRESSION REPORT")
    print("=" * 60)
    print(f"  Regressions:       {len(regressions)}")
    print(f"  Improvements:      {len(improvements)}")
    print(f"  Stable:            {len(stable)}")
    print(f"  Noisy (filtered):  {len(noisy)}")
    print(f"  Insufficient Data: {len(insufficient)}")
    print("=" * 60)

    if regressions:
        print("")
        print("  REGRESSIONS:")
        for entry in _sorted_by_change(regressions, reverse=True):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            print(f"    {name}: {current} (was {previous}, {change})")

    if improvements:
        print("")
        print("  IMPROVEMENTS:")
        for entry in _sorted_by_change(improvements, reverse=True):
            name = _display_name(entry)
            current = format_time(entry["current_ns"])
            previous = format_time(entry.get("previous_ns"))
            change = _format_change(entry.get("change_pct"))
            print(f"    {name}: {current} (was {previous}, {change})")

    if noisy:
        print("")
        print(f"  NOISY ({len(noisy)} filtered):")
        for entry in sorted(noisy, key=lambda e: abs(e.get("change_pct", 0) or 0), reverse=True):
            name = _display_name(entry)
            reason = entry.get("noise_reason", "")
            print(f"    {name}: {reason}")

    print("")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    args = parse_args()
    config = load_config(args.config)

    type_categories = config.get("type_categories", {})

    if args.bdn_results:
        regression_only = not args.no_category_filter
        run_data = convert_bdn_results(args.bdn_results, regression_only=regression_only,
                                       type_categories=type_categories)
        append_to_history(run_data, args.history)

    history = load_history(args.history)
    if not history:
        print("No benchmark history found.")
        return

    # Backfill categories on history entries that were saved without them
    _backfill_categories(history, type_categories)

    latest = history[-1]
    analysis = analyze_regressions(latest, history, config)

    generate_charts(history, analysis, config, args.output_dir, args.last_n)
    generate_report(latest, analysis, config, args.output_dir)

    print_summary(analysis)

    if args.exit_code:
        regressions = [a for a in analysis if a["status"] == "regression"]
        sys.exit(1 if regressions else 0)


if __name__ == "__main__":
    main()
