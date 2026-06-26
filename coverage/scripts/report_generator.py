#!/usr/bin/env python3
"""
Typhon Code Coverage Report Generator

Parses dotnet-coverage XML reports, records class-level coverage history in
JSONL, detects coverage regressions/improvements, generates SVG trend charts,
and produces a Markdown summary report.

Uses only the Python 3 standard library (no pip dependencies).

Usage:
    python3 coverage/scripts/report_generator.py \
        --coverage-xml <file> \
        --history <file> \
        --config <file> \
        --output-dir <dir> \
        --source-dir <dir> \
        --last-n <N> \
        --exit-code
"""

import argparse
import glob
import json
import math
import os
import platform
import re
import subprocess
import sys
import xml.etree.ElementTree as XET
from datetime import datetime, timezone


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(description="Typhon Code Coverage Report Generator")
    p.add_argument("--coverage-xml", default=None,
                   help="dotnet-coverage XML report file")
    p.add_argument("--history", default="coverage/history/results.jsonl",
                   help="JSONL history file (default: coverage/history/results.jsonl)")
    p.add_argument("--config", default="coverage/config.json",
                   help="Config file (default: coverage/config.json)")
    p.add_argument("--output-dir", default="coverage/reports",
                   help="Output directory (default: coverage/reports)")
    p.add_argument("--source-dir", default="src/Typhon.Engine",
                   help="Engine source directory for area scanning (default: src/Typhon.Engine)")
    p.add_argument("--last-n", type=int, default=0,
                   help="Only show last N runs in charts (0 = all)")
    p.add_argument("--exit-code", action="store_true",
                   help="Return non-zero exit code if regressions detected")
    return p.parse_args()


# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

