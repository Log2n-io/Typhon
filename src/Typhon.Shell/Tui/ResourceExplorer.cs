using System;
using System.Linq;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Typhon.Shell.Session;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Typhon.Shell.Tui;

/// <summary>
/// Full-screen interactive Terminal.Gui resource tree explorer with a modern dark theme.
/// Left pane: hierarchical resource tree. Right pane: detail panel for the selected node.
/// Bottom bar: status line with keyboard shortcuts.
/// Press q or Esc to exit back to the REPL.
/// </summary>
internal sealed class ResourceExplorer
{
    private readonly ShellSession _session;

    public ResourceExplorer(ShellSession session)
    {
        _session = session;
    }

    public void Run()
    {
        using var app = Application.Create().Init();
        
        {
            // ── One Dark Theme ────────────────────────────────────
            // Colors from Atom's One Dark Syntax theme (HSL→RGB converted).
            // https://github.com/atom/one-dark-syntax

            var bg     = new Color(0x28, 0x2C, 0x34);  // #282c34  syntax-bg
            var gutter = new Color(0x21, 0x25, 0x2B);  // #21252b  gutter/panel bg
            var fg     = new Color(0xAB, 0xB2, 0xBF);  // #abb2bf  mono-1 (foreground)
            var dim    = new Color(0x5C, 0x63, 0x70);  // #5c6370  mono-3 (comments/dim)
            var cyan   = new Color(0x56, 0xB6, 0xC2);  // #56b6c2  hue-1
            var blue   = new Color(0x61, 0xAF, 0xEF);  // #61afef  hue-2
            var white  = new Color(0xE6, 0xE6, 0xE6);  // #e6e6e6  bright white

            var darkScheme = new Scheme
            {
                Normal    = new Attribute(fg, bg),
                Focus     = new Attribute(white, bg),
                HotNormal = new Attribute(blue, bg),
                HotFocus  = new Attribute(blue, bg),
                Disabled  = new Attribute(dim, bg)
            };

            var treeScheme = new Scheme
            {
                Normal    = new Attribute(fg, bg),
                Focus     = new Attribute(bg, blue),
                HotNormal = new Attribute(cyan, bg),
                HotFocus  = new Attribute(bg, blue),
                Disabled  = new Attribute(dim, bg)
            };

            var statusScheme = new Scheme
            {
                Normal    = new Attribute(fg, gutter),
                Focus     = new Attribute(white, gutter),
                HotNormal = new Attribute(blue, gutter),
                HotFocus  = new Attribute(blue, gutter),
                Disabled  = new Attribute(dim, gutter)
            };

            // ── Layout ───────────────────────────────────────────

            var win = new Window
            {
                Title = $"Typhon Resource Explorer \u2014 {_session.DatabaseName}",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };
            win.SetScheme(darkScheme);

            // ── Left pane: resource tree ─────────────────────────

            var leftPane = new FrameView
            {
                Title = "Resources",
                X = 0,
                Y = 0,
                Width = Dim.Percent(35),
                Height = Dim.Fill(1), // leave 1 row for status bar
                CanFocus = true,
                TabStop = TabBehavior.TabGroup,
            };
            leftPane.SetScheme(darkScheme);

            var treeView = new TreeView<IResource>
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                TreeBuilder = new DelegateTreeBuilder<IResource>(
                    r => r.Children ?? Enumerable.Empty<IResource>(),
                    r => r.Children != null && r.Children.Any()
                ),
                AspectGetter = FormatTreeNode
            };
            treeView.SetScheme(treeScheme);

            // Populate from the resource registry
            var registry = _session.ResourceRegistry;
            if (registry.Root != null)
            {
                treeView.AddObject(registry.Root);
                treeView.ExpandAll();
            }

            leftPane.Add(treeView);

            // ── Right pane: detail view ──────────────────────────

            var rightPane = new FrameView
            {
                Title = "Details",
                X = Pos.Right(leftPane),
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1), // leave 1 row for status bar
                CanFocus = true,
                TabStop = TabBehavior.TabGroup,
            };
            rightPane.SetScheme(darkScheme);

