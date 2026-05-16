using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Resets per-worker render buffers + pre-computes the food/nest overlay + downsamples the
/// pheromone grid into the heatmap RGBA buffer. Pure callback. Cross-phase ordering vs <c>AntUpdate</c>
/// (writes PheromoneGrid) and <c>PheroDecay</c> (writes PheromoneGrid) is derived automatically
/// from the R/W graph — no explicit edges needed.
/// </summary>
internal sealed class PrepareRenderBufferSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public PrepareRenderBufferSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("PrepareRenderBuffer")
        .Phase(AntPhases.Render)
        .ReadsResource("PheromoneGrid")
        .ReadsResource("FoodInventory")
        .ReadsResource("NestInventory")
        .WritesResource("RenderBuffers");
        // "Heatmap" write moved to HeatmapRgbaPackSystem (Phase 6 polish — parallel heatmap path).

    protected override void Execute(TickContext ctx) => _bridge.PrepareRender(ctx);
}

/// <summary>
/// Per-worker fill of the render buffer (transform, color, alpha per visible ant). Reads
/// bounds + state + genetics; per-worker writes to the worker's slot in
/// <c>RenderBuffers</c> — declared as <c>WritesResource("RenderBuffers")</c> with
/// <c>.WritesVersioned()</c>. Sole consumer of <c>TierMirror</c> + <c>CameraAABB</c> in this
/// phase for clipping.
/// </summary>
internal sealed class FillRenderBufferSystem : QuerySystem
{
    private readonly TyphonBridge _bridge;
    public FillRenderBufferSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("FillRenderBuffer")
        .Phase(AntPhases.Render)
        .Parallel()
        .Reads<WorldBounds>()
        .Reads<AntState>()
        .ReadsSnapshot<Genetics>()
        .ReadsResource("CameraAABB")
        .ReadsResource("TierMirror")
        .WritesResource("RenderBuffers")
        .Input(() => _bridge._antView)
        .After("PrepareRenderBuffer");

    protected override void Execute(TickContext ctx) => _bridge.FillRender(ctx);
}

/// <summary>
/// Snapshots the worker buffers + heatmap + overlay into a frame and publishes to the render
/// bridge. Swaps the heatmap double buffer + clears worker buffers for the next tick.
/// </summary>
internal sealed class PublishRenderFrameSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public PublishRenderFrameSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("PublishRenderFrame")
        .Phase(AntPhases.Render)
        .WritesResource("RenderBuffers")
        .WritesResource("Heatmap")
        // Multiple writers of RenderBuffers (Prepare, Fill, this) and Heatmap (HeatmapRgbaPack, this)
        // — the deriver requires direct pairwise edges between every writer, no transitive closure.
        .AfterAll("PrepareRenderBuffer", "FillRenderBuffer", "HeatmapRgbaPack");

    protected override void Execute(TickContext ctx) => _bridge.PublishRender(ctx);
}

/// <summary>
/// Drains the three telemetry queues (deaths, food pickups, food deliveries) and updates the
/// public counters (<c>DeathCount</c>, <c>FoodDelivered</c>) the UI exposes. Replaces the
/// previous <c>Interlocked.Increment</c> calls in <c>AntUpdate</c> — same totals, but
/// the data flow is visible in the System DAG view as queue-edges.
/// </summary>
internal sealed class AntStatsAggregatorSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public AntStatsAggregatorSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("AntStatsAggregator")
        .Phase(AntPhases.Render)
        .ReadsEvents(_bridge._antDiedQueue)
        .ReadsEvents(_bridge._foodPickedUpQueue)
        .ReadsEvents(_bridge._foodDeliveredQueue)
        // Cross-phase producer→consumer edge from AntUpdate is derived automatically by the
        // deriver (WritesEvents on AntUpdate side, ReadsEvents here).
        // Run before the renderer's stats dump so the published counters reflect this tick.
        .Before("PrepareRenderBuffer");

    protected override void Execute(TickContext ctx)
    {
        var prevDeath = _bridge._deathCount;

        _bridge._deathCount += _bridge._antDiedQueue.Count;
        _bridge._foodDelivered += _bridge._foodDeliveredQueue.Count;
        // FoodPickedUpEvent isn't surfaced as a counter today; consumed only to advance the
        // queue (the next tick's Reset clears it). Kept on the wire for future stats panels.
        _ = _bridge._foodPickedUpQueue.Count;

        // Significance filter: only death milestones — delivery milestones spammed too fast.
        const int DeathMilestone = 1000;
        const float CenterRender = 50f;  // 100m world / 2

        var t = _bridge._simTimeSec;
        var deathMsBefore = prevDeath / DeathMilestone;
        var deathMsAfter = _bridge._deathCount / DeathMilestone;
        for (var m = deathMsBefore + 1; m <= deathMsAfter; m++)
        {
            _bridge._eventLog.Enqueue(new LogEntry(t,
                $"{m * DeathMilestone:N0} ants died total",
                CenterRender, CenterRender, LogSeverity.Milestone));
        }
    }
}
