using System;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The economy's foundation (economy-foundation.md): a wallet per character opened with its grant, a ledger row per transfer, tips between players, and the
/// audit that replays the ledger and catches every rule broken.
/// </summary>
/// <remarks>
/// Players are parked in the planet's far south-west, where no city, lair site or terminal is near: nothing else happens to them while a case watches its
/// own transfers. Reads of a player go through the tick (#1496); reads of wallets and rows are quick transactions, which <c>EW-02</c> allows for them.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class EconomyChecks : ServedChecks
{
    private TatooineSim Start(bool persist = false)
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.Persist = persist;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private static (float X, float Z) OpenGround(TatooineSim sim)
    {
        var reach = sim.Config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge * 0.9f;
        return (-reach, -reach);
    }

    /// <summary>A player logged in and parked at a point; its link, body and character.</summary>
    private (FakeLink Link, EntityId Body, EntityId Character, long Parked) LogIn(TatooineSim sim, ulong account, float x, float z, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = Worlds.Park(sim, link.Session, x, z, out var parked);
        Assert.That(CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(account), out var character, out _), Is.True, "the account has no character");
        return (link, body, character, parked);
    }

    private static int WalletRev(TatooineSim sim, EntityId body)
    {
        var rev = 0;
        sim.WriteInTick(tx => rev = tx.Open(body).Read(Player.State).WalletRev);
        return rev;
    }

    /// <summary>Two players standing five metres apart, each named in the other's view.</summary>
    private (FakeLink A, EntityId BodyA, EntityId CharA, FakeLink B, EntityId BodyB, EntityId CharB, uint BSeenByA) Pair(TatooineSim sim, ulong accountA = 0x7201,
        ulong accountB = 0x7202)
    {
        var (x, z) = OpenGround(sim);
        var a = LogIn(sim, accountA, x, z, 1);
        var b = LogIn(sim, accountB, x + 5f, z, 2);
        var seen = SessionHarness.UntilNamed(a.Link.Session, b.Body, "the second player to reach the first's view", Math.Max(a.Parked, b.Parked));
        return (a.Link, a.Body, a.Character, b.Link, b.Body, b.Character, seen);
    }

    // ── E0-1: the wallet and its grant ──────────────────────────────────────────────────────────────────────────────

    /// <summary>E0-1: a first login opens the character's wallet with one grant from World; a later login opens nothing.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFirstLogin_OpensAWalletWithItsGrant_AndALaterOneOpensNothing()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var first = LogIn(sim, 0x7210, x, z, 1);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, first.Character, out _), "the wallet to be opened");
        Harness.Ticks(2);

        var rows = Ledgers.Rows(sim);
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Balance(sim, first.Character), Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Kind, Is.EqualTo(TransferKind.Grant));
            Assert.That(rows[0].Seq, Is.EqualTo(1));
            Assert.That(rows[0].From, Is.EqualTo(EconomyLedger.World));
            Assert.That(rows[0].To, Is.EqualTo((long)first.Character.RawValue));
            Assert.That(rows[0].Amount, Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(rows[0].FromAfter, Is.EqualTo(-EconomyLedger.StartingCredits), "World runs negative by what it paid out");
            Assert.That(rows[0].ToAfter, Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(WalletRev(sim, first.Body), Is.GreaterThan(0), "the body was not told its wallet moved");
            Assert.That(sim.Bridge.WalletsOpened, Is.EqualTo(1));
        });

        var destroyed = TatooineReplication.AvatarsDestroyed;
        first.Link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed > destroyed, "the avatar to be destroyed");
        var again = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7210));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 2, "the second avatar to be spawned");
        Harness.Ticks(4);
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Rows(sim), Has.Count.EqualTo(1), "a second login granted again");
            Assert.That(Ledgers.Balance(sim, first.Character), Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(Ledgers.WalletCount(sim), Is.EqualTo(1));
            Assert.That(again.Closed, Is.Null);
        });
    }

    // ── E0-7: tips ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>E0-7: a tip moves its amount from one wallet to the other as one row, tells both bodies, and leaves the audit clean.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ATip_MovesItsAmount_AsOneRow_AndTellsBothBodies()
    {
        using var sim = Start();
        var p = Pair(sim);
        SessionHarness.Until(() => Ledgers.Rows(sim).Count == 2, "both wallets to be opened");
        Harness.Ticks(2);
        var revA = WalletRev(sim, p.BodyA);
        var revB = WalletRev(sim, p.BodyB);

        Ledgers.Tip(p.A, p.BSeenByA, 150);
        SessionHarness.Until(() => sim.Bridge.Tips >= 1, "the tip to be applied");
        Harness.Ticks(2);

        var rows = Ledgers.Rows(sim);
        var tip = rows[^1];
        var audit = EconomyEndpoints.Audit(sim.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Balance(sim, p.CharA), Is.EqualTo(EconomyLedger.StartingCredits - 150));
            Assert.That(Ledgers.Balance(sim, p.CharB), Is.EqualTo(EconomyLedger.StartingCredits + 150));
            Assert.That(rows, Has.Count.EqualTo(3), "two grants and the tip");
            Assert.That(tip.Kind, Is.EqualTo(TransferKind.Tip));
            Assert.That(tip.Seq, Is.EqualTo(3));
            Assert.That(tip.From, Is.EqualTo((long)p.CharA.RawValue));
            Assert.That(tip.To, Is.EqualTo((long)p.CharB.RawValue));
            Assert.That(tip.Amount, Is.EqualTo(150));
            Assert.That(tip.FromAfter, Is.EqualTo(EconomyLedger.StartingCredits - 150));
            Assert.That(tip.ToAfter, Is.EqualTo(EconomyLedger.StartingCredits + 150));
            Assert.That(WalletRev(sim, p.BodyA), Is.GreaterThan(revA), "the giver's body was not told");
            Assert.That(WalletRev(sim, p.BodyB), Is.GreaterThan(revB), "the recipient's body was not told");
            Assert.That(Ledgers.Refusals(p.A), Is.Empty);
            Assert.That(audit.Ok, Is.True, string.Join("; ", audit.Violations));
            Assert.That(audit.Entries, Is.EqualTo(3));
            Assert.That(audit.WalletsTotal + audit.World, Is.Zero);
        });
    }

    /// <summary>
    /// E0-7: every refusal says why and moves nothing — a bad amount, more than the wallet holds, oneself, a body with no wallet, a body too far away.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ATip_IsRefused_WithItsReason_AndMovesNothing()
    {
        using var sim = Start();
        var p = Pair(sim);
        SessionHarness.Until(() => Ledgers.Rows(sim).Count == 2, "both wallets to be opened");
        var self = SessionHarness.UntilNamed(p.A.Session, p.BodyA, "the giver's own body in its view");

        // A body of the crowd beside the giver: in its view, and with no character, so no wallet.
        var crowd = EntityId.Null;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                for (var bits = cluster.OccupancyBits; bits != 0 && crowd.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (cluster.Realm.Value == 0 && controls[slot].Kind == ControllerKind.Scripted)
                    {
                        crowd = cluster.GetEntityId(slot);
                    }
                }
            }
        });
        var (x, z) = OpenGround(sim);
        var placed = Worlds.Park(sim, crowd, x, z + 4f);
        var crowdSeen = SessionHarness.UntilNamed(p.A.Session, crowd, "the crowd body in the giver's view", placed);

        var cases = new (uint Target, uint Amount, byte Reason, string What)[]
        {
            (p.BSeenByA, 0, TatooineReplication.TipBadAmount, "zero credits"),
            (p.BSeenByA, (uint)EconomyLedger.MaxTip + 1, TatooineReplication.TipBadAmount, "past the most one tip may move"),
            (p.BSeenByA, (uint)EconomyLedger.StartingCredits + 1, TatooineReplication.TipNotEnough, "more than the wallet holds"),
            (self, 10, TatooineReplication.TipSelf, "oneself"),
            (crowdSeen, 10, TatooineReplication.TipNoRecipient, "a body with no wallet"),
            (999_999, 10, TatooineReplication.TipNoRecipient, "a netId never shown"),
        };
        foreach (var (target, amount, reason, what) in cases)
        {
            var refused = sim.Bridge.TipsRefused;
            Ledgers.Tip(p.A, target, amount);
            SessionHarness.Until(() => sim.Bridge.TipsRefused > refused, $"the tip of {what} to be refused");

            // The command's rate is two a second, burst four: spaced, so the engine's limiter never answers for the economy.
            Harness.Ticks(Worlds.ServedHz / 2 + 1);
            Assert.That(Ledgers.Refusals(p.A), Does.Contain(reason), what);
        }

        // Too far: the recipient walks off to forty metres, still in view.
        var moved = Worlds.Park(sim, p.BodyB, x + 40f, z);
        Harness.Ticks(3);
        var far = sim.Bridge.TipsRefused;
        Ledgers.Tip(p.A, p.BSeenByA, 10);
        SessionHarness.Until(() => sim.Bridge.TipsRefused > far, "the tip from too far to be refused");
        Harness.Ticks(2);

        var audit = EconomyEndpoints.Audit(sim.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.GreaterThanOrEqualTo(0));
            Assert.That(Ledgers.Refusals(p.A), Does.Contain(TatooineReplication.TipTooFar), "too far");
            Assert.That(Ledgers.Rows(sim), Has.Count.EqualTo(2), "a refused tip wrote a row");
            Assert.That(Ledgers.Balance(sim, p.CharA), Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(Ledgers.Balance(sim, p.CharB), Is.EqualTo(EconomyLedger.StartingCredits));
            Assert.That(sim.Bridge.Tips, Is.Zero);
            Assert.That(audit.Ok, Is.True, string.Join("; ", audit.Violations));
        });
    }

    // ── E0-8: the audit ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// E0-8: the audit names each rule a corrupted ledger breaks — a wallet that disagrees with its rows, a row whose balance-after is wrong, a gap in the
    /// sequence — and passes again once each is put back. Corrupted off the tick, the way a tool or a bug in a second writer would.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheAudit_NamesEveryRuleACorruptedLedgerBreaks()
    {
        using var sim = Start();
        var p = Pair(sim);
        SessionHarness.Until(() => Ledgers.Rows(sim).Count == 2, "both wallets to be opened");
        Ledgers.Tip(p.A, p.BSeenByA, 100);
        SessionHarness.Until(() => sim.Bridge.Tips >= 1, "the tip to be applied");
        Harness.Ticks(2);
        Assert.That(EconomyEndpoints.Audit(sim.Dbe).Ok, Is.True, "the premise: a clean ledger");

        var a = (long)p.CharA.RawValue;
        Assert.That(Ledgers.TryWallet(sim, p.CharA, out _, out var walletA), Is.True);
        var rows = Ledgers.Rows(sim, out var rowIds);
        var tipRow = rowIds[^1];

        // A wallet that disagrees with its rows: and the sum is off by as much.
        Corrupt(sim, tx => Edit(tx, walletA, WalletArch.Purse, (ref w) => w.Balance += 7));
        var wallet = EconomyEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, walletA, WalletArch.Purse, (ref w) => w.Balance -= 7));

        // A row whose recipient's balance-after is not what the replay gives.
        Corrupt(sim, tx => Edit(tx, tipRow, LedgerArch.Entry, (ref e) => e.ToAfter += 1));
        var after = EconomyEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, tipRow, LedgerArch.Entry, (ref e) => e.ToAfter -= 1));

        // A gap: the last row renumbered past the next.
        Corrupt(sim, tx => Edit(tx, tipRow, LedgerArch.Entry, (ref e) => e.Seq = 5));
        var gap = EconomyEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, tipRow, LedgerArch.Entry, (ref e) => e.Seq = 3));

        var clean = EconomyEndpoints.Audit(sim.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(wallet.Ok, Is.False);
            Assert.That(wallet.Violations, Has.Some.StartsWith($"wallet {a}: holds {EconomyLedger.StartingCredits - 100 + 7}, the replay says {EconomyLedger.StartingCredits - 100}"));
            Assert.That(wallet.Violations, Has.Some.StartsWith("conservation:"));
            Assert.That(after.Ok, Is.False);
            Assert.That(after.Violations, Has.Some.Contains("'s balance after is"));
            Assert.That(after.Violations.Any(v => v.StartsWith("conservation:", StringComparison.Ordinal)), Is.False, "the balances themselves still sum");
            Assert.That(gap.Ok, Is.False);
            Assert.That(gap.Violations, Has.Some.StartsWith("seq 5: expected 3"));
            Assert.That(gap.Violations, Has.Some.StartsWith("head: last seq 3"));
            Assert.That(clean.Ok, Is.True, string.Join("; ", clean.Violations));
        });
    }

    /// <summary>E0-8: the audit's endpoint answers the same verdict as JSON.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheAuditEndpoint_AnswersItsVerdictAsJson()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var one = LogIn(sim, 0x7220, x, z, 1);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, one.Character, out _), "the wallet to be opened");
        var json = EconomyEndpoints.Audit(sim.Dbe).ToJson();
        var wallet = EconomyEndpoints.WalletJson(sim.Dbe, one.Character);
        var ledger = EconomyEndpoints.LedgerJson(sim.Dbe, 0, 10);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.StartWith("{\"ok\":true,\"entries\":1,\"lastSeq\":1,\"wallets\":1,\"world\":-1000,\"walletsTotal\":1000,\"sum\":0,\"violationCount\":0,\"violations\":[]"));
            Assert.That(wallet, Does.StartWith("{\"open\":true,\"credits\":1000,\"transfers\":[{\"seq\":\"1\""));
            Assert.That(wallet, Does.Contain("\"kind\":\"grant\",\"amount\":1000,\"other\":\"World\""));
            Assert.That(ledger, Does.StartWith("{\"rows\":[{\"seq\":1,"));
            Assert.That(ledger, Does.EndWith("],\"next\":1}"));
        });
    }

    // ── E0-9: across a reopen ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// E0-9: wallets and rows survive a server stopped with its players still in the world — no logout, the way a kill drops them — and the sequence carries
    /// on from where it stopped, the audit clean on both sides of the reopen.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void TheLedger_SurvivesAStopWithPlayersInTheWorld_AndCarriesOn()
    {
        long balanceA;
        long balanceB;
        int rowsBefore;
        EntityId charA;
        EntityId charB;
        using (var first = Start(persist: true))
        {
            var p = Pair(first, 0x7230, 0x7231);
            SessionHarness.Until(() => Ledgers.Rows(first).Count == 2, "both wallets to be opened");
            Ledgers.Tip(p.A, p.BSeenByA, 42);
            SessionHarness.Until(() => first.Bridge.Tips >= 1, "the tip to be applied");
            Harness.Ticks(3);
            charA = p.CharA;
            charB = p.CharB;
            balanceA = Ledgers.Balance(first, charA);
            balanceB = Ledgers.Balance(first, charB);
            rowsBefore = Ledgers.Rows(first).Count;
            Assert.That(EconomyEndpoints.Audit(first.Dbe).Ok, Is.True, "the premise: a clean ledger before the stop");

            // No Close: dropped the way a kill -9 drops it.
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database, or this proves nothing");
        Harness.Ticks(3);
        var reopened = EconomyEndpoints.Audit(second.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.Ok, Is.True, string.Join("; ", reopened.Violations));
            Assert.That(reopened.Entries, Is.EqualTo(rowsBefore));
            Assert.That(Ledgers.Balance(second, charA), Is.EqualTo(balanceA));
            Assert.That(Ledgers.Balance(second, charB), Is.EqualTo(balanceB));
        });

        var q = Pair(second, 0x7230, 0x7231);
        Ledgers.Tip(q.A, q.BSeenByA, 8);
        SessionHarness.Until(() => second.Bridge.Tips >= 1, "the tip after the reopen to be applied");
        Harness.Ticks(2);
        var rows = Ledgers.Rows(second);
        var audit = EconomyEndpoints.Audit(second.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(second.Bridge.WalletsOpened, Is.Zero, "a wallet was opened again after the reopen: the grant was paid twice");
            Assert.That(rows, Has.Count.EqualTo(rowsBefore + 1));
            Assert.That(rows[^1].Seq, Is.EqualTo(rowsBefore + 1), "the sequence did not carry on");
            Assert.That(Ledgers.Balance(second, charA), Is.EqualTo(balanceA - 8));
            Assert.That(audit.Ok, Is.True, string.Join("; ", audit.Violations));
        });
    }

    // ── corruption helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static void Corrupt(TatooineSim sim, Action<Transaction> write)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        write(tx);
        Assert.That(tx.Commit(), Is.True, "the corruption did not commit");
    }

    private delegate void Mutate<T>(ref T value);

    private static void Edit<T>(Transaction tx, EntityId id, Comp<T> component, Mutate<T> change)
        where T : unmanaged
    {
        var e = tx.OpenMut(id);
        var v = e.Read(component);
        change(ref v);
        e.Set(component, v);
    }
}