            var detailView = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = true,
                CanFocus = true,
                Text = " Select a resource node to inspect.\n\n" +
                       " Use \u2191\u2193 to navigate, \u2192 to expand, \u2190 to collapse.\n" +
                       " Press r to refresh, a to toggle auto-refresh (5 Hz),\n" +
                       " q or Esc to return to the shell."
            };
            detailView.SetScheme(darkScheme);

            rightPane.Add(detailView);

            // ── Status bar at bottom ─────────────────────────────

            var autoRefreshShortcut = new Shortcut { Key = Key.A, Text = "Auto [OFF]" };

            var statusBar = new StatusBar(
            [
                new Shortcut
                {
                    Text = "Navigate",
                    HelpText = "\u2191\u2193",
                    Key = Key.Empty,
                },
                new Shortcut
                {
                    Text = "Switch Pane",
                    HelpText = "F6",
                    Key = Key.Empty,
                },
                new Shortcut { Key = Key.R, Text = "Refresh" },
                autoRefreshShortcut,
                new Shortcut { Key = Key.Q, Text = "Quit" },
            ]);
            statusBar.SetScheme(statusScheme);

            // ── Selection handler ────────────────────────────────

            treeView.SelectionChanged += (_, _) =>
            {
                var resource = treeView.SelectedObject;
                if (resource != null)
                {
                    detailView.Text = BuildDetailText(resource);
                }
            };

            win.Add(leftPane);
            win.Add(rightPane);
            win.Add(statusBar);

            // ── Auto-refresh state ──────────────────────────────
            object autoRefreshToken = null;

            void StartAutoRefresh()
            {
                if (autoRefreshToken != null)
                {
                    return;
                }

                autoRefreshToken = app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
                {
                    var selected = treeView.SelectedObject;
                    if (selected is IMetricSource)
                    {
                        detailView.Text = BuildDetailText(selected);
                    }

                    return true; // keep repeating
                });

                autoRefreshShortcut.Text = "Auto [ON]";
            }

            void StopAutoRefresh()
            {
                if (autoRefreshToken != null)
                {
                    app.RemoveTimeout(autoRefreshToken);
                    autoRefreshToken = null;
                }

                autoRefreshShortcut.Text = "Auto [OFF]";
            }

            // ── Global key bindings (app.Keyboard.KeyDown fires before any view) ──

            app.Keyboard.KeyDown += (_, key) =>
            {
                if (key.Handled)
                {
                    return;
                }

                if (key == Key.Q || key == Key.Q.WithShift || key == Key.Esc)
                {
                    StopAutoRefresh();
                    app.RequestStop();
                    key.Handled = true;
                }
                else if (key == Key.R || key == Key.R.WithShift)
                {
                    var selected = treeView.SelectedObject;
                    if (selected != null)
                    {
                        detailView.Text = BuildDetailText(selected);
                    }
                    key.Handled = true;
                }
                else if (key == Key.A || key == Key.A.WithShift)
                {
                    if (autoRefreshToken != null)
                    {
                        StopAutoRefresh();
                    }
                    else
                    {
                        StartAutoRefresh();
                    }
                    key.Handled = true;
                }
            };

            app.Run(win);
            win.Dispose();
        }
    }

    // ── Tree rendering ───────────────────────────────────────

    private static string FormatTreeNode(IResource r)
    {
        var icon = r.Type switch
        {
            // Structural
            ResourceType.Node            => "\u25cb",  // ○
            // Service layer
            ResourceType.Service         => "\u25a0",  // ■
            ResourceType.Engine          => "\u25c6",  // ◆
            // Transaction layer
            ResourceType.TransactionPool => "\u25ce",  // ◎
            ResourceType.Transaction     => "\u25cf",  // ●
            ResourceType.ChangeSet       => "\u25b3",  // △
            // Storage layer
            ResourceType.ComponentTable  => "\u25a3",  // ▣
            ResourceType.Segment         => "\u25aa",  // ▪
            ResourceType.Index           => "\u25c7",  // ◇
            ResourceType.Cache           => "\u25a6",  // ▦
            // Persistence layer
            ResourceType.File            => "\u25a1",  // □
            ResourceType.Memory          => "\u2588",  // █
            ResourceType.Bitmap          => "\u2593",  // ▓
            // Metadata
            ResourceType.Schema          => "\u25a4",  // ▤
            // Utility
            ResourceType.Allocator       => "\u25a7",  // ▧
            // Durability layer
            ResourceType.WAL             => "\u25b6",  // ▶
            ResourceType.Checkpoint      => "\u25c8",  // ◈
            ResourceType.Backup          => "\u25b7",  // ▷
            _                            => "\u00b7"   // ·
        };

        return $"{icon} {r.Id}";
    }

    // ── Memory helpers ─────────────────────────────────────

    private static (int count, long bytes) SumMemory(IResource root)
    {
        int count = 0;
        long bytes = 0;

        if (root is IMemoryResource mem)
        {
            count++;
            bytes += mem.EstimatedMemorySize;
        }

        if (root.Children == null)
        {
            return (count, bytes);
        }

        foreach (var child in root.Children)
        {
            var (c, b) = SumMemory(child);
            count += c;
            bytes += b;
        }

        return (count, bytes);
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024 * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024               => $"{bytes / 1024.0:F1} KB",
            _                     => $"{bytes} B"
        };

    // ── Detail panel ─────────────────────────────────────────

    private string BuildDetailText(IResource resource)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($" {resource.Id}");
        sb.AppendLine($" {"".PadRight(Math.Min(resource.Id.Length + 1, 40), '\u2500')}");
        sb.AppendLine();

        // Properties
        sb.AppendLine($" Type:       {resource.Type}");
        sb.AppendLine($" Created:    {resource.CreatedAt:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($" Parent:     {resource.Parent?.Id ?? "(root)"}");

        var childCount = resource.Children?.Count() ?? 0;
        sb.AppendLine($" Children:   {childCount}");

        // Memory summary — aggregate IMemoryResource sizes in subtree
        var (memCount, memBytes) = SumMemory(resource);
        if (memBytes > 0)
        {
            sb.AppendLine($" Memory:     {FormatBytes(memBytes)} across {memCount} block{(memCount != 1 ? "s" : "")}");
        }

        sb.AppendLine();

        // Metrics
        if (resource is IMetricSource metricSource)
        {
            sb.AppendLine(" \u2550 Metrics \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
            var writer = new StringMetricWriter();
            metricSource.ReadMetrics(writer);
            sb.Append(writer.ToString());
            sb.AppendLine();
        }

        // Debug properties
        if (resource is IDebugPropertiesProvider debugProps)
        {
            var props = debugProps.GetDebugProperties();
            if (props.Count > 0)
            {
                sb.AppendLine(" \u2550 Properties \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");

                // Find longest key for alignment
                var maxKeyLen = props.Keys.Max(k => k.Length);

                foreach (var kvp in props)
                {
                    sb.AppendLine($"   {kvp.Key.PadRight(maxKeyLen)}  {kvp.Value}");
                }
            }
        }

        // Children summary
        if (childCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine(" \u2550 Children \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
            foreach (var child in resource.Children.Take(20))
            {
                var typeTag = child.Type != ResourceType.None ? $" [{child.Type}]" : "";
                sb.AppendLine($"   {child.Id}{typeTag}");
            }

            if (childCount > 20)
            {
                sb.AppendLine($"   ... and {childCount - 20} more");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collects metric writer calls into formatted strings for the detail panel.
    /// </summary>
    private sealed class StringMetricWriter : IMetricWriter
    {
        private readonly StringBuilder _sb = new();

        public void WriteCapacity(long current, long maximum)
        {
            var pct = maximum > 0 ? (double)current / maximum : 0;
            var progressBar = BuildBar(pct, 20);
            _sb.AppendLine($"   Capacity:    {progressBar} {pct:P1}  ({current:N0} / {maximum:N0})");
        }

        public void WriteMemory(long allocatedBytes, long peakBytes) =>
            _sb.AppendLine($"   Memory:      {FormatBytes(allocatedBytes),-10} (peak {FormatBytes(peakBytes)})");

        public void WriteThroughput(string label, long count) =>
            _sb.AppendLine($"   {label + ":",-16} {count:N0}");

        public void WriteDuration(string label, long lastUs, long avgUs, long maxUs) =>
            _sb.AppendLine($"   {label + ":",-16} last={FormatUs(lastUs)}  avg={FormatUs(avgUs)}  max={FormatUs(maxUs)}");

        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes) =>
            _sb.AppendLine($"   Disk I/O:    R={readOps:N0} ({FormatBytes(readBytes)})  W={writeOps:N0} ({FormatBytes(writeBytes)})");

        public override string ToString() => _sb.ToString();

        private static string FormatBytes(long bytes) =>
            bytes switch
            {
                >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
                >= 1024 * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
                >= 1024               => $"{bytes / 1024.0:F1} KB",
                _                     => $"{bytes} B"
            };

        private static string FormatUs(long us) =>
            us switch
            {
                >= 1_000_000 => $"{us / 1_000_000.0:F2}s",
                >= 1_000     => $"{us / 1_000.0:F1}ms",
                _            => $"{us}us"
            };

        private static string BuildBar(double pct, int width)
        {
            var filled = (int)(pct * width);
            if (filled > width)
            {
                filled = width;
            }

            return "[" + new string('\u2588', filled) + new string('\u2500', width - filled) + "]";
        }
    }
}
