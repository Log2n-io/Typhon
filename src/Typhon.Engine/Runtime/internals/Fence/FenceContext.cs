namespace Typhon.Engine.Internals;

/// <summary>
/// Tick-scoped state shared across the four fence phases (Prep, Migrate, AabbRefresh, Finalize). One instance lives on <see cref="DatabaseEngine"/>; bound onto
/// each fence-phase system via <see cref="DagScheduler.RegisterContext{TContext}"/>. Reset at the start of every fence pass.
/// </summary>
internal sealed class FenceContext
{
    public long TickNumber;
    public ChangeSet UowChangeSet;
    public int WorkerCount;
    public int ChunkOversubscription;
    public LiveFenceCostModel CostModel;

    /// <summary>Mirror of <c>RuntimeOptions.EntityMapBulkMinEntriesPerBucket</c>, read by the Migrate phase's Prepare.</summary>
    public float EntityMapBulkMinEntriesPerBucket;

    public long HighestTableLsn;
    public long HighestArchetypeLsn;

    public void Reset(long tickNumber, ChangeSet uowCs, int workerCount, int chunkOversub, LiveFenceCostModel cost,
        float entityMapBulkMinEntriesPerBucket = 0f)
    {
        EntityMapBulkMinEntriesPerBucket = entityMapBulkMinEntriesPerBucket;
        TickNumber = tickNumber;
        UowChangeSet = uowCs;
        WorkerCount = workerCount;
        ChunkOversubscription = chunkOversub;
        CostModel = cost;
        HighestTableLsn = 0;
        HighestArchetypeLsn = 0;
    }
}
