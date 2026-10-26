using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Two commits that want one key in a unique index: the loser is refused BEFORE its append, with the exception its caller handles, and the engine goes on
/// committing (AP-03, #1390).
/// </summary>
/// <remarks>
/// <para>
/// <b>The single-threaded case has been covered since #675 and it was never the hard one.</b> <c>ValidatePublishBeforeAppend</c> descends the index and
/// refuses a key another entity holds, deliberately before the point of no return so the refusal is a clean rollback. But the descent can only see keys
/// that have PUBLISHED, and in a race nobody has published yet: every racer found the key free, every racer appended, and the first publish to insert it
/// won while the others threw <c>UniqueConstraintViolationException</c> out of <c>BTree.Insert</c> — past the append, where AP-02 allows no rollback and
/// the only remaining answer is to stop the engine. A caller asking "is this name taken?" got <c>CommitPipelineStopped</c> and a database to reopen.
/// </para>
/// <para>
/// So these cases run the race rather than the sequence, and they assert the EXCEPTION TYPE rather than just that something failed — the defect's whole
/// signature is that the right refusal came wrapped in the wrong one. The barrier is what makes it a race: every thread has its transaction open and its
/// payload built before any of them commits, which is the staging the old code needed to fail and the probabilistic version did not reliably give.
/// </para>
/// </remarks>
[TestFixture]
class UniqueKeyRaceTests : TestBase<UniqueKeyRaceTests>
{
    /// <summary>The contended key. <c>CompD.B</c> is the unique index; <c>A</c> and <c>C</c> are AllowMultiple and carry no constraint.</summary>
    private const int ContendedKey = 4_242;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>What one racer did: the entity it created, or the exception that refused it.</summary>
    private readonly record struct Outcome(EntityId Created, Exception Refusal);

    /// <summary>
    /// Runs <paramref name="racers"/> threads that each spawn <see cref="ContendedKey"/> and commit, all released from one barrier so they contend.
    /// </summary>
    private static Outcome[] Race(DatabaseEngine dbe, int racers)
    {
        var outcomes = new Outcome[racers];
        using var start = new Barrier(racers);
        var threads = new Task[racers];

        for (var i = 0; i < racers; i++)
        {
            var slot = i;
            threads[slot] = Task.Factory.StartNew(
                () =>
                {
                    using var tx = dbe.CreateQuickTransaction();

                    // Staged before the barrier: the spawn is prepared, so what the barrier releases is the COMMIT of each racer and not its set-up. The
                    // value differs per racer in its AllowMultiple fields so a winner is identifiable, and collides in B, which is the unique one.
                    var id = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(slot, ContendedKey, slot)));
                    start.SignalAndWait();

                    try
                    {
                        tx.Commit();
                        outcomes[slot] = new Outcome(id, null);
                    }
                    catch (Exception ex)
                    {
                        outcomes[slot] = new Outcome(default, ex);
                    }
                },
                CancellationToken.None,
                // Dedicated threads, not pool work items: eight pool items can be dispatched onto fewer threads than that, and a racer parked on the
                // barrier while its peers wait for a thread is a sequence wearing a race's clothes.
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(threads);
        return outcomes;
    }

    /// <summary>
    /// Eight threads spawn one unique key at once: exactly one commits, the other seven get <see cref="UniqueConstraintViolationException"/>, and none of
    /// them gets a durability failure — which is what a refusal discovered past the append reads as.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [Repeat(8)]
    public void EightSpawnsOfOneUniqueKey_LeaveOneWinnerAndSevenCatchableRefusals()
    {
        using var dbe = SetupEngine();
        AssertOneWinnerAndCatchableRefusals(dbe);
    }

    /// <summary>The verifier, over an engine the caller owns: the mutant below needs its flag set on the engine the racers commit against.</summary>
    private static void AssertOneWinnerAndCatchableRefusals(DatabaseEngine dbe)
    {
        var outcomes = Race(dbe, 8);

        var winners = new List<EntityId>();
        var wrong = new List<Exception>();
        var refusals = 0;
        foreach (ref readonly var outcome in outcomes.AsSpan())
        {
            if (outcome.Refusal == null)
            {
                winners.Add(outcome.Created);
            }
            else if (outcome.Refusal is UniqueConstraintViolationException)
            {
                refusals++;
            }
            else
            {
                wrong.Add(outcome.Refusal);
            }
        }

        Assert.Multiple(() =>
        {
            // Named first because it is the defect: a DurabilityException or a CommitPublishIncompleteException here means the collision was found in the
            // publish, and the engine stopped rather than refusing one commit.
            Assert.That(wrong, Is.Empty,
                $"a racer failed with something other than a unique-constraint violation, so the collision was not caught before the append: "
                + $"{(wrong.Count == 0 ? "" : string.Join(" | ", wrong.ConvertAll(e => $"{e.GetType().Name}: {e.Message}")))}");
            Assert.That(winners, Has.Count.EqualTo(1), "exactly one of eight commits may take a unique key");
            Assert.That(refusals, Is.EqualTo(7), "every racer that did not win must have been refused");
        });
    }