def load_config(path):
    """Load config, returning sensible defaults if file is missing."""
    defaults = {
        "defaults": {
            "min_coverage_pct": 0.0,
            "regression_pct": 5.0,
            "improvement_pct": 5.0,
            "min_runs_for_trend": 2,
            "baseline_window": 3,
        },
        "source_dir": "src/Typhon.Engine",
        "area_order": [],
        "areas": {},
        "class_overrides": {},
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


def resolve_threshold(config, class_name, area, field):
    """Resolve a threshold value using: class_overrides > areas > defaults."""
    overrides = config.get("class_overrides", {})
    class_cfg = overrides.get(class_name, {})
    if field in class_cfg:
        return class_cfg[field]
    areas = config.get("areas", {})
    area_cfg = areas.get(area, {})
    if field in area_cfg:
        return area_cfg[field]
    return config["defaults"].get(field)


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
# Source Area Scanner
# ---------------------------------------------------------------------------

def scan_source_areas(source_dir):
    """Scan src/Typhon.Engine/**/*.cs to build className -> area mapping.

    For each .cs file, the area is the immediate parent directory name.
    Files in the root of source_dir get area "Root".

    Returns dict: {"AccessControl": "Concurrency", "BTree": "Data", ...}
    """
    area_map = {}
    source_dir = os.path.normpath(source_dir)

    for cs_file in glob.glob(os.path.join(source_dir, "**", "*.cs"), recursive=True):
        cs_file = os.path.normpath(cs_file)

        # Skip bin/obj directories anywhere in the path
        rel_path = os.path.relpath(cs_file, source_dir)
        parts = rel_path.replace("\\", "/").split("/")
        if any(p in ("bin", "obj") for p in parts):
            continue

        parent_dir = os.path.dirname(cs_file)
        filename = os.path.splitext(os.path.basename(cs_file))[0]

        if parent_dir == source_dir:
            area = "Root"
        else:
            area = os.path.basename(parent_dir)

        area_map[filename] = area

    return area_map


# ---------------------------------------------------------------------------
# Coverage XML Parser (dotnet-coverage XML format)
# ---------------------------------------------------------------------------

# Patterns for compiler-generated types that should be folded into their parent
_COMPILER_GENERATED_RE = re.compile(
    r'^<>c(?:__DisplayClass\d+_\d+)?$'  # <>c, <>c__DisplayClass0_0, etc.
    r'|^<\w+>d__\d+$'                   # <MethodName>d__0 (async state machine)
)


def _extract_parent_class(type_name):
    """Extract the outermost class name from a potentially nested/generic type.

    Examples:
        "AccessControl"                    -> "AccessControl"
        "AccessControl.LockData"           -> "AccessControl"
        "AccessControl.<>c"                -> "AccessControl"
        "BTree<TKey>"                      -> "BTree"
        "AccessControlSmall.<>c__DisplayClass20_0" -> "AccessControlSmall"
    """
    # Split on '.' to handle nested types (Microsoft XML uses '.' not '+')
    parts = type_name.split(".")
    base = parts[0]
    # Strip generic arity markers like `2 or <TKey>
    base = re.sub(r'`\d+', '', base)
    base = re.sub(r'<[^>]+>', '', base)
    return base


def _is_compiler_generated(type_name):
    """Check if a type_name (or its nested part) is compiler-generated."""
    parts = type_name.split(".")
    # The full type might be compiler generated
    if len(parts) == 1:
        return bool(_COMPILER_GENERATED_RE.match(parts[0]))
    # Or the nested part is compiler generated (parent.nested)
    return bool(_COMPILER_GENERATED_RE.match(parts[-1]))


def parse_coverage_xml(xml_path, area_map, class_overrides=None):
    """Parse dotnet-coverage XML report and extract class-level coverage.

    The XML format has: <results> > <modules> > <module> > <functions> > <function>
    Each <function> has attributes: namespace, type_name, blocks_covered, blocks_not_covered

    We:
    1. Find the Typhon.Engine.dll module
    2. Iterate all <function> elements
    3. Aggregate blocks by type (folding nested/compiler-generated types into parents)
    4. Map each class to its area via the source scanner

    Returns (results_list, summary_dict, tool_version).
    """
    if class_overrides is None:
        class_overrides = {}

    tree = XET.parse(xml_path)
    root = tree.getroot()

    # Find Typhon.Engine.dll module
    engine_module = None
    for mod in root.findall('.//module'):
        name = mod.get('name', '')
        if name == 'Typhon.Engine.dll':
            engine_module = mod
            break

    if engine_module is None:
        available = [m.get('name', '?') for m in root.findall('.//module')]
        print(f"Error: Could not find Typhon.Engine.dll module in coverage XML.")
        print(f"  Available modules: {available}")
        sys.exit(1)

    # Aggregate by class (folding nested types into parent)
    seen_classes = {}  # fullName -> {namespace, className, covered, total}

    for func in engine_module.findall('.//function'):
        namespace = func.get('namespace', '')
        type_name = func.get('type_name', '')
        blocks_covered = int(func.get('blocks_covered', 0))
        blocks_not_covered = int(func.get('blocks_not_covered', 0))

        # Extract parent class name
        class_name = _extract_parent_class(type_name)

        # Resolve area: class_overrides > area_map > "Unknown"
        if class_name in class_overrides and "area" in class_overrides[class_name]:
            area = class_overrides[class_name]["area"]
        else:
            area = area_map.get(class_name, "Unknown")

        full_name = f"{namespace}.{class_name}" if namespace else class_name

        if full_name in seen_classes:
            seen_classes[full_name]["coveredStatements"] += blocks_covered
            seen_classes[full_name]["totalStatements"] += blocks_covered + blocks_not_covered
        else:
            seen_classes[full_name] = {
                "namespace": namespace,
                "className": class_name,
                "fullName": full_name,
                "area": area,
                "coveredStatements": blocks_covered,
                "totalStatements": blocks_covered + blocks_not_covered,
                "coveragePercent": 0.0,
            }

    # Calculate percentages
    results = []
    for entry in seen_classes.values():
        total = entry["totalStatements"]
        covered = entry["coveredStatements"]
        entry["coveragePercent"] = round((covered / total * 100.0) if total > 0 else 0.0, 1)
        results.append(entry)

    # Summary
    total_statements = sum(r["totalStatements"] for r in results)
    covered_statements = sum(r["coveredStatements"] for r in results)
    coverage_pct = (covered_statements / total_statements * 100.0) if total_statements > 0 else 0.0

    summary = {
        "totalStatements": total_statements,
        "coveredStatements": covered_statements,
        "coveragePercent": round(coverage_pct, 1),
    }

    # Extract tool version from module-level coverage info
    module_coverage = engine_module.get('block_coverage', 'unknown')
    tool_version = f"dotnet-coverage (block coverage: {module_coverage}%)"

    return results, summary, tool_version


# ---------------------------------------------------------------------------
# Coverage Results Converter
# ---------------------------------------------------------------------------

def convert_coverage_results(xml_path, config, source_dir):
    """Parse coverage XML and build a complete JSONL run entry."""
    area_map = scan_source_areas(source_dir)
    class_overrides = config.get("class_overrides", {})

    results, summary, tool_version = parse_coverage_xml(xml_path, area_map, class_overrides)

    # Detect environment
    os_version = platform.platform()
    try:
        dn_result = subprocess.run(
            ["dotnet", "--version"],
            capture_output=True, text=True, timeout=10,
        )
        dotnet_version = dn_result.stdout.strip() if dn_result.returncode == 0 else "unknown"
    except Exception:
        dotnet_version = "unknown"

    try:
        dc_result = subprocess.run(
            ["dotnet-coverage", "--version"],
            capture_output=True, text=True, timeout=10,
        )
        coverage_tool_version = dc_result.stdout.strip() if dc_result.returncode == 0 else "unknown"
    except Exception:
        coverage_tool_version = "unknown"

    return {
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "gitCommit": get_git_commit(),
        "gitBranch": get_git_branch(),
        "environment": {
            "os": os_version,
            "dotnetVersion": dotnet_version,
            "coverageToolVersion": coverage_tool_version,
            "configuration": "Debug",
        },
        "summary": summary,
        "results": results,
    }


# ---------------------------------------------------------------------------
# History (JSONL)
# ---------------------------------------------------------------------------

def append_to_history(run_data, history_path):
    """Append a run_data dict as a single JSON line to the history file."""
    os.makedirs(os.path.dirname(history_path) or ".", exist_ok=True)
    with open(history_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(run_data, separators=(",", ":")) + "\n")
    print(f"Appended run ({len(run_data['results'])} classes) to {history_path}")


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


# ---------------------------------------------------------------------------
# Regression Detection
# ---------------------------------------------------------------------------

def analyze_changes(latest_run, all_history, config):
    """Analyze each class in the latest run for coverage regressions/improvements."""
    prior_runs = all_history[:-1] if len(all_history) > 1 else []

    # Index: fullName -> list of coveragePercent values in chronological order
    history_index = {}
    for run in prior_runs:
        for result in run.get("results", []):
            key = result["fullName"]
            history_index.setdefault(key, []).append(result["coveragePercent"])

    analysis = []
    for result in latest_run.get("results", []):
        key = result["fullName"]
        class_name = result.get("className", "")
        area = result.get("area", "")
        current = result["coveragePercent"]

        min_runs = int(resolve_threshold(config, class_name, area, "min_runs_for_trend"))
        baseline_window = int(resolve_threshold(config, class_name, area, "baseline_window"))
        regression_pct = float(resolve_threshold(config, class_name, area, "regression_pct"))
        improvement_pct = float(resolve_threshold(config, class_name, area, "improvement_pct"))
        min_coverage = float(resolve_threshold(config, class_name, area, "min_coverage_pct"))

        past_values = history_index.get(key, [])

        entry = {
            "fullName": result["fullName"],
            "className": class_name,
            "area": area,
            "current_pct": current,
            "coveredStatements": result.get("coveredStatements", 0),
            "totalStatements": result.get("totalStatements", 0),
            "min_coverage_pct": min_coverage,
        }

        if len(past_values) < min_runs:
            entry.update({
                "status": "insufficient_data",
                "baseline_pct": None,
                "change_pct": None,
                "threshold_pct": regression_pct,
            })
        else:
            window = past_values[-baseline_window:]
            baseline = sum(window) / len(window)
            # change_pct is the absolute difference in coverage percentage points
            change_pct = current - baseline

            if change_pct < -regression_pct:
                status = "regression"
            elif change_pct > improvement_pct:
                status = "improvement"
            else:
                status = "stable"

            entry.update({
                "status": status,
                "baseline_pct": round(baseline, 1),
                "change_pct": round(change_pct, 1),
                "threshold_pct": regression_pct,
            })

        # Check if below minimum coverage threshold
        entry["below_threshold"] = current < min_coverage if min_coverage > 0 else False

        analysis.append(entry)

    return analysis


# ---------------------------------------------------------------------------
# Format helpers
# ---------------------------------------------------------------------------

def _format_change(pct):
    """Format a percentage point change with sign."""
    if pct is None:
        return "N/A"
    sign = "+" if pct >= 0 else ""
    return f"{sign}{pct:.1f}pp"


def _safe_filename(text):
    """Replace unsafe characters for filenames."""
    return re.sub(r'[<>()\=, :]+', '_', text).strip('_')


# ---------------------------------------------------------------------------
# SVG Trend Chart Generator
# ---------------------------------------------------------------------------

# Chart constants (same dimensions as benchmark charts)
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


def generate_charts(all_history, analysis, config, output_dir, last_n):
    """Generate SVG trend charts: one overall + one per area."""
    charts_dir = os.path.join(output_dir, "charts")
    os.makedirs(charts_dir, exist_ok=True)

    # Wipe stale charts before regenerating: removed/renamed areas must not leave orphaned SVGs
    # behind, since the charts/ dir is now tracked in git (see ci-merge-gate design doc).
    for _stale in os.listdir(charts_dir):
        if _stale.endswith(".svg"):
            os.remove(os.path.join(charts_dir, _stale))

    chart_count = 0

    # Overall coverage trend
    overall_series = []
    for run in all_history:
        summary = run.get("summary", {})
        pct = summary.get("coveragePercent", 0.0)
        commit = run.get("gitCommit", "?")
        overall_series.append({"value": pct, "commit": commit})

    if last_n > 0 and len(overall_series) > last_n:
        overall_series = overall_series[-last_n:]

    if overall_series:
        svg_path = os.path.join(charts_dir, "overall_coverage.svg")
        _render_coverage_svg(overall_series, "Overall Coverage", svg_path, threshold=None)
        chart_count += 1

    # Per-area trend charts
    area_order = config.get("area_order", [])
    all_areas = set()
    for run in all_history:
        for result in run.get("results", []):
            all_areas.add(result.get("area", "Unknown"))

    ordered_areas = [a for a in area_order if a in all_areas]
    ordered_areas += sorted(a for a in all_areas if a not in area_order)

    for area in ordered_areas:
        series = []
        for run in all_history:
            # Aggregate coverage for this area across all classes in the run
            area_covered = 0
            area_total = 0
            for result in run.get("results", []):
                if result.get("area") == area:
                    area_covered += result.get("coveredStatements", 0)
                    area_total += result.get("totalStatements", 0)
            if area_total > 0:
                pct = area_covered / area_total * 100.0
            else:
                pct = 0.0
            commit = run.get("gitCommit", "?")
            series.append({"value": round(pct, 1), "commit": commit})

        if last_n > 0 and len(series) > last_n:
            series = series[-last_n:]

        if series:
            # Get threshold for this area
            area_cfg = config.get("areas", {}).get(area, {})
            threshold = area_cfg.get("min_coverage_pct", config["defaults"].get("min_coverage_pct", 0.0))
            if threshold <= 0:
                threshold = None

            filename = f"area_{_safe_filename(area)}.svg"
            svg_path = os.path.join(charts_dir, filename)
            _render_coverage_svg(series, f"Coverage: {area}", svg_path, threshold=threshold)
            chart_count += 1

    print(f"Generated {chart_count} chart(s) in {charts_dir}")


def _render_coverage_svg(series, title, svg_path, threshold=None):
    """Render a single SVG coverage trend chart. Y-axis is always 0-100%."""
    n = len(series)
    values = [s["value"] for s in series]
    commits = [s["commit"] for s in series]

    # Fixed Y-axis: 0-100%
    axis_min = 0.0
    axis_max = 100.0
    y_range = 100.0

    # Generate ticks at 0, 20, 40, 60, 80, 100
    ticks = [0.0, 20.0, 40.0, 60.0, 80.0, 100.0]

    def y_pos(val):
        return MARGIN_TOP + PLOT_H - ((val - axis_min) / y_range * PLOT_H)

    def x_pos(idx):
        if n == 1:
            return MARGIN_LEFT + PLOT_W / 2
        return MARGIN_LEFT + idx / (n - 1) * PLOT_W

    # Build SVG (using XET alias to avoid clash with ET used in benchmark)
    svg = XET.Element("svg", {
        "xmlns": "http://www.w3.org/2000/svg",
        "width": str(CHART_W),
        "height": str(CHART_H),
        "viewBox": f"0 0 {CHART_W} {CHART_H}",
    })

    # Background
    XET.SubElement(svg, "rect", {
        "x": "0", "y": "0",
        "width": str(CHART_W), "height": str(CHART_H),
        "fill": COLOR_BG,
    })

    # Title
    title_el = XET.SubElement(svg, "text", {
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
        XET.SubElement(svg, "line", {
            "x1": str(MARGIN_LEFT), "y1": f"{ty:.1f}",
            "x2": str(CHART_W - MARGIN_RIGHT), "y2": f"{ty:.1f}",
            "stroke": COLOR_GRID, "stroke-width": "1",
        })

    # Y-axis tick labels
    for tick in ticks:
        ty = y_pos(tick)
        label = XET.SubElement(svg, "text", {
            "x": str(MARGIN_LEFT - 8), "y": f"{ty + 4:.1f}",
            "text-anchor": "end",
            "font-family": FONT_FAMILY,
            "font-size": "11",
            "fill": "#666666",
        })
        label.text = f"{tick:.0f}%"

    # Threshold line (dashed orange)
    if threshold is not None and threshold > 0:
        ty = y_pos(threshold)
        XET.SubElement(svg, "line", {
            "x1": str(MARGIN_LEFT), "y1": f"{ty:.1f}",
            "x2": str(CHART_W - MARGIN_RIGHT), "y2": f"{ty:.1f}",
            "stroke": COLOR_ORANGE, "stroke-width": "1.5",
            "stroke-dasharray": "6,4",
        })
        tgt_label = XET.SubElement(svg, "text", {
            "x": str(CHART_W - MARGIN_RIGHT - 4),
            "y": f"{ty - 4:.1f}",
            "text-anchor": "end",
            "font-family": FONT_FAMILY,
            "font-size": "10",
            "fill": COLOR_ORANGE,
        })
        tgt_label.text = f"min: {threshold:.0f}%"

    # Fill area under the line (light blue)
    if n > 1:
        fill_points = []
        for i in range(n):
            fill_points.append(f"{x_pos(i):.1f},{y_pos(values[i]):.1f}")
        # Close the polygon along the bottom
        fill_points.append(f"{x_pos(n - 1):.1f},{y_pos(0):.1f}")
        fill_points.append(f"{x_pos(0):.1f},{y_pos(0):.1f}")
        XET.SubElement(svg, "polygon", {
            "points": " ".join(fill_points),
            "fill": COLOR_BLUE,
            "fill-opacity": "0.1",
        })

    # Data line
    if n > 1:
        points_str = " ".join(f"{x_pos(i):.1f},{y_pos(values[i]):.1f}" for i in range(n))
        XET.SubElement(svg, "polyline", {
            "points": points_str,
            "fill": "none",
            "stroke": COLOR_BLUE,
            "stroke-width": "2",
        })

    # Data points
    for i in range(n):
        cx = x_pos(i)
        cy = y_pos(values[i])

        # Color last point based on trend
        if i == n - 1 and n > 1:
            if values[i] < values[i - 1] - 2:
                color = COLOR_RED
            elif values[i] > values[i - 1] + 2:
                color = COLOR_GREEN
            else:
                color = COLOR_BLUE
        else:
            color = COLOR_BLUE

        XET.SubElement(svg, "circle", {
            "cx": f"{cx:.1f}", "cy": f"{cy:.1f}",
            "r": "4",
            "fill": color,
        })

        # Value label on each point
        val_label = XET.SubElement(svg, "text", {
            "x": f"{cx:.1f}", "y": f"{cy - 8:.1f}",
            "text-anchor": "middle",
            "font-family": FONT_FAMILY,
            "font-size": "9",
            "fill": "#666666",
        })
        val_label.text = f"{values[i]:.1f}%"

    # X-axis commit labels (rotated 45 degrees)
    for i in range(n):
        cx = x_pos(i)
        label_y = MARGIN_TOP + PLOT_H + 14
        lbl = XET.SubElement(svg, "text", {
            "x": f"{cx:.1f}", "y": f"{label_y:.1f}",
            "text-anchor": "end",
            "font-family": FONT_FAMILY,
            "font-size": "10",
            "fill": "#666666",
            "transform": f"rotate(-45, {cx:.1f}, {label_y:.1f})",
        })
        lbl.text = commits[i][:7]

    # Axes border lines
    XET.SubElement(svg, "line", {
        "x1": str(MARGIN_LEFT), "y1": str(MARGIN_TOP),
        "x2": str(MARGIN_LEFT), "y2": str(MARGIN_TOP + PLOT_H),
        "stroke": "#333333", "stroke-width": "1",
    })
    XET.SubElement(svg, "line", {
        "x1": str(MARGIN_LEFT), "y1": str(MARGIN_TOP + PLOT_H),
        "x2": str(CHART_W - MARGIN_RIGHT), "y2": str(MARGIN_TOP + PLOT_H),
        "stroke": "#333333", "stroke-width": "1",
    })

    # Write SVG
    tree = XET.ElementTree(svg)
    XET.indent(tree, space="  ")
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
    os_name = env.get("os", "unknown")
    dotnet = env.get("dotnetVersion", "unknown")
    coverage_tool = env.get("coverageToolVersion", "unknown")
    summary = latest_run.get("summary", {})

    regressions = [a for a in analysis if a["status"] == "regression"]
    improvements = [a for a in analysis if a["status"] == "improvement"]
    stable = [a for a in analysis if a["status"] == "stable"]
    insufficient = [a for a in analysis if a["status"] == "insufficient_data"]
    below_threshold = [a for a in analysis if a.get("below_threshold")]

    lines = []
    w = lines.append

    w("# Code Coverage Report")
    w("")
    w(f"**Date:** {timestamp}")
    w(f"**Commit:** {commit} ({branch})")
    w(f"**Environment:** {os_name} | .NET {dotnet} | dotnet-coverage {coverage_tool}")
    w("")

    # Overall summary
    w("## Overall Summary")
    w("")
    w("| Metric | Value |")
    w("|--------|-------|")
    w(f"| Total Statements | {summary.get('totalStatements', 0):,} |")
    w(f"| Covered Statements | {summary.get('coveredStatements', 0):,} |")
    w(f"| Coverage | **{summary.get('coveragePercent', 0):.1f}%** |")
    w("")

    w("![Overall Coverage](charts/overall_coverage.svg)")
    w("")

    # Area summary table
    w("## Coverage by Area")
    w("")
    area_stats = _compute_area_stats(analysis, config)
    area_order = config.get("area_order", [])
    ordered_area_names = [a for a in area_order if a in area_stats]
    ordered_area_names += sorted(a for a in area_stats if a not in area_order)

    w("| Area | Covered | Total | Coverage | Threshold | Status |")
    w("|------|---------|-------|----------|-----------|--------|")
    for area_name in ordered_area_names:
        stats = area_stats[area_name]
        threshold_str = f"{stats['threshold']:.0f}%" if stats["threshold"] > 0 else "-"
        if stats["threshold"] > 0 and stats["pct"] < stats["threshold"]:
            status_str = "Below"
        else:
            status_str = "OK"
        w(f"| {area_name} | {stats['covered']:,} | {stats['total']:,} | "
          f"{stats['pct']:.1f}% | {threshold_str} | {status_str} |")
    w("")

    # Regressions section
    w("## Regressions")
    w("")
    if regressions:
        w("> [!WARNING]")
        w(f"> {len(regressions)} class(es) show coverage regression")
        w("")
        w("| Class | Area | Current | Baseline | Change | Threshold |")
        w("|-------|------|---------|----------|--------|-----------|")
        for entry in sorted(regressions, key=lambda e: e.get("change_pct", 0)):
            name = entry["className"]
            area = entry["area"]
            current = f"{entry['current_pct']:.1f}%"
            baseline = f"{entry.get('baseline_pct', 0):.1f}%"
            change = _format_change(entry.get("change_pct"))
            threshold = f"{entry.get('threshold_pct', 0):.0f}pp"
            w(f"| {name} | {area} | {current} | {baseline} | {change} | {threshold} |")
        w("")
    else:
        w("No regressions detected.")
        w("")

    # Improvements section
    w("## Improvements")
    w("")
    if improvements:
        w("| Class | Area | Current | Baseline | Change |")
        w("|-------|------|---------|----------|--------|")
        for entry in sorted(improvements, key=lambda e: e.get("change_pct", 0), reverse=True):
            name = entry["className"]
            area = entry["area"]
            current = f"{entry['current_pct']:.1f}%"
            baseline = f"{entry.get('baseline_pct', 0):.1f}%"
            change = _format_change(entry.get("change_pct"))
            w(f"| {name} | {area} | {current} | {baseline} | {change} |")
        w("")
    else:
        w("No improvements detected.")
        w("")

    # Below threshold (collapsed)
    if below_threshold:
        w("<details>")
        w(f"<summary>Below Threshold ({len(below_threshold)})</summary>")
        w("")
        w("| Class | Area | Coverage | Threshold |")
        w("|-------|------|----------|-----------|")
        for entry in sorted(below_threshold, key=lambda e: e["current_pct"]):
            name = entry["className"]
            area = entry["area"]
            current = f"{entry['current_pct']:.1f}%"
            threshold = f"{entry['min_coverage_pct']:.0f}%"
            w(f"| {name} | {area} | {current} | {threshold} |")
        w("")
        w("</details>")
        w("")

    # All classes by area (collapsed)
    w("<details>")
    w(f"<summary>All Classes by Area ({len(analysis)})</summary>")
    w("")
    classes_by_area = {}
    for entry in analysis:
        area = entry.get("area", "Unknown")
        classes_by_area.setdefault(area, []).append(entry)

    for area_name in ordered_area_names:
        if area_name not in classes_by_area:
            continue
        entries = sorted(classes_by_area[area_name], key=lambda e: e["className"])
        w(f"#### {area_name}")
        w("")
        w("| Class | Covered | Total | Coverage | Status |")
        w("|-------|---------|-------|----------|--------|")
        for entry in entries:
            name = entry["className"]
            covered = entry.get("coveredStatements", 0)
            total = entry.get("totalStatements", 0)
            current = f"{entry['current_pct']:.1f}%"
            status = entry["status"]
            w(f"| {name} | {covered} | {total} | {current} | {status} |")
        w("")

    # Handle areas not in area_order
    for area_name in sorted(classes_by_area.keys()):
        if area_name in ordered_area_names:
            continue
        entries = sorted(classes_by_area[area_name], key=lambda e: e["className"])
        w(f"#### {area_name}")
        w("")
        w("| Class | Covered | Total | Coverage | Status |")
        w("|-------|---------|-------|----------|--------|")
        for entry in entries:
            name = entry["className"]
            covered = entry.get("coveredStatements", 0)
            total = entry.get("totalStatements", 0)
            current = f"{entry['current_pct']:.1f}%"
            status = entry["status"]
            w(f"| {name} | {covered} | {total} | {current} | {status} |")
        w("")

    w("</details>")
    w("")

    # Area trend charts
    w("## Area Trend Charts")
    w("")
    for area_name in ordered_area_names:
        filename = f"area_{_safe_filename(area_name)}.svg"
        w(f"### {area_name}")
        w(f"![Coverage: {area_name}](charts/{filename})")
        w("")

    with open(report_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print(f"Report written to {report_path}")


def _compute_area_stats(analysis, config):
    """Aggregate coverage stats per area."""
    areas = {}
    for entry in analysis:
        area = entry.get("area", "Unknown")
        if area not in areas:
            areas[area] = {"covered": 0, "total": 0}
        areas[area]["covered"] += entry.get("coveredStatements", 0)
        areas[area]["total"] += entry.get("totalStatements", 0)

    result = {}
    for area, stats in areas.items():
        pct = (stats["covered"] / stats["total"] * 100.0) if stats["total"] > 0 else 0.0
        area_cfg = config.get("areas", {}).get(area, {})
        threshold = area_cfg.get("min_coverage_pct", config["defaults"].get("min_coverage_pct", 0.0))
        result[area] = {
            "covered": stats["covered"],
            "total": stats["total"],
            "pct": round(pct, 1),
            "threshold": threshold,
        }
    return result


# ---------------------------------------------------------------------------
# Summary Printer
# ---------------------------------------------------------------------------

def print_summary(latest_run, analysis):
    """Print a concise console summary."""
    summary = latest_run.get("summary", {})
    regressions = [a for a in analysis if a["status"] == "regression"]
    improvements = [a for a in analysis if a["status"] == "improvement"]
    stable = [a for a in analysis if a["status"] == "stable"]
    insufficient = [a for a in analysis if a["status"] == "insufficient_data"]

    print("")
    print("=" * 60)
    print("  CODE COVERAGE REPORT")
    print("=" * 60)
    print(f"  Total Statements:    {summary.get('totalStatements', 0):,}")
    print(f"  Covered Statements:  {summary.get('coveredStatements', 0):,}")
    print(f"  Coverage:            {summary.get('coveragePercent', 0):.1f}%")
    print("-" * 60)
    print(f"  Regressions:         {len(regressions)}")
    print(f"  Improvements:        {len(improvements)}")
    print(f"  Stable:              {len(stable)}")
    print(f"  Insufficient Data:   {len(insufficient)}")
    print("=" * 60)

    if regressions:
        print("")
        print("  REGRESSIONS:")
        for entry in sorted(regressions, key=lambda e: e.get("change_pct", 0)):
            name = entry["className"]
            area = entry["area"]
            current = f"{entry['current_pct']:.1f}%"
            baseline = f"{entry.get('baseline_pct', 0):.1f}%"
            change = _format_change(entry.get("change_pct"))
            print(f"    {name} ({area}): {current} (was {baseline}, {change})")

    if improvements:
        print("")
        print("  IMPROVEMENTS:")
        for entry in sorted(improvements, key=lambda e: e.get("change_pct", 0), reverse=True):
            name = entry["className"]
            area = entry["area"]
            current = f"{entry['current_pct']:.1f}%"
            baseline = f"{entry.get('baseline_pct', 0):.1f}%"
            change = _format_change(entry.get("change_pct"))
            print(f"    {name} ({area}): {current} (was {baseline}, {change})")

    print("")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    args = parse_args()
    config = load_config(args.config)

    if args.coverage_xml:
        run_data = convert_coverage_results(args.coverage_xml, config, args.source_dir)
        append_to_history(run_data, args.history)

    history = load_history(args.history)
    if not history:
        print("No coverage history found.")
        return

    latest = history[-1]
    analysis = analyze_changes(latest, history, config)

    generate_charts(history, analysis, config, args.output_dir, args.last_n)
    generate_report(latest, analysis, config, args.output_dir)

    print_summary(latest, analysis)

    if args.exit_code:
        regressions = [a for a in analysis if a["status"] == "regression"]
        sys.exit(1 if regressions else 0)


if __name__ == "__main__":
    main()
