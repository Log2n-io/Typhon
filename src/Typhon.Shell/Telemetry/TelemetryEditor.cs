using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Typhon.Engine;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Typhon.Shell.Telemetry;

/// <summary>
/// Full-screen interactive editor for <c>typhon.telemetry.json</c> (Feature #522 / T6). Renders the flag catalog
/// as a tri-state tree — each node is Explicit-On <c>[+]</c>, Explicit-Off <c>[-]</c> or Inherit <c>[·]</c> — with a
/// live effective-state column (<c>●</c>/<c>○</c>) that honours the resolver's parent-implies-children semantics
/// and the un-gated-gauge exceptions. Effective-on nodes are green, explicitly-off nodes red. Opens with only the
/// top categories visible (collapsed). Runs on the alternate screen buffer, sequentially with the REPL. Saves only
/// on F2; Esc cancels.
/// </summary>
internal sealed class TelemetryEditor
{
    private readonly TelemetryFile _model;
    private bool[] _eff;
    private bool _save;

    // Transparent normal background (terminal shows through); dark selection bar keeps coloured text readable.
    private Color _bgNormal;
    private Color _bgFocus;
    private Color _fgDefault;
    private Scheme _transparent;
    private Scheme _footgunScheme;

    // Palette copied verbatim from the One Dark theme claudeusage.cs renders with — sampled pixel-exact from the
    // dashboard so the two tools match. Terminal.Gui's own named colours are harsh W3C primaries (Color.Green is
    // #008000), so we give the RGB directly and let the default true-colour driver emit it unchanged.
    private static readonly Color On = new Color(152, 195, 121);      // #98C379 One Dark green — effective ON
    private static readonly Color Footgun = new Color(229, 192, 123); // #E5C07B One Dark amber — enabled but blocked ("orange")
    private static readonly Color Off = new Color(224, 108, 117);     // #E06C75 One Dark red — explicitly OFF

    public TelemetryEditor(TelemetryFile model)
    {
        _model = model;
        _eff = model.ResolveEffective();
    }

    /// <summary>Run the editor; returns true if the user saved.</summary>
    public bool Run()
    {
        Application.Init();
        try
        {
            // Keep the editor transparent: Color.None resolves to the terminal's own (default) background, so the
            // TUI blends with the surrounding shell instead of painting a solid slab. On a One Dark terminal that
            // background is #282C34; every colour below is a sampled One Dark value chosen to sit legibly on it.
            var baseScheme = SchemeManager.GetScheme(Schemes.Base);
            _bgNormal = Color.None;                    // node text sits directly on the terminal background (transparent)
            _bgFocus = new Color(62, 68, 81);          // #3E4451 One Dark selection line — subtle bar, keeps coloured text readable
            _fgDefault = new Color(171, 178, 191);     // #ABB2BF One Dark foreground — inherited/off nodes, soft not stark white
            var transparent = baseScheme with { Normal = new Attribute(_fgDefault, _bgNormal), Focus = new Attribute(_fgDefault, _bgFocus) };
            _transparent = transparent;
            _footgunScheme = new Scheme
            {
                Normal = new Attribute(Footgun, _bgNormal),
                Focus = new Attribute(Footgun, _bgFocus),
            };

            var refs = BuildRefs();

            var tree = new FlagTree
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(4), // leave 4 rows for the selection info + 2 legend lines
            };
            tree.TreeBuilder = new DelegateTreeBuilder<FlagRef>(
                childGetter: r => TelemetryFlagCatalog.Children(r.Index).Select(i => refs[i]),
                canExpand: r => TelemetryFlagCatalog.Children(r.Index).Any());
            tree.AspectGetter = Render;
            tree.ColorGetter = ColorFor;
            tree.AddObject(refs[0]);
            tree.Expand(refs[0]); // show ONLY the top categories, each collapsed
            tree.SetScheme(transparent);

            var selInfo = new Label
            {
                X = 1,
                Y = Pos.Bottom(tree),
                Width = Dim.Fill(1),
                Height = 2,
                Text = SelInfo(refs[0]),
            };
            // Legends are built from adjacent single-colour Labels (Terminal.Gui can't colour spans within one
            // Label): each token is tinted with the same palette the tree uses, so the key reads at a glance.
            var legendState = BuildLegendRow(1, Pos.Bottom(selInfo),
            [
                ("State:  ", _fgDefault),
                ("[*] explicit ON", On),
                ("    ", _fgDefault),
                ("[-] explicit OFF", Off),
                ("    ", _fgDefault),
                ("[·] inherit", _fgDefault),
                ("        Effective:  ", _fgDefault),
                ("● on", On),
                ("    ", _fgDefault),
                ("○ off", _fgDefault),
                ("    (", _fgDefault),
                ("orange = on but blocked by an off parent", Footgun),
                (")", _fgDefault),
            ]);
            var legendKeys = BuildLegendRow(1, Pos.Bottom(selInfo) + 1,
            [
                ("Keys:   ", _fgDefault),
                ("Space / Enter", Footgun),
                (" = cycle state        ", _fgDefault),
                ("F2", On),
                (" = save & quit        ", _fgDefault),
                ("Esc", Off),
                (" = cancel & quit", _fgDefault),
            ]);

            tree.Toggle = r =>
            {
                Cycle(r);
                if (TelemetryFlagCatalog.Children(r.Index).Any())
                {
                    tree.Expand(r); // reveal the (non-)cascade immediately
                }
                tree.SetNeedsDraw();
                UpdateSel(selInfo, r);
            };
            tree.SaveQuit = () =>
            {
                _save = true;
                Application.RequestStop();
            };
            tree.Cancel = () => Application.RequestStop();
            tree.SelectionChanged += (_, e) =>
            {
                if (e.NewValue is FlagRef r)
                {
                    UpdateSel(selInfo, r);
                }
            };

            var win = new Window
            {
                Title = $"telemetry — {_model.Path}",
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };
            win.SetScheme(transparent);
            win.Add(tree, selInfo);
            foreach (var v in legendState)
            {
                win.Add(v);
            }
            foreach (var v in legendKeys)
            {
                win.Add(v);
            }
            Application.Run(win);
            win.Dispose();
        }
        finally
        {
            Application.Shutdown();
        }

