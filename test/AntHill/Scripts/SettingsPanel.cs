using Godot;

namespace AntHill;

/// <summary>
/// Phase 1 settings overlay — Esc toggles. Lives on its own CanvasLayer (Layer = 20) so it floats above the HUD
/// (Layer 10) and the minimap (Layer 11). Pauses input passthrough while open so clicks don't fall through to the camera.
///
/// Controls:
///   • Time-scale slider (0× / 0.5× / 1× / 2× / 4× / 10×)
///   • Tilt mode (90° top-down / 45° iso / 85° cinematic)
///   • Pheromone overlay toggle
///   • Debug overlay toggle (right-side HUD visibility)
/// </summary>
public partial class SettingsPanel : CanvasLayer
{
    public delegate void TiltChosenHandler(float tiltRadians);
    public event TiltChosenHandler TiltChosen;
    public delegate void TimeScaleChangedHandler(float scale);
    public event TimeScaleChangedHandler TimeScaleChanged;
    public delegate void PheromoneToggledHandler(bool enabled);
    public event PheromoneToggledHandler PheromoneToggled;
    public delegate void DebugToggledHandler(bool enabled);
    public event DebugToggledHandler DebugToggled;
    public delegate void SnapToGridChangedHandler(bool enabled);
    public event SnapToGridChangedHandler SnapToGridChanged;
    public delegate void LuminosityChangedHandler(float value);
    public event LuminosityChangedHandler LuminosityChanged;
    public delegate void PauseDayNightToggledHandler(bool paused);
    public event PauseDayNightToggledHandler PauseDayNightToggled;

    private Panel _panel;
    private HSlider _timeScaleSlider;
    private Label _timeScaleLabel;
    private OptionButton _tiltDropdown;
    private CheckButton _pheromoneCheck;
    private CheckButton _debugCheck;
    private CheckButton _snapToGridCheck;
    private HSlider _luminositySlider;
    private Label _luminosityLabel;
    private CheckButton _pauseDayNightCheck;

    private static readonly float[] TimeScales = [0f, 0.5f, 1f, 2f, 4f, 10f];

    public override void _Ready()
    {
        Layer = 20;
        Visible = false;

        _panel = new Panel
        {
            CustomMinimumSize = new Vector2(360, 420),
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -180,
            OffsetRight = 180,
            OffsetTop = -210,
            OffsetBottom = 210,
        };

        var vbox = new VBoxContainer
        {
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = 16,
            OffsetTop = 16,
            OffsetRight = -16,
            OffsetBottom = -16,
        };
        _panel.AddChild(vbox);

        var title = new Label { Text = "Settings", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        vbox.AddChild(title);
        vbox.AddChild(new HSeparator());

        // Time-scale row
        _timeScaleLabel = new Label { Text = "Time scale: 1×" };
        vbox.AddChild(_timeScaleLabel);
        _timeScaleSlider = new HSlider
        {
            MinValue = 0,
            MaxValue = TimeScales.Length - 1,
            Step = 1,
            Value = 2,        // index of 1× in TimeScales
        };
        _timeScaleSlider.ValueChanged += OnTimeScaleSliderChanged;
        vbox.AddChild(_timeScaleSlider);

        vbox.AddChild(new HSeparator());

        // Phase 6A — Daisyworld luminosity row
        _luminosityLabel = new Label { Text = "Luminosity: 0.50" };
        vbox.AddChild(_luminosityLabel);
        _luminositySlider = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            Value = 0.5,
        };
        _luminositySlider.ValueChanged += OnLuminosityChanged;
        vbox.AddChild(_luminositySlider);

        _pauseDayNightCheck = new CheckButton { Text = "Pause day/night cycle" };
        _pauseDayNightCheck.Toggled += b => PauseDayNightToggled?.Invoke(b);
        vbox.AddChild(_pauseDayNightCheck);

        vbox.AddChild(new HSeparator());

        // Tilt mode dropdown
        vbox.AddChild(new Label { Text = "Camera tilt" });
        _tiltDropdown = new OptionButton();
        _tiltDropdown.AddItem("90° top-down", 0);
        _tiltDropdown.AddItem("45° isometric", 1);
        _tiltDropdown.AddItem("85° cinematic", 2);
        _tiltDropdown.Selected = 0;
        _tiltDropdown.ItemSelected += OnTiltSelected;
        vbox.AddChild(_tiltDropdown);

        vbox.AddChild(new HSeparator());

        _pheromoneCheck = new CheckButton { Text = "Pheromone overlay (H)" };
        _pheromoneCheck.Toggled += b => PheromoneToggled?.Invoke(b);
        vbox.AddChild(_pheromoneCheck);

        _debugCheck = new CheckButton { Text = "Debug overlay (F1)", ButtonPressed = true };
        _debugCheck.Toggled += b => DebugToggled?.Invoke(b);
        vbox.AddChild(_debugCheck);

        _snapToGridCheck = new CheckButton { Text = "Snap tools to 1 m grid" };
        _snapToGridCheck.Toggled += b => SnapToGridChanged?.Invoke(b);
        vbox.AddChild(_snapToGridCheck);

        vbox.AddChild(new HSeparator());
        var closeBtn = new Button { Text = "Close (Esc)" };
        closeBtn.Pressed += () => Visible = false;
        vbox.AddChild(closeBtn);

        AddChild(_panel);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            Visible = !Visible;
            GetViewport().SetInputAsHandled();
        }
    }

    public void SyncFromCamera(LodBand band, float fadeIndividuals, float fadeDensity)
    {
        // Nothing to do — placeholder for future band sliders.
    }

    public void SetPheromoneToggle(bool enabled)
    {
        if (_pheromoneCheck != null) _pheromoneCheck.ButtonPressed = enabled;
    }

    /// <summary>
    /// Step the time-scale slider by ±1 index (keyboard [ / ] shortcut). Moving the slider fires
    /// the value-changed handler which in turn invokes TimeScaleChanged — Main wires that to the
    /// bridge, so this keeps slider + bridge in sync via a single source of truth.
    /// </summary>
    public void StepTimeScale(int direction)
    {
        if (_timeScaleSlider == null) return;
        int next = Mathf.Clamp((int)_timeScaleSlider.Value + direction, 0, TimeScales.Length - 1);
        _timeScaleSlider.Value = next;
    }

    private void OnTimeScaleSliderChanged(double value)
    {
        int idx = Mathf.Clamp((int)value, 0, TimeScales.Length - 1);
        float scale = TimeScales[idx];
        _timeScaleLabel.Text = $"Time scale: {(scale == 0f ? "Paused" : scale + "×")}";
        TimeScaleChanged?.Invoke(scale);
    }

    private void OnLuminosityChanged(double value)
    {
        float v = Mathf.Clamp((float)value, 0f, 1f);
        _luminosityLabel.Text = $"Luminosity: {v:F2}";
        LuminosityChanged?.Invoke(v);
    }

    private void OnTiltSelected(long index)
    {
        float tilt = index switch
        {
            0 => 0f,
            1 => Mathf.Pi / 4f,
            2 => Mathf.Pi * (85f / 180f),
            _ => 0f,
        };
        TiltChosen?.Invoke(tilt);
    }
}
