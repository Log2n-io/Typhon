// Compile-only mirror of the idioms in doc/llms-preamble.md and doc/guide/using-with-ai-coding-agents.md §3 that Program.cs and
// Systems.cs do not already exercise. Nothing here runs: the file exists so that an API change to any idiom we tell coding agents
// to use breaks the Typhon.slnx build, instead of silently invalidating llms.txt. Keep it in step with those two pages.

using Typhon.Engine;
using Typhon.Samples.Swg.Shard;
using Typhon.Schema.Definition;

namespace SwgGuide;

internal static class AgentIdioms
{
    // Transactions: OpenMut writes, Open reads; the Try* forms and IsAlive for an id that may be stale.
    public static void StaleIdSafeUpdate(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        if (!tx.IsAlive(id) || !tx.TryOpenMut(id, out var entity))
        {
            return;
        }
        var wallet = entity.Read(Character.Wallet);   // a copy
        wallet.Credits += 10;
        entity.Set(Character.Wallet, wallet);         // stored back; there is no Write
        tx.Commit();

        if (tx.TryOpen(id, out var readOnly))
        {
            _ = readOnly.Read(Character.Wallet).Credits;
        }
    }

    // Queries: WhereField uses the [Index]; Where<T> refines over a full scan. The foreach variable is a read-only EntityRef:
    // read it directly, write through OpenMut(e.Id).
    public static void QueryThenMutate(Transaction tx)
    {
        var woundedImperials = tx.Query<Character>()
            .WhereField<Faction>(f => f.Value == Factions.Imperial)
            .Where<Ham>(h => h.Health < h.MaxHealth);
        foreach (var e in woundedImperials)
        {
            var ham = e.Read(Character.Ham);
            ham.Health = ham.MaxHealth;
            tx.OpenMut(e.Id).Set(Character.Ham, ham);
        }
    }
}

/// <summary>Every access declaration the preamble lists, plus the in-system replication and indexed-component write idioms.</summary>
internal sealed class AgentIdiomsSystem : QuerySystem
{
    private readonly EcsView<Character> _characters;

    public AgentIdiomsSystem(EcsView<Character> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("AgentIdioms")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .After("Regen")                // a second same-phase writer of Ham: Build() demands an explicit order
        .Writes<Ham>()
        .Writes<Faction>()
        .Reads<Intent>()
        .ReadsFresh<Transform>()       // after this tick's writers of Transform
        .ReadsSnapshot<Wallet>()       // before them — Versioned components only
        .SideWrites<Wallet>();         // written by a side transaction (informational)

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Character>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Character>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                // Faction carries an [Index] and is SingleVersion: no span over it (GetSpan/Get throw) — write through Set.
                cluster.Set(Character.Faction, slot, new Faction { Value = Factions.Neutral });
                // PushDetection.Explicit (the default): without this mark no client is sent the change.
                ctx.Subscriptions.Replicate(in cluster, slot);
            }
        }
    }
}
