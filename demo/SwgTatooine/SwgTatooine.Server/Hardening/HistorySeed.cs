using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SwgTatooine.Server;

/// <summary>What <see cref="SimBridge.SeedHistory"/> wrote.</summary>
public sealed class HistorySeedReport
{
    public int Characters;
    public long EconomyRows;
    public long ItemRows;
    public long Kills;
    public long Missions;
    public long VendorSales;
    public long Tips;
    public long TipsRefused;
    public long Commits;
    public double Seconds;
}

/// <summary>
/// The history seeder (<c>--seed-history</c>): a long-lived world's ledgers, written into a fresh one before it serves, for hardening — the page cache under
/// walks of a database many times its size, audits over tens of millions of rows beside a live crowd.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the real writers, never raw rows.</b> Every credit moves through <see cref="Transfer"/>, every item through <see cref="ItemTransfer"/> and
/// <see cref="PlaceDrop"/>, every wallet opens through <see cref="OpenWallet"/>, every mission pays through <see cref="Reward"/> then
/// <see cref="MintCache"/> — the desk's order — and every vendor sale is the market's two rows naming each other. The heads, the balances, the stacks and
/// the counters are the ones a live world keeps, so the economy, item and market audits pass on a seeded world as on a played one.
/// </para>
/// <para>
/// <b>The mix</b>, per step on a random character: a kill's loot, a mission's reward and cache, a vendor sale of one stack (forced once the inventory is
/// nearly full, so drops are seldom lost), or a tip to another character. Item rows run about four per credit row, the ratio swgclient measured in a soak.
/// </para>
/// <para>
/// <b>Characters of their own</b>: accounts named <c>seed0000001</c>…, never <c>bot…</c>, each with a character and an empty credential — nobody can log
/// in as one. A character exists before its wallet opens (the audits check it).
/// </para>
/// <para>
/// <b>One world tick for all of it</b>, the last one before the runtime's first: the audits require a ledger's ticks never to go back, and the live rows
/// that follow are stamped later. Before <c>TatooineReplication.Declare</c>, so no row queues a player notice.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Steps per transaction: big enough to amortise the commit, small enough to keep its change set and its unit of WAL modest.</summary>
    private const int SeedStepsPerCommit = 2_000;

    /// <summary>Characters per transaction while creating them.</summary>
    private const int SeedCharactersPerCommit = 1_000;

    /// <summary>Raw-id bases for the seeded kills' victims and missions: unique references, far from any real entity's.</summary>
    private const ulong SeedVictimBase = 0x7D00_0000_0000_0000UL;

    private const ulong SeedMissionBase = 0x7E00_0000_0000_0000UL;

    /// <summary>
    /// Seeds about <paramref name="economyRows"/> credit rows, and some four times as many item rows, across <paramref name="characters"/> new characters.
    /// For a fresh world only: the caller refuses a reopened one.
    /// </summary>
    public HistorySeedReport SeedHistory(long economyRows, int characters, int seed, Action<string> log)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(economyRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(characters, 2);
        var report = new HistorySeedReport();
        var started = Stopwatch.GetTimestamp();
        var rng = new Random(seed);
        var worldTick = Math.Max(0, Dbe.NextWorldTick - 1);
        var species = Enumerable.Range(0, 256).Where(s => LootTables.FamiliesOf(s).Length > 0).Select(s => (byte)s).ToArray();
        if (species.Length == 0)
        {
            throw new InvalidOperationException("no species has a loot family: nothing to seed");
        }

        // The characters, then their wallets: a wallet opens only for a character its transaction can see committed.
        var owners = new List<EntityId>(characters);
        for (var made = 0; made < characters;)
        {
            using var tx = Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
            var n = Math.Min(SeedCharactersPerCommit, characters - made);
            for (var i = 0; i < n; i++, made++)
            {
                var account = new Account { Name = (String64)$"seed{made + 1:D7}", CreatedUtcTicks = DateTime.UtcNow.Ticks };
                var accountId = tx.Spawn<AccountArch>(AccountArch.Identity.Set(in account), AccountArch.Secret.Set(default(AccountSecret)));
                var profile = new Character
                {
                    Account = accountId,
                    Name = (String64)$"Seed {made + 1}",
                    LastRealm = 0,
                    LastSavedUtcTicks = DateTime.UtcNow.Ticks,
                };
                owners.Add(tx.Spawn<CharacterArch>(CharacterArch.Profile.Set(profile), CharacterArch.Progress.Set(CharacterProgress.Start)));
            }

            if (!tx.Commit())
            {
                throw new InvalidOperationException("the seeded characters did not commit");
            }
        }

        report.Characters = characters;
        log($"seed: {characters:N0} characters in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");

        for (var opened = 0; opened < owners.Count;)
        {
            using var tx = Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
            EnsureEconomy(tx);
            EnsureItems(tx);
            var n = Math.Min(SeedCharactersPerCommit, owners.Count - opened);
            for (var i = 0; i < n; i++, opened++)
            {
                OpenWallet(tx, owners[opened], worldTick);
            }

            SeedCommit(tx, report);
        }

        var economyBase = _head.LastSeq;
        var itemBase = _itemHead.LastSeq;
        ulong victims = 0;
        ulong missions = 0;
        var nextLog = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (_head.LastSeq - economyBase < economyRows)
        {
            using var tx = Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
            EnsureEconomy(tx);
            EnsureItems(tx);
            for (var step = 0; step < SeedStepsPerCommit; step++)
            {
                var who = owners[rng.Next(owners.Count)];
                var key = (long)who.RawValue;
                var items = ItemsOf(key);
                var used = 0;
                for (var s = 1; s <= ItemLedger.Slots; s++)
                {
                    used += items.Slots[s].Count > 0 ? 1 : 0;
                }

                // Kills 70 %, missions 12 %, vendor sales 8 % and whenever the inventory is nearly full, tips 10 %.
                var roll = rng.Next(100);
                if (used >= ItemLedger.Slots - 4 || (roll >= 82 && roll < 90 && used > 0))
                {
                    SeedVendorSale(tx, key, items, rng, worldTick, report);
                }
                else if (roll < 70)
                {
                    var victim = EntityId.FromRawValue(SeedVictimBase + ++victims);
                    var source = rng.Next(10) == 0 ? DropSource.WildCache : DropSource.Kill;
                    var drop = LootTables.Roll(species[rng.Next(species.Length)], 1 + rng.Next(50), source, victim.RawValue, worldTick);
                    PlaceDrop(tx, who, in drop, source == DropSource.WildCache ? ItemMoveKind.Lair : ItemMoveKind.Kill, source, (long)victim.RawValue,
                        worldTick);
                    report.Kills++;
                }
                else if (roll < 82)
                {
                    // The desk's order: the reward, then the cache, in one transaction and on one tick (the item audit's missionTick rule).
                    var mission = EntityId.FromRawValue(SeedMissionBase + ++missions);
                    var held = new HeldMission { Kind = species[rng.Next(species.Length)], Level = (byte)(1 + rng.Next(50)), Reward = 100 + rng.Next(2_900) };
                    if (Reward(tx, who, EntityId.Null, mission, held.Reward, worldTick))
                    {
                        MintCache(tx, who, EntityId.Null, mission, in held, worldTick);
                        report.Missions++;
                    }
                }
                else
                {
                    var to = owners[rng.Next(owners.Count)];
                    if (to != who && Transfer(tx, key, (long)to.RawValue, 1 + rng.Next(200), TransferKind.Tip, 0, worldTick))
                    {
                        report.Tips++;
                    }
                    else
                    {
                        report.TipsRefused++;
                    }
                }
            }

            SeedCommit(tx, report);
            if (Stopwatch.GetTimestamp() >= nextLog)
            {
                nextLog = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
                var secs = Stopwatch.GetElapsedTime(started).TotalSeconds;
                log($"seed: {_head.LastSeq - economyBase:N0} credit rows, {_itemHead.LastSeq - itemBase:N0} item rows, {secs:F0} s, "
                    + $"{(_head.LastSeq - economyBase) / secs:N0} credit rows/s");
            }
        }

        report.EconomyRows = _head.LastSeq;
        report.ItemRows = _itemHead.LastSeq;
        report.Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return report;
    }

    /// <summary>A vendor sale of one stack, as the market writes it (SimBridge.Market.cs): the item rows and the credit row name each other.</summary>
    private void SeedVendorSale(Transaction tx, long seller, OwnerItems items, Random rng, long worldTick, HistorySeedReport report)
    {
        var start = 1 + rng.Next(ItemLedger.Slots);
        for (var i = 0; i < ItemLedger.Slots; i++)
        {
            var s = 1 + ((start - 1 + i) % ItemLedger.Slots);
            var slot = items.Slots[s];
            if (slot.Count <= 0)
            {
                continue;
            }

            var count = Math.Min(slot.Count, UnitsOf(items, slot.Template, slot.Mk));
            var payout = MarketValues.VendorPayout(slot.Template, slot.Mk, count);
            if (payout <= 0)
            {
                continue;
            }

            var firstItemSeq = _itemHead.LastSeq + 1;
            var creditSeq = _head.LastSeq + 1;
            if (ItemTransfer(tx, seller, ItemLedger.World, slot.Template, slot.Mk, count, ItemMoveKind.Vendor, creditSeq, worldTick) != count)
            {
                throw new InvalidOperationException($"seed: a vendor sale of {count} units held by {seller} moved fewer");
            }

            if (!Transfer(tx, EconomyLedger.World, seller, payout, TransferKind.Vendor, firstItemSeq, worldTick))
            {
                throw new InvalidOperationException($"seed: the vendor payout to {seller} was refused");
            }

            report.VendorSales++;
            return;
        }
    }

    /// <summary>Commits a seeding transaction as the desk does: the item head first, then the economy and the items told how it ended.</summary>
    private void SeedCommit(Transaction tx, HistorySeedReport report)
    {
        FlushItemHead(tx);
        var committed = tx.Commit();
        EndEconomy(committed);
        EndItems(committed);
        _deskWalletsMoved.Clear();
        _deskItemsMoved.Clear();
        if (!committed)
        {
            throw new InvalidOperationException("a seeding transaction did not commit");
        }

        report.Commits++;
    }
}