        if (_save)
        {
            _model.Save();
        }
        return _save;
    }

    private static FlagRef[] BuildRefs()
    {
        var all = TelemetryFlagCatalog.All;
        var refs = new FlagRef[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            refs[i] = new FlagRef(i);
        }
        return refs;
    }

    private string Render(FlagRef r)
    {
        var d = TelemetryFlagCatalog.All[r.Index];
        string tri;
        if (d.Kind == TelemetryFlagKind.Master)
        {
            // The root has no parent to inherit from: strictly on/off, never inherit.
            tri = _model.TryGetExplicit(d.Path, out var mv) && mv ? "[*]" : "[-]";
        }
        else
        {
            tri = _model.TryGetExplicit(d.Path, out var v) ? (v ? "[*]" : "[-]") : "[·]";
        }
        var eff = _eff[r.Index] ? "●" : "○";
        var name = d.Path.Length == 0 ? "Profiler" : d.Name;
        return $"{tri} {eff} {name}";
    }

    // Text-only colouring (backgrounds copied from the theme). Palette matches claudeusage.cs:
    // green = effective on, yellow/amber = enabled-but-blocked-by-parent (the footgun), red = explicitly off.
    private Scheme ColorFor(FlagRef r)
    {
        var d = TelemetryFlagCatalog.All[r.Index];
        bool hasExplicit = _model.TryGetExplicit(d.Path, out var explicitValue);
        if (hasExplicit && explicitValue && !_eff[r.Index])
        {
            return Fg(Footgun);        // turned on, but a disabled parent blocks it
        }
        if (_eff[r.Index])
        {
            return Fg(On);             // effectively on
        }
        if (hasExplicit && !explicitValue)
        {
            return Fg(Off);            // explicitly off
        }
        return Fg(_fgDefault);         // inherited off — default colour on a transparent background
    }

    private Scheme Fg(Color fg) => new Scheme
    {
        Normal = new Attribute(fg, _bgNormal),
        Focus = new Attribute(fg, _bgFocus),
    };

    // Build one legend line as a row of adjacent, individually-coloured Labels (Terminal.Gui has no span colouring
    // inside a single Label). Each X offset is the running character count, so the tinted tokens never overlap.
    private List<Label> BuildLegendRow(int baseX, Pos y, (string text, Color color)[] segs)
    {
        var row = new List<Label>(segs.Length);
        int col = 0;
        foreach (var (text, color) in segs)
        {
            var lbl = new Label { X = baseX + col, Y = y, Width = text.Length, Height = 1, Text = text };
            lbl.SetScheme(Fg(color));
            row.Add(lbl);
            col += text.Length;
        }
        return row;
    }

    private void Cycle(FlagRef r)
    {
        var d = TelemetryFlagCatalog.All[r.Index];
        var path = d.Path;
        if (d.Kind == TelemetryFlagKind.Master)
        {
            // Two-state root: on <-> off (off = absent, the default). No inherit.
            if (_model.TryGetExplicit(path, out var mv) && mv)
            {
                _model.Reset(path);
            }
            else
            {
                _model.Set(path, true);
            }
        }
        else if (_model.TryGetExplicit(path, out var v))
        {
            if (v)
            {
                _model.Set(path, false);   // On -> Off
            }
            else
            {
                _model.Reset(path);        // Off -> Inherit
            }
        }
        else
        {
            _model.Set(path, true);        // Inherit -> On
        }
        _eff = _model.ResolveEffective();  // recompute the honest cascade
    }

    // Update the info line's text AND colour it orange when the selected node is the enabled-but-blocked footgun.
    private void UpdateSel(Label label, FlagRef r)
    {
        label.Text = SelInfo(r);
        var d = TelemetryFlagCatalog.All[r.Index];
        bool footgun = _model.TryGetExplicit(d.Path, out var ev) && ev && !_eff[r.Index];
        bool ungated = d.Kind == TelemetryFlagKind.RawLeaf; // its info-line note is orange, same attention cue as the footgun
        label.SetScheme(footgun || ungated ? _footgunScheme : _transparent);
        label.SetNeedsDraw();
    }

    private string SelInfo(FlagRef r)
    {
        var d = TelemetryFlagCatalog.All[r.Index];
        var key = d.Path.Length == 0 ? "(master)" : d.Path;
        var line = $"{key}   —   effective: {(_eff[r.Index] ? "ON" : "off")}";
        if (d.Kind == TelemetryFlagKind.RawLeaf)
        {
            // Ungated firehose gauge: reads its own key directly, so it stays active even with the Profiler
            // master off. Green here is correct, not a leak — it's silenced only by setting it OFF explicitly.
            line += "    (ungated — active independently of the Profiler master; set it OFF explicitly to silence)";
        }
        else if (_model.TryGetExplicit(d.Path, out var ev) && ev && !_eff[r.Index])
        {
            var blocker = NearestOffAncestor(r.Index);
            if (blocker != null)
            {
                line += $"    (!) enabled but blocked by '{blocker}' — turn it on to cascade";
            }
        }
        return $"{line}\n{d.Description}";
    }

    // The nearest ancestor whose effective state is off — the reason an enabled node still won't emit.
    private string NearestOffAncestor(int index)
    {
        for (int p = TelemetryFlagCatalog.All[index].ParentIndex; p >= 0; p = TelemetryFlagCatalog.All[p].ParentIndex)
        {
            if (!_eff[p])
            {
                var pd = TelemetryFlagCatalog.All[p];
                return pd.Path.Length == 0 ? "Profiler" : pd.Path;
            }
        }
        return null;
    }

    /// <summary>Reference wrapper so the generic tree carries a non-ambiguous, nullable node object.</summary>
    private sealed class FlagRef
    {
        public readonly int Index;
        public FlagRef(int index) => Index = index;
    }

    /// <summary>TreeView specialization that turns key presses into editor actions.</summary>
    private sealed class FlagTree : TreeView<FlagRef>
    {
        public Action<FlagRef> Toggle;
        public Action SaveQuit;
        public Action Cancel;

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Space || key == Key.Enter)
            {
                if (SelectedObject != null)
                {
                    Toggle?.Invoke(SelectedObject);
                }
                return true;
            }
            if (key == Key.F2)
            {
                SaveQuit?.Invoke();
                return true;
            }
            if (key == Key.Esc)
            {
                Cancel?.Invoke();
                return true;
            }
            return base.OnKeyDown(key);
        }
    }
}
