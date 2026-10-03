namespace Typhon.Engine.Internals;

/// <summary>
/// The adapter behind <see cref="Dag.ChunkedSystem{TChunk}"/>: a chunked callback whose prepare and chunk body are the registered lambdas, with the same
/// table and the same check as the class form.
/// </summary>
internal sealed class LambdaChunkedSystem<TChunk> : ChunkedCallbackSystem where TChunk : unmanaged
{
    private readonly ChunkTable<TChunk> _plan = new();
    private readonly ChunkPrepare<TChunk> _prepare;
    private readonly ChunkExecute<TChunk> _execute;

    public LambdaChunkedSystem(ChunkPrepare<TChunk> prepare, ChunkExecute<TChunk> execute)
    {
        _prepare = prepare;
        _execute = execute;
    }

    internal override int OnPrepare() => ChunkPlans.Checked(_prepare(_plan), _plan.Count);

    // Registered directly by Dag.ChunkedSystem, which writes the registration itself: nothing calls Configure.
    protected override void Configure(SystemBuilder b)
    {
    }

    protected override void Execute(TickContext ctx) => _execute(ctx, ref _plan[ctx.ChunkIndex]);
}
