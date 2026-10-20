using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// A session's held keys (#1407) without a runtime: each side costs what it holds, the commit swaps the sides, a frame that wrote none commits nothing, a
/// RESET reads the committed keys as none until it is published; and the server keys' one-pass merge against a plain model.
/// </summary>
[TestFixture]
[VerifiesRule("SUB-33")]
class GroupSessionStateTests
{
    private static void Write(GroupSessionState gs, int set, params (long Key, uint Cursor)[] keys)
    {
        var held = gs.BeginPending(set, keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            held.Keys[i] = keys[i].Key;
            held.Cursors[i] = keys[i].Cursor;
        }

        held.Count = keys.Length;
    }

    private static long[] Keys(HeldKeys held) => held.KeySpan.ToArray();

    [Test]
    public void ASideCostsWhatItHoldsGrowingAndShrinking()
    {
        var gs = new GroupSessionState(2);
        Assert.That(gs.CapacityForTest(0, committed: true), Is.Zero, "nothing held, nothing allocated");

        gs.BeginFrame();
        Write(gs, 0, (1, 0u), (2, 0u), (3, 0u));
        Assert.That(gs.CapacityForTest(0, committed: false), Is.EqualTo(GroupSessionState.InitialCapacity));
        Assert.That(gs.CapacityForTest(1, committed: false), Is.Zero, "a set with no key allocates nothing");
        gs.Commit();

        gs.BeginFrame();
        var many = Enumerable.Range(1, 1000).Select(k => ((long)k, 0u)).ToArray();
        Write(gs, 0, many);
        Assert.That(gs.CapacityForTest(0, committed: false), Is.EqualTo(1024), "grown to the next power of two");
        gs.Commit();

        // The free side is the old committed one, 8 keys: a frame of 1 000 again gets a holder of its own, then the two alternate with no allocation.
        for (var f = 0; f < 3; f++)
        {
            gs.BeginFrame();
            Write(gs, 0, many);
            gs.Commit();
        }

        Assert.That(gs.CapacityForTest(0, committed: true), Is.EqualTo(1024));
        Assert.That(gs.CapacityForTest(0, committed: false), Is.EqualTo(1024));

        // Down to ten: four times too large, the next pending side is a small one.
        gs.BeginFrame();
        Write(gs, 0, many[..10]);
        Assert.That(gs.CapacityForTest(0, committed: false), Is.EqualTo(16));
        Assert.That(gs.Pending(0).Count, Is.EqualTo(10));
    }

    [Test]
    public void TheCommitSwapsTheSidesAndAFrameThatWroteNoneCommitsNothing()
    {
        var gs = new GroupSessionState(1);
        gs.BeginFrame();
        Write(gs, 0, (5, GroupSessionState.Complete), (9, 4u));
        gs.SetPendingOver(0, 3);
        Assert.Multiple(() =>
        {
            Assert.That(gs.Committed(0).Count, Is.Zero, "not yet published");
            Assert.That(Keys(gs.Pending(0)), Is.EqualTo(new long[] { 5, 9 }));
            Assert.That(gs.PendingIsCommitted(0), Is.False);
        });

        gs.Commit();
        Assert.Multiple(() =>
        {
            Assert.That(Keys(gs.Committed(0)), Is.EqualTo(new long[] { 5, 9 }));
            Assert.That(gs.Committed(0).CursorOf(9), Is.EqualTo(4u));
            Assert.That(gs.Committed(0).CursorOf(7), Is.Zero, "not held");
            Assert.That(gs.CommittedOver(0), Is.EqualTo(3));
            Assert.That(gs.PendingIsCommitted(0), Is.True, "a frame not written reads as the committed one");
            Assert.That(gs.IsFilling(), Is.True, "key 9 still fills");
        });

        // An unplaced frame holds what was held: the commit after it leaves the committed side as it was.
        gs.Hold(reset: false);
        gs.Commit();
        Assert.That(Keys(gs.Committed(0)), Is.EqualTo(new long[] { 5, 9 }));

        // A frame written the same as the committed side is a steady state.
        gs.BeginFrame();
        Write(gs, 0, (5, GroupSessionState.Complete), (9, 4u));
        Assert.That(gs.PendingIsCommitted(0), Is.True);
        gs.SetPendingOver(0, 3);
        gs.Commit();
        Assert.That(Keys(gs.Committed(0)), Is.EqualTo(new long[] { 5, 9 }));
    }

