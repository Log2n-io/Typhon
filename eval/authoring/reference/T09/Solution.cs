using System.Numerics;
using System.Runtime.CompilerServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

[Component("Ref.Spot", 1, StorageMode = StorageMode.SingleVersion)]
public struct Spot
{
    [SpatialIndex]
    public AABB2F Box;
}

[Archetype]
public sealed partial class Thing : Archetype<Thing>
{
    public static readonly Comp<Spot> Spot = Register<Spot>();
}

public static class Solution
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DatabaseEngine, StrongBox<long>> Ticks = new();

    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o
        .Register<Spot>()
        .ConfigureSpatialGrid(SpatialGridConfig.Flat(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

    public static EntityId Spawn(DatabaseEngine db, float x, float y)
    {
        EntityId id;
        using (var tx = db.CreateQuickTransaction())
        {
            id = tx.Spawn<Thing>(Thing.Spot.Set(new Spot { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } }));
            tx.Commit();
        }
        // No runtime here: the spatial index is maintained by the tick fence, so run one (tick numbers must increase).
        var tick = Ticks.GetValue(db, _ => new StrongBox<long>(0));
        db.WriteTickFence(Interlocked.Increment(ref tick.Value));
        return id;
    }

    public static void Move(DatabaseEngine db, EntityId entity, float x, float y)
    {
        using (var tx = db.CreateQuickTransaction())
        {
            tx.OpenMut(entity).Set(Thing.Spot, new Spot { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } });
            tx.Commit();
        }
        var tick = Ticks.GetValue(db, _ => new StrongBox<long>(0));
        db.WriteTickFence(Interlocked.Increment(ref tick.Value));
    }

    public static int CountWithin(DatabaseEngine db, float x, float y, float radius)
    {
        using var tx = db.CreateReadOnlyTransaction();
        return tx.Query<Thing>().WhereNearby<Spot>(x, y, 0, radius).Count();
    }
}
