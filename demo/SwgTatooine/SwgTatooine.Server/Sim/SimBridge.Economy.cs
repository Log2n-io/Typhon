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
/// The economy (economy-foundation.md): wallets, a ledger row per transfer, and the one code path that writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One code path, in order</b> (economy-build § 1, rule 1). Every movement of credits — a character's starting grant, a mission's reward, a tip — goes
/// through <see cref="Transfer"/>: the two balances, the head and the row are written in the transaction that carries it and commit together or not at all.
/// Two serial systems call it, one after the other on the same tick and ordered by an edge: the mission desk pays a reward <b>in the transaction that ends
/// the mission</b>, so a mission is never ended unpaid or paid twice whatever fails; the <c>Ledger</c> opens wallets and applies tips in its own.
/// </para>
/// <para>
/// <b>In memory: the wallet of each owner, and the head.</b> Read from the database on the first use, so a reopened world resumes its sequence. A transaction
/// holding economy writes that fails to commit forgets both, and the next use reads them again; its counts are dropped with it.
/// </para>
/// <para>
/// <b>A wallet is opened only for a character bound on an earlier tick.</b> A first login spawns its character in the session system's transaction,
/// which commits into the tick's unit of work — visible to the side transactions later in the tick, but made durable only by the tick's flush at its end,
/// while a side transaction is durable on its own. Opened on the bind's tick, a crash between the two flushes would leave a wallet — and a grant — whose
/// owner does not exist. The bind is kept as a request and served on the next tick, once the tick that spawned the character has been flushed; a tip
/// or a reward meanwhile finds no wallet for that character and says so.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How long a bind's wallet request waits for its character to be visible before it is dropped; the next login asks again.</summary>
    private const int WalletAskSeconds = 10;

    private bool _economyLoaded;
    private EntityId _ledgerHead;
    private LedgerHead _head;

    // Owner key (a character's raw id) to its wallet.
    private readonly Dictionary<long, EntityId> _walletOf = [];

    // The bodies bound to a character, each with the tick it was bound on: kept until its wallet is open, or it is too old. And the characters bound on
    // the tick under way, whose wallets wait for the next.
    private readonly List<(EntityId Character, EntityId Body, long Tick)> _walletsAsked = [];
    private readonly List<(EntityId Character, EntityId Body)> _boundScratch = [];
    private readonly HashSet<EntityId> _boundThisTick = [];

    // This tick's tips, applied in the Ledger's transaction: refused after all if that transaction does not commit.
    private readonly List<ClientCommand<GiveCredits>> _tipsApplied = [];

    // Per tick: every character with a body online and that body, both ways; and the bodies whose wallet moved, the desk's and the Ledger's.
    private readonly Dictionary<EntityId, EntityId> _econBodies = [];
    private readonly Dictionary<EntityId, EntityId> _econCharacterOf = [];
    private readonly HashSet<EntityId> _walletsMoved = [];
    private readonly HashSet<EntityId> _deskWalletsMoved = [];

    private IndexRef _walletByOwner;
    private IndexRef _headByKey;

    // Written by the transaction under way, and counted only when it commits.
    private bool _economyDirty;
    private long _pendingTransfers;
    private long _pendingGrants;
    private long _pendingRewards;
    private long _pendingTips;

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

    /// <summary>Tips refused, whatever the reason — those of a transaction that did not commit included.</summary>
    public long TipsRefused => Interlocked.Read(ref _tipsRefused);

    /// <summary>Transactions holding economy writes that did not commit: every transfer in them was lost together, and the state was read again.</summary>
    public long EconomyCommitsFailed => Interlocked.Read(ref _economyCommitsFailed);

    /// <summary>The counters above as JSON, for <c>GET /swg/economy/stats</c> (economy-foundation.md E0-12).</summary>
    public string EconomyCountersJson()
        => FormattableString.Invariant(
            $"{{\"transfers\":{Transfers},\"walletsOpened\":{WalletsOpened},\"rewardsPaid\":{RewardsPaid},\"tips\":{Tips},\"tipsRefused\":{TipsRefused},\"commitsFailed\":{EconomyCommitsFailed}}}");

    /// <summary>
    /// A completed mission's payout, credited to its holder's wallet in the desk's own transaction — the one that destroys the mission — so the two commit
    /// together. Called by the mission desk, which ends the mission only when this paid it.
    /// </summary>
    /// <returns><see langword="false"/>, having paid nothing, when the holder has no wallet yet (bound on this very tick): the mission stays, paid by a later pass.</returns>
    private bool Reward(Transaction tx, EntityId character, EntityId body, EntityId mission, int reward, long worldTick)
    {
        EnsureEconomy(tx);
        OpenWallet(tx, character, worldTick);
        if (reward <= 0)
        {
            return true;
        }

        if (!Transfer(tx, EconomyLedger.World, (long)character.RawValue, reward, TransferKind.MissionReward, (long)mission.RawValue, worldTick))
        {
            return false;
        }

        _pendingRewards++;
        _deskWalletsMoved.Add(body);
        return true;
    }

    /// <summary>The desk's transaction ended: its rewards counted and their bodies told, or — not committed — the economy read again.</summary>
    private void EndDeskEconomy(Transaction tick0, bool committed)
    {
        if (EndEconomy(committed))
        {
            foreach (var body in _deskWalletsMoved)
            {
                BumpWallet(tick0, body);
            }
        }

        _deskWalletsMoved.Clear();
    }

    /// <summary>The <c>Ledger</c> system: the wallets asked for by a bind, then this tick's tips, in that order.</summary>
    public void LedgerTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var subs = ctx.Subscriptions;
        var tick = ctx.TickNumber;
        _boundScratch.Clear();
        TatooineReplication.DrainBodiesBound(_boundScratch);
        foreach (var (character, body) in _boundScratch)
        {
            _walletsAsked.Add((character, body, tick));
            _boundThisTick.Add(character);
        }

        var tips = subs == null ? default : subs.Commands<GiveCredits>();
        if (_economyLoaded && _walletsAsked.Count == 0 && tips.Count == 0)
        {
            _boundThisTick.Clear();
            return;
        }

        var tick0 = ctx.Transaction;
        var worldTick = ctx.WorldTick;
        _walletsMoved.Clear();
        _tipsApplied.Clear();
        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        EnsureEconomy(tx);
        foreach (var (character, body, asked) in _walletsAsked)
        {
            if (asked < tick && OpenWallet(tx, character, worldTick))
            {
                _walletsMoved.Add(body);
            }
        }

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

        var committed = EndEconomy(tx.Commit());
        _boundThisTick.Clear();
        if (!committed)
        {
            // Every transfer of this tick is lost together. The wallet requests stay for the next tick; the tips are refused, so their senders know.
            foreach (var command in _tipsApplied)
            {
                RefuseTip(subs, in command, TatooineReplication.TipFailed);
            }

            return;
        }

        var oldest = tick - ((long)_config.TickRateHz * WalletAskSeconds);
        _walletsAsked.RemoveAll(a => _walletOf.ContainsKey((long)a.Character.RawValue) || a.Tick < oldest);
        foreach (var body in _walletsMoved)
        {
            BumpWallet(tick0, body);
        }
    }

    /// <summary>Reads the wallets and the head on the first use after a start or a failed commit.</summary>
    private void EnsureEconomy(Transaction tx)
    {
        if (!_economyLoaded)
        {
            LoadEconomy(tx);
            _economyLoaded = true;
        }
    }

    /// <summary>
    /// A transaction that may hold economy writes ended. Committed, its counts are published; not, and it held any, everything this code remembers is read
    /// again on the next use.
    /// </summary>
    /// <returns>Whether it committed.</returns>
    private bool EndEconomy(bool committed)
    {
        if (_economyDirty)
        {
            if (committed)
            {
                Interlocked.Add(ref _transfers, _pendingTransfers);
                Interlocked.Add(ref _grants, _pendingGrants);
                Interlocked.Add(ref _rewardsPaid, _pendingRewards);
                Interlocked.Add(ref _tips, _pendingTips);
            }
            else
            {
                Interlocked.Increment(ref _economyCommitsFailed);
                _economyLoaded = false;
                _walletOf.Clear();
            }
        }

        _economyDirty = false;
        _pendingTransfers = 0;
        _pendingGrants = 0;
        _pendingRewards = 0;
        _pendingTips = 0;
        return committed;
    }

    /// <summary>Every wallet by its owner, and the head — created on a world's first use.</summary>
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
        _economyDirty = true;
    }

    /// <summary>
    /// Opens a character's wallet with its grant, unless it has one, or it was bound on the tick under way (not durable yet), or this transaction cannot see
    /// it.
    /// </summary>
    /// <returns><see langword="true"/> when it was opened now.</returns>
    private bool OpenWallet(Transaction tx, EntityId character, long worldTick)
    {
        var key = (long)character.RawValue;
        if (key == EconomyLedger.World || _walletOf.ContainsKey(key) || _boundThisTick.Contains(character) || !tx.TryOpen(character, out _))
        {
            return false;
        }

        var wallet = new Wallet { Owner = key };
        _walletOf[key] = tx.Spawn<WalletArch>(WalletArch.Purse.Set(in wallet));
        _economyDirty = true;
        Transfer(tx, EconomyLedger.World, key, EconomyLedger.StartingCredits, TransferKind.Grant, 0, worldTick);
        _pendingGrants++;
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
        _economyDirty = true;
        _pendingTransfers++;
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

        // Either side's wallet is opened here if its bind's request has not been served yet; one whose character is not committed yet has none.
        OpenWallet(tx, character, worldTick);
        OpenWallet(tx, recipient, worldTick);
        if (!_walletOf.ContainsKey((long)character.RawValue))
        {
            // Bound this very tick: its wallet opens on the next, and the tip can be sent again then.
            RefuseTip(subs, in command, TatooineReplication.TipFailed);
            return;
        }

        if (!_walletOf.ContainsKey((long)recipient.RawValue))
        {
            RefuseTip(subs, in command, TatooineReplication.TipNoRecipient);
            return;
        }

        if (!Transfer(tx, (long)character.RawValue, (long)recipient.RawValue, amount, TransferKind.Tip, 0, worldTick))
        {
            RefuseTip(subs, in command, TatooineReplication.TipNotEnough);
            return;
        }

        _pendingTips++;
        _tipsApplied.Add(command);
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
