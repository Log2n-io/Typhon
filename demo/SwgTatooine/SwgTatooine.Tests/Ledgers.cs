using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// What the checks read of the economy: a wallet, the ledger's rows, and a session's refusals. Every read is one quick transaction off the tick, which
/// <c>EW-02</c> allows for these Versioned, non-spatial archetypes — the way <c>GET /swg/wallet</c> and the audit read them.
/// </summary>
internal static class Ledgers
{
    /// <summary>A character's wallet balance; fails the case when it has none.</summary>
    public static long Balance(TatooineSim sim, EntityId character)
    {
        Assert.That(TryWallet(sim, character, out var wallet), Is.True, $"character {character.RawValue} has no wallet");
        return wallet.Balance;
    }

    /// <summary>A character's wallet, if it has one, and its entity.</summary>
    public static bool TryWallet(TatooineSim sim, EntityId character, out Wallet wallet) => TryWallet(sim, character, out wallet, out _);

    /// <inheritdoc cref="TryWallet(TatooineSim, EntityId, out Wallet)"/>
    public static bool TryWallet(TatooineSim sim, EntityId character, out Wallet wallet, out EntityId id)
    {
        var key = unchecked((long)character.RawValue);
        var index = sim.Dbe.GetIndexRef<Wallet, long>(w => w.Owner);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<Wallet, long>(index, key, key);
        if (found.MoveNext())
        {
            wallet = found.CurrentComponent;
            id = EntityId.FromRawValue((ulong)found.CurrentEntityPK);
            return true;
        }

        wallet = default;
        id = EntityId.Null;
        return false;
    }

    /// <summary>How many wallets exist.</summary>
    public static int WalletCount(TatooineSim sim)
    {
        var index = sim.Dbe.GetIndexRef<Wallet, long>(w => w.Owner);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<Wallet, long>(index, long.MinValue, long.MaxValue);
        var n = 0;
        while (found.MoveNext())
        {
            n++;
        }

        return n;
    }

    /// <summary>Every ledger row, in sequence.</summary>
    public static List<LedgerEntry> Rows(TatooineSim sim) => Rows(sim, out _);

    /// <summary>Every ledger row, in sequence, and each row's entity.</summary>
    public static List<LedgerEntry> Rows(TatooineSim sim, out List<EntityId> ids)
    {
        var rows = new List<LedgerEntry>();
        ids = [];
        var index = sim.Dbe.GetIndexRef<LedgerEntry, long>(e => e.Seq);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<LedgerEntry, long>(index, long.MinValue, long.MaxValue);
        while (found.MoveNext())
        {
            rows.Add(found.CurrentComponent);
            ids.Add(EntityId.FromRawValue((ulong)found.CurrentEntityPK));
        }

        return rows;
    }

    /// <summary>Every <c>ACKS</c> reason this session was sent.</summary>
    public static byte[] Refusals(FakeLink link)
    {
        var sink = new AckSink { Reasons = [] };
        var frame = default(RealmFrame);
        var sent = link.Sent;
        for (var i = 0; i < sent.Length; i++)
        {
            if (sent[i].Length > 0 && sent[i][0] == MessageTypes.Tick)
            {
                TickReader.Read(sent[i], link.Plan, ref frame, ref sink);
            }
        }

        return [.. sink.Reasons];
    }

    /// <summary>The command a <c>/tip</c> sends.</summary>
    public static void Tip(FakeLink from, uint target, uint amount)
        => from.SendCommand(nameof(GiveCredits), new RecordValues { ["target"] = FieldValue.Of(target), ["amount"] = FieldValue.Of(amount) });
}