    /// <summary>
    /// The same race against a production path that admits a key another commit has already claimed: the case above must reject it.
    /// </summary>
    /// <remarks>
    /// The mutant is the defect itself, restored — the claim is taken and its refusal dropped — so the racers go back to finding the key free, appending,
    /// and colliding in their publishes. It belongs here rather than in a reasoned argument because the alternative way to know that the case above can
    /// fail is to trust that it once did: this one shows it on every run.
    /// </remarks>
    [Test]
    [RuleMutant("AP-03")]
    public void ARaceAdmittedPastItsClaimIsDetected()
    {
        using var dbe = SetupEngine();
        dbe.UniqueKeys.AdmitClaimedKeysForTest = true;

        RuleMutants.AssertDetects("AP-03", "not caught before the append", () => AssertOneWinnerAndCatchableRefusals(dbe));
    }

    /// <summary>
    /// The engine is still committing after the race, and the winner's key is in the index: a ninth commit for the same key is refused the ordinary way,
    /// and one for a free key succeeds.
    /// </summary>
    /// <remarks>
    /// Separate from the case above because it projects a different claim. That one says the losers were refused correctly; this one says the refusal cost
    /// nothing — before the fix the engine had latched <c>CommitPipelineFailure</c> by this point and every later commit, of any key, was refused with
    /// <c>CommitPipelineStopped</c>.
    /// </remarks>
    [Test]
    [VerifiesRule("AP-03")]
    public void AfterTheRace_TheEngineStillCommitsAndTheKeyIsHeld()
    {
        using var dbe = SetupEngine();

        Race(dbe, 8);

        using (var taken = dbe.CreateQuickTransaction())
        {
            taken.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, ContendedKey, 1.0)));
            Assert.Throws<UniqueConstraintViolationException>(() => taken.Commit(),
                "the winner's key must be in the index, so a later commit for it is refused as a plain duplicate");
        }

        using var free = dbe.CreateQuickTransaction();
        free.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, ContendedKey + 1, 1.0)));
        Assert.That(free.Commit(), Is.True, "the engine must still be committing: a race for one key is not a reason to stop taking records");
    }

    /// <summary>
    /// Every claim a commit takes is given back, whether it won or was refused — so a key is not reserved for the engine's life by a commit that is over.
    /// </summary>
    /// <remarks>
    /// The leak this guards is silent and permanent: a claim left behind refuses its key to every later commit, and the symptom is a name that can never be
    /// used again with no exception anywhere to say why. Asserted against the table rather than inferred from a later commit succeeding, because a count of
    /// zero is the claim and a successful commit of some other key would not test it.
    /// </remarks>
    [Test]
    [VerifiesRule("AP-03")]
    public void EveryRacerGivesBackItsClaim()
    {
        using var dbe = SetupEngine();

        Race(dbe, 8);

        Assert.That(dbe.UniqueKeys.Count, Is.Zero, "a commit that is over still holds a unique-key claim: that key is now unusable for good");
    }

    /// <summary>
    /// A key whose claim was released by a REFUSED commit is usable afterwards: the winner's entity is destroyed and the key spawned again, in a later
    /// transaction, successfully.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    public void AKeyFreedByItsHoldersDestroy_IsClaimableAgain()
    {
        using var dbe = SetupEngine();

        EntityId holder;
        using (var tx = dbe.CreateQuickTransaction())
        {
            holder = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, ContendedKey, 1.0)));
            tx.Commit();
        }

        using (var refused = dbe.CreateQuickTransaction())
        {
            refused.Spawn<CompDArch>(CompDArch.D.Set(new CompD(2f, ContendedKey, 2.0)));
            Assert.Throws<UniqueConstraintViolationException>(() => refused.Commit(), "premise: the duplicate is refused, and its claim released with it");
        }

        using (var destroy = dbe.CreateQuickTransaction())
        {
            destroy.Destroy(holder);
            destroy.Commit();
        }

        using var reuse = dbe.CreateQuickTransaction();
        reuse.Spawn<CompDArch>(CompDArch.D.Set(new CompD(3f, ContendedKey, 3.0)));
        Assert.Multiple(() =>
        {
            Assert.That(reuse.Commit(), Is.True, "the key's holder is gone, so the key is free — unless a refused commit left its claim behind");
            Assert.That(dbe.UniqueKeys.Count, Is.Zero, "no claim outlives its commit");
        });
    }
}