    [Test]
    public void AResetReadsTheCommittedKeysAsNoneUntilItIsPublished()
    {
        var gs = new GroupSessionState(1);
        gs.BeginFrame();
        Write(gs, 0, (5, GroupSessionState.Complete));
        gs.SetPendingOver(0, 2);
        gs.Commit();

        gs.Reset = true;
        gs.BeginFrame();
        Write(gs, 0, (6, 0u));
        Assert.Multiple(() =>
        {
            Assert.That(gs.Committed(0).Count, Is.Zero, "the frame reads none");
            Assert.That(gs.CommittedOver(0), Is.Zero, "and the client was told nothing");
            Assert.That(Keys(gs.Published(0)), Is.EqualTo(new long[] { 5 }), "the client still holds 5 until the RESET is published");
        });

        // Not published: the next frame starts again from the published side.
        gs.Reset = false;
        gs.Hold(reset: false);
        Assert.That(Keys(gs.Pending(0)), Is.EqualTo(new long[] { 5 }));

        // A RESET held while unplaced empties every set once published.
        gs.Hold(reset: true);
        gs.Commit();
        Assert.Multiple(() =>
        {
            Assert.That(gs.Committed(0).Count, Is.Zero);
            Assert.That(gs.CommittedOver(0), Is.Zero);
        });
    }

