using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>The ledger's fixed points: World's account key, the head's key, and what a new character is given.</summary>
public static class EconomyLedger
{
    /// <summary>The account money enters from and leaves to. No character has raw id 0, so it names no wallet.</summary>
    public const long World = 0;

    /// <summary>The one <see cref="LedgerHead"/>'s <see cref="LedgerHead.Key"/>.</summary>
    public const int HeadKey = 1;

    /// <summary>The credits a character's wallet opens with, granted by World.</summary>
    public const long StartingCredits = 1_000;

    /// <summary>The most one <c>/tip</c> may move: a mistyped amount is refused rather than emptying a wallet.</summary>
    public const long MaxTip = 1_000_000;

    /// <summary>How close, in metres between the two bodies, a tip may be given: the two people are standing together.</summary>
    public const float TipReachM = 20f;
}

/// <summary>
/// The economy (economy-foundation.md): wallets, a ledger row per transfer, and the <c>Ledger</c> system that is their only writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer, in order</b> (economy-build § 1, rule 1). Every movement of credits — a character's starting grant, a mission's reward, a tip — is applied
/// here, one after the other, in one transaction: the two balances, the head and the row commit together or not at all. Nothing else declares a write of
/// <see cref="Wallet"/>, <see cref="LedgerEntry"/> or <see cref="LedgerHead"/>, so last-writer-wins never meets two writers.
/// </para>
/// <para>
/// <b>Requests, not writes.</b> Other systems hand their transfers over: a body bound to a character (the session system, Input phase) asks for its wallet,
/// a completed mission (the desk, earlier in the Economy phase) for its reward. Both run before this on the same tick, one after the other, so the request
/// lists are plain lists. Commands — <see cref="GiveCredits"/> — are read here directly, in arrival order.
/// </para>
/// <para>
/// <b>In memory: the wallet of each owner, and the head.</b> Read from the database on the first tick, so a reopened world resumes its sequence; maintained by
/// this system alone afterwards. A transaction that fails to commit forgets both, and the next tick reads them again.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    private bool _economyLoaded;
    private EntityId _ledgerHead;
    private LedgerHead _head;

    // Owner key (a character's raw id) to its wallet.
    private readonly Dictionary<long, EntityId> _walletOf = [];

    // This tick's requests, from systems that ran earlier in it: the bodies the session system bound, and the rewards the desk owes.
    private readonly List<(EntityId Character, EntityId Body)> _walletsAsked = [];
    private readonly List<(EntityId Character, EntityId Body, EntityId Mission, int Reward)> _rewardsOwed = [];

    // Per tick: every character with a body online and that body, both ways; and the bodies whose wallet moved.
    private readonly Dictionary<EntityId, EntityId> _econBodies = [];
    private readonly Dictionary<EntityId, EntityId> _econCharacterOf = [];
    private readonly HashSet<EntityId> _walletsMoved = [];

    private IndexRef _walletByOwner;
    private IndexRef _headByKey;

    private long _transfers;
    private long _grants;
    private long _rewardsPaid;
    private long _tips;
    private long _tipsRefused;
    private long _economyCommitsFailed;

    /// <summary>Ledger rows written since start.</summary>
    public long Transfers => Interlocked.Read(ref _transfers);

    /// <summary>Wallets opened, each with its grant.</summary>
    public long WalletsOpened => Interlocked.Read(ref _grants);

    /// <summary>Mission rewards paid into a wallet.</summary>
    public long RewardsPaid => Interlocked.Read(ref _rewardsPaid);

    /// <summary>Tips applied.</summary>
    public long Tips => Interlocked.Read(ref _tips);

    /// <summary>Tips refused, whatever the reason.</summary>
    public long TipsRefused => Interlocked.Read(ref _tipsRefused);

    /// <summary>Economy transactions that did not commit: every transfer in them was lost together, and the state was read again.</summary>
    public long EconomyCommitsFailed => Interlocked.Read(ref _economyCommitsFailed);

    /// <summary>The counters above as JSON, for <c>GET /swg/economy/stats</c> (economy-foundation.md E0-12).</summary>
    public string EconomyCountersJson()
        => FormattableString.Invariant(
            $"{{\"transfers\":{Transfers},\"walletsOpened\":{WalletsOpened},\"rewardsPaid\":{RewardsPaid},\"tips\":{Tips},\"tipsRefused\":{TipsRefused},\"commitsFailed\":{EconomyCommitsFailed}}}");

    /// <summary>
    /// A completed mission's payout, handed to the economy: the <c>Ledger</c> system credits it to the holder's wallet later in this tick. Called by the
    /// mission desk.
    /// </summary>
    private void Reward(Transaction tick0, EntityId character, EntityId body, EntityId mission, int reward)
    {
        _rewardsOwed.Add((character, body, mission, reward));
        Interlocked.Increment(ref _missionRewards);
        if (tick0.TryOpenMut(body, out var player))
        {
            var state = player.Read(Player.State);
            state.MissionsCompleted++;
            player.Set(Player.State, state);
        }
    }

    /// <summary>The <c>Ledger</c> system: wallets asked for, rewards owed, then this tick's tips, in that order.</summary>
    public void LedgerTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var subs = ctx.Subscriptions;
        TatooineReplication.DrainBodiesBound(_walletsAsked);
        var tips = subs == null ? default : subs.Commands<GiveCredits>();
        if (_economyLoaded && _walletsAsked.Count == 0 && _rewardsOwed.Count == 0 && tips.Count == 0)
        {
            return;
        }

        var tick0 = ctx.Transaction;
        var worldTick = ctx.WorldTick;
        _walletsMoved.Clear();
        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        if (!_economyLoaded)
        {
            LoadEconomy(tx);
            _economyLoaded = true;
        }

        foreach (var (character, body) in _walletsAsked)
        {
            if (OpenWallet(tx, character.RawValue, worldTick))
            {
                _walletsMoved.Add(body);
            }
        }

        _walletsAsked.Clear();
        foreach (var (character, body, mission, reward) in _rewardsOwed)
        {
            // A wallet asked for and never opened — a character from before wallets — is opened now, so the reward has somewhere to go.
            OpenWallet(tx, character.RawValue, worldTick);
            if (reward > 0 && Transfer(tx, EconomyLedger.World, (long)character.RawValue, reward, TransferKind.MissionReward, (long)mission.RawValue, worldTick))
            {
                Interlocked.Increment(ref _rewardsPaid);
                _walletsMoved.Add(body);
            }
        }

        _rewardsOwed.Clear();
        if (tips.Count > 0)
        {
            TatooineReplication.FillCharacterBodies(_econBodies);
            _econCharacterOf.Clear();
            foreach (var (character, body) in _econBodies)
            {
                _econCharacterOf[body] = character;
            }

            foreach (var command in tips)
            {
                TipFromCommand(tx, tick0, subs, in command, worldTick);
            }
        }

        if (!tx.Commit())
        {
            // Every transfer of this tick is lost together, and what this system remembers is no longer what the database holds: read it again next tick.
            Interlocked.Increment(ref _economyCommitsFailed);
            _economyLoaded = false;
            _walletOf.Clear();
            return;
        }

        foreach (var body in _walletsMoved)
        {
            BumpWallet(tick0, body);
        }
    }

    /// <summary>Every wallet by its owner, and the head — created on a world's first tick.</summary>
    private void LoadEconomy(Transaction tx)
    {
        _walletByOwner = Dbe.GetIndexRef<Wallet, long>(w => w.Owner);
        _headByKey = Dbe.GetIndexRef<LedgerHead, int>(h => h.Key);
        _walletOf.Clear();
        using (var wallets = tx.EnumerateIndex<Wallet, long>(_walletByOwner, long.MinValue, long.MaxValue))
        {
            while (wallets.MoveNext())
            {
                _walletOf[wallets.CurrentComponent.Owner] = EntityId.FromRawValue((ulong)wallets.CurrentEntityPK);
            }
        }

        using (var heads = tx.EnumerateIndex<LedgerHead, int>(_headByKey, EconomyLedger.HeadKey, EconomyLedger.HeadKey))
        {
            if (heads.MoveNext())
            {
                _ledgerHead = EntityId.FromRawValue((ulong)heads.CurrentEntityPK);
                _head = heads.CurrentComponent;
                return;
            }
        }

        _head = new LedgerHead { Key = EconomyLedger.HeadKey };
        _ledgerHead = tx.Spawn<LedgerHeadArch>(LedgerHeadArch.Head.Set(in _head));
    }

    /// <summary>Opens an owner's wallet with its grant, unless it has one.</summary>
    /// <returns><see langword="true"/> when it was opened now.</returns>
    private bool OpenWallet(Transaction tx, ulong owner, long worldTick)
    {
        var key = (long)owner;
        if (key == EconomyLedger.World || _walletOf.ContainsKey(key))
        {
            return false;
        }

        var wallet = new Wallet { Owner = key };
        _walletOf[key] = tx.Spawn<WalletArch>(WalletArch.Purse.Set(in wallet));
        Transfer(tx, EconomyLedger.World, key, EconomyLedger.StartingCredits, TransferKind.Grant, 0, worldTick);
        Interlocked.Increment(ref _grants);
        return true;
    }

    /// <summary>
    /// Moves <paramref name="amount"/> from one account to another and appends its row: the one place a balance changes.
    /// </summary>
    /// <returns><see langword="false"/>, having written nothing, when either side has no wallet or the payer cannot cover it.</returns>
    private bool Transfer(Transaction tx, long from, long to, long amount, byte kind, long reference, long worldTick)
    {
        if (amount <= 0 || from == to)
        {
            return false;
        }

        var fromWallet = EntityId.Null;
        var toWallet = EntityId.Null;
        if ((from != EconomyLedger.World && !_walletOf.TryGetValue(from, out fromWallet)) || (to != EconomyLedger.World && !_walletOf.TryGetValue(to, out toWallet)))
        {
            return false;
        }

        long fromAfter;
        long toAfter;
        var seq = _head.LastSeq + 1;
        if (from == EconomyLedger.World)
        {
            fromAfter = _head.World - amount;
        }
        else
        {
            var payer = tx.OpenMut(fromWallet);
            var w = payer.Read(WalletArch.Purse);
            if (w.Balance < amount)
            {
                return false;
            }

            w.Balance -= amount;
            w.LastSeq = seq;
            w.Transfers++;
            payer.Set(WalletArch.Purse, w);
            fromAfter = w.Balance;
        }

        if (to == EconomyLedger.World)
        {
            toAfter = (from == EconomyLedger.World ? fromAfter : _head.World) + amount;
        }
        else
        {
            var payee = tx.OpenMut(toWallet);
            var w = payee.Read(WalletArch.Purse);
            w.Balance += amount;
            w.LastSeq = seq;
            w.Transfers++;
            payee.Set(WalletArch.Purse, w);
            toAfter = w.Balance;
        }

        _head.LastSeq = seq;
        if (from == EconomyLedger.World)
        {
            _head.World = fromAfter;
        }
        else if (to == EconomyLedger.World)
        {
            _head.World = toAfter;
        }

        tx.OpenMut(_ledgerHead).Set(LedgerHeadArch.Head, _head);
        var row = new LedgerEntry
        {
            Seq = seq,
            WorldTick = worldTick,
            From = from,
            To = to,
            Amount = amount,
            FromAfter = fromAfter,
            ToAfter = toAfter,
            Ref = reference,
            Kind = kind,
        };
        tx.Spawn<LedgerArch>(LedgerArch.Entry.Set(in row));
        Interlocked.Increment(ref _transfers);
        return true;
    }

    /// <summary>A person's (or a bot's) tip, validated against the world rather than the message.</summary>
    private void TipFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<GiveCredits> command, long worldTick)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character))
        {
            RefuseTip(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var amount = (long)command.Value.Amount;
        if (amount <= 0 || amount > EconomyLedger.MaxTip)
        {
            RefuseTip(subs, in command, TatooineReplication.TipBadAmount);
            return;
        }

        // TryResolve (SUB-26): a client may only name what it was shown. A creature, a structure, the scripted crowd — anything without a character — has
        // no wallet to receive it.
        if (!subs.TryResolve(command.Session, command.Value.Target, out var target) || !_econCharacterOf.TryGetValue(target, out var recipient))
        {
            RefuseTip(subs, in command, TatooineReplication.TipNoRecipient);
            return;
        }

        if (recipient == character)
        {
            RefuseTip(subs, in command, TatooineReplication.TipSelf);
            return;
        }

        if (!tick0.TryOpen(body, out var giver) || !tick0.TryOpen(target, out var taker))
        {
            RefuseTip(subs, in command, TatooineReplication.TipNoRecipient);
            return;
        }

        var a = giver.Read(Player.Bounds);
        var b = taker.Read(Player.Bounds);
        var reach = EconomyLedger.TipReachM;
        if (giver.Read(Player.Realm).Value != taker.Read(Player.Realm).Value || Distance2(a.X, a.Z, b.X, b.Z) > reach * reach)
        {
            RefuseTip(subs, in command, TatooineReplication.TipTooFar);
            return;
        }

        OpenWallet(tx, character.RawValue, worldTick);
        OpenWallet(tx, recipient.RawValue, worldTick);
        if (!Transfer(tx, (long)character.RawValue, (long)recipient.RawValue, amount, TransferKind.Tip, 0, worldTick))
        {
            RefuseTip(subs, in command, TatooineReplication.TipNotEnough);
            return;
        }

        Interlocked.Increment(ref _tips);
        _walletsMoved.Add(body);
        _walletsMoved.Add(target);
    }

    private void RefuseTip(SubscriptionsCommands subs, in ClientCommand<GiveCredits> command, byte reason)
    {
        subs.Reject(command, reason);
        Interlocked.Increment(ref _tipsRefused);
    }

    /// <summary>Moves the body's wallet revision, so its client re-reads <c>GET /swg/wallet</c>.</summary>
    private static void BumpWallet(Transaction tick0, EntityId body)
    {
        if (!tick0.TryOpenMut(body, out var player))
        {
            return;
        }

        var state = player.Read(Player.State);
        state.WalletRev++;
        player.Set(Player.State, state);
        TatooineReplication.Replicate(in player);
    }
}
