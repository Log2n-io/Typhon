using System.Collections.Generic;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// What the checks read of items: a character's stacks, the item ledger's rows, a body's items revision; and the kills they stage. Every read of stacks and
/// rows is one quick transaction off the tick, which <c>EW-02</c> allows for these Versioned, non-spatial archetypes — the way the endpoints read them.
/// </summary>
internal static class Items
{
    /// <summary>A character's stacks, in slot order.</summary>
    public static List<(EntityId Id, ItemStack Stack)> Stacks(TatooineSim sim, EntityId character)
    {
        var key = unchecked((long)character.RawValue);
        var index = sim.Dbe.GetIndexRef<ItemStack, long>(s => s.Owner);
        var stacks = new List<(EntityId, ItemStack)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<ItemStack, long>(index, key, key);
        while (found.MoveNext())
        {
            stacks.Add((EntityId.FromRawValue((ulong)found.CurrentEntityPK), found.CurrentComponent));
        }

        stacks.Sort(static (a, b) => a.Item2.Slot.CompareTo(b.Item2.Slot));
        return stacks;
    }

    /// <summary>Every item ledger row, in sequence.</summary>
    public static List<ItemLedgerEntry> Rows(TatooineSim sim) => Rows(sim, out _);

    /// <summary>Every item ledger row, in sequence, and each row's entity.</summary>
    public static List<ItemLedgerEntry> Rows(TatooineSim sim, out List<EntityId> ids)
    {
        var rows = new List<ItemLedgerEntry>();
        ids = [];
        var index = sim.Dbe.GetIndexRef<ItemLedgerEntry, long>(e => e.Seq);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<ItemLedgerEntry, long>(index, long.MinValue, long.MaxValue);
        while (found.MoveNext())
        {
            rows.Add(found.CurrentComponent);
            ids.Add(EntityId.FromRawValue((ulong)found.CurrentEntityPK));
        }

        return rows;
    }

    /// <summary>A body's items revision, read on the tick.</summary>
    public static int ItemsRev(TatooineSim sim, EntityId body)
    {
        var rev = 0;
        sim.WriteInTick(tx => rev = tx.Open(body).Read(Player.State).ItemsRev);
        return rev;
    }

    /// <summary>A kill as the combat drain would push it: a creature of <paramref name="species"/> at (x, z), killed by <paramref name="killer"/>.</summary>
    public static KillEvent Kill(EntityId killer, ulong victim, float x, float z, byte species, ushort realm = 0, byte kind = KillVictimKind.Creature,
        EntityId mission = default)
        => new()
        {
            Killer = killer,
            Victim = EntityId.FromRawValue(victim),
            Lair = EntityId.Null,
            Mission = mission,
            X = x,
            Z = z,
            Realm = realm,
            VictimKind = kind,
            VictimLevel = 0,
            Species = species,
        };
}