    [Test]
    public void GrowingThePendingSideKeepsItsKeysAndClearForgetsEverything()
    {
        var gs = new GroupSessionState(1);
        gs.BeginFrame();
        Write(gs, 0, (1, 0u), (2, 0u));
        var grown = gs.GrowPending(0, 100);
        Assert.Multiple(() =>
        {
            Assert.That(grown.Capacity, Is.EqualTo(128));
            Assert.That(Keys(grown), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(gs.Pending(0), Is.SameAs(grown));
        });

        gs.Commit();
        gs.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(gs.Committed(0).Count, Is.Zero);
            Assert.That(gs.Pending(0).Count, Is.Zero);
            Assert.That(gs.CapacityForTest(0, committed: true), Is.Zero);
        });
    }

    /// <summary>
    /// The server keys' merge against a plain model, over two sets of very different sizes and keys of both signs: the client's keys kept, the server's
    /// replaced, new ones in key order while there is room — each set's arrays its own, whatever the other set's merges did.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void TheServerKeysMergeAgreesWithAModel(int seed)
    {
        const int max = 40;
        var random = new Random(seed);
        var sets = new[]
        {
            new CompiledGroupSet { Selector = GroupSelector.Explicit, Members = [] },
            new CompiledGroupSet { Selector = GroupSelector.Explicit, Members = [] },
        };
        var state = new SessionGroupRequests(sets, max);
        var models = new[] { new SortedDictionary<long, byte>(), new SortedDictionary<long, byte>() };
        for (var round = 0; round < 400; round++)
        {
            // Set 0 takes up to 50 keys a merge, set 1 up to 4: the larger one's arrays must never end up the smaller one's.
            var set = random.Next(2);
            var model = models[set];
            var spread = set == 0 ? 50 : 4;
            if (set == 0 && random.Next(3) == 0)
            {
                // A client request, or its withdrawal: the large set's only.
                var key = (long)random.Next(-40, 40);
                key = key == 0 ? 1 : key;
                if (random.Next(2) == 0)
                {
                    var added = state.Add(set, key, SessionGroupRequests.FromClient);
                    var room = model.ContainsKey(key) || model.Count < max;
                    Assert.That(added, Is.EqualTo(room));
                    if (room)
                    {
                        model[key] = (byte)(model.GetValueOrDefault(key) | SessionGroupRequests.FromClient);
                    }
                }
                else
                {
                    state.Remove(set, key, SessionGroupRequests.FromClient);
                    if (model.TryGetValue(key, out var bits) && (bits &= unchecked((byte)~SessionGroupRequests.FromClient)) == 0)
                    {
                        model.Remove(key);
                    }
                    else if (model.ContainsKey(key))
                    {
                        model[key] = bits;
                    }
                }
            }
            else
            {
                var keys = Enumerable.Range(0, random.Next(0, spread)).Select(_ => (long)random.Next(-40, 40)).ToArray();
                Array.Sort(keys);
                var over = state.MergeServerKeys(set, keys);
                Assert.That(over, Is.EqualTo(ModelServerKeys(model, keys, max)), $"round {round}");
            }

            for (var s = 0; s < 2; s++)
            {
                Assert.That(state.Keys[s].AsSpan(0, state.Count[s]).ToArray(), Is.EqualTo(models[s].Keys.ToArray()), $"round {round}, set {s}");
                Assert.That(state.Origin[s].AsSpan(0, state.Count[s]).ToArray(), Is.EqualTo(models[s].Values.ToArray()), $"round {round}, set {s}");
            }
        }

        // Each set's arrays follow its own size: the small set never holds the large one's.
        Assert.That(state.Keys[1].Length, Is.LessThanOrEqualTo(8), "the small set's arrays are its own");
    }

    /// <summary>
    /// The member-log match against a plain filter: the entries of one set filed under a key held before or after the frame, in log order — through the
    /// branch that tests each filing (few filings) and the one that looks each held key up (few keys) alike.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void TheMemberLogMatchAgreesWithAFilter(int seed)
    {
        var random = new Random(seed);
        using var scratch = new FrameWorkerScratch();
        for (var round = 0; round < 300; round++)
        {
            // Filings over three sets, keys of both signs, repeats; held keys from a few to many, so both branches run.
            var log = Enumerable.Range(0, random.Next(0, 60))
                .Select(i => new MemberEntry { Set = random.Next(3), Key = random.Next(-20, 20), Event = i })
                .OrderBy(e => e.Set).ThenBy(e => e.Key).ThenBy(e => e.Event).ToArray();
            var committed = Held(random, random.Next(0, round % 2 == 0 ? 4 : 40));
            var pending = Held(random, random.Next(0, round % 2 == 0 ? 4 : 40));
            var set = random.Next(3);

            var count = PushReplication<PushEvent>.MatchHeld(log, log.Length, set, committed, pending, scratch);
            var expected = Enumerable.Range(0, log.Length)
                .Where(i => log[i].Set == set && (committed.IndexOf(log[i].Key) >= 0 || pending.IndexOf(log[i].Key) >= 0)).ToArray();
            Assert.That(scratch.HeldMatches.AsSpan(0, count).ToArray(), Is.EqualTo(expected), $"round {round}");
        }
    }

    private static HeldKeys Held(Random random, int count)
    {
        var keys = Enumerable.Range(0, count).Select(_ => (long)random.Next(-20, 20)).Distinct().OrderBy(k => k).ToArray();
        var held = new HeldKeys(Math.Max(1, keys.Length)) { Count = keys.Length };
        keys.CopyTo(held.Keys, 0);
        return held;
    }

    // The old two-pass form: off every server key no longer named, then on each named one in key order while the set has room.
    private static int ModelServerKeys(SortedDictionary<long, byte> model, long[] keys, int max)
    {
        var named = keys.Where(k => k != 0).ToHashSet();
        foreach (var key in model.Keys.ToArray())
        {
            if ((model[key] & SessionGroupRequests.FromServer) != 0 && !named.Contains(key))
            {
                var bits = (byte)(model[key] & ~SessionGroupRequests.FromServer);
                if (bits == 0)
                {
                    model.Remove(key);
                }
                else
                {
                    model[key] = bits;
                }
            }
        }

        var over = 0;
        foreach (var key in named.OrderBy(k => k))
        {
            if (model.TryGetValue(key, out var bits))
            {
                model[key] = (byte)(bits | SessionGroupRequests.FromServer);
            }
            else if (model.Count < max)
            {
                model[key] = SessionGroupRequests.FromServer;
            }
            else
            {
                over++;
            }
        }

        return over;
    }
}
