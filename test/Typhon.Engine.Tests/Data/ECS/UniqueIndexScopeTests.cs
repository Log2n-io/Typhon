using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema — one declaring archetype, two descendants that inherit the unique field

/// <summary>
/// Carries a UNIQUE index (<c>AllowMultiple</c> defaults to false). Declared by <see cref="UsBase"/> only, so the scope the constraint SHOULD have —
/// <see cref="UsBase"/>'s subtree — is unambiguous.
/// </summary>
[Component("Typhon.Test.UniqScope.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct UsData
{
    [Index] public int Key;
    public long Pad;

    public UsData(int key) { Key = key; Pad = 0; }
}

/// <summary>Declares the unique field. Every archetype below inherits it — none of them re-declares it.</summary>
[Archetype]
class UsBase : Archetype<UsBase>
{
    public static readonly Comp<UsData> Data = Register<UsData>();
}

[Archetype]
class UsMonster : Archetype<UsMonster, UsBase>
{
}

[Archetype]
class UsBoss : Archetype<UsBoss, UsMonster>
{
}

#endregion

/// <summary>
/// The scope a unique <c>[Index]</c> is enforced over. See issue #678 and <c>claude/design/Indexing/index-scope-and-uniqueness.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is true today.</b> Each archetype owns its own B+Tree per indexed field, and the duplicate check is a lookup in THAT tree. So the constraint holds
/// within one archetype and nowhere else: two entities in different archetypes of the same tree may hold the same key, and a polymorphic query over the parent
/// returns both. <see cref="SameArchetype_DuplicateKey_Throws"/> is the control that runs today and proves the constraint exists at all; everything marked
/// <c>[Ignore]</c> below is the scope it SHOULD have.
/// </para>
/// <para>
/// <b>Why these are written now rather than with the fix.</b> They are the acceptance criteria for #678, and they were written while the reproduction was in
/// hand. Left unwritten, the gap gets rediscovered from scratch — it already survived a full pre-merge review, because every finding in that review asked
/// whether the index was CORRECT and none asked what the constraint on it MEANT.
/// </para>
/// <para>
/// <b>Why the fix is not "check the sibling trees before inserting".</b> That is O(K) descents per insert AND racy — the probe and the insert are not atomic,
/// so two concurrent inserts of the same key into different archetypes both pass. Uniqueness needs a single structure; the design adds a subtree-scoped hash
/// and leaves the per-archetype trees untouched.
/// </para>
/// </remarks>
[TestFixture]
class UniqueIndexScopeTests : TestBase<UniqueIndexScopeTests>
{
    private const int Key = 777;

    /// <summary>Control — runs today, and must keep running. If this ever goes red the constraint is not enforced at all, which is a different bug.</summary>
    [Test]
    public void SameArchetype_DuplicateKey_Throws()
    {
        using var dbe = SetupEngine();

        Assert.Throws<UniqueConstraintViolationException>(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<UsBase>(UsBase.Data.Set(new UsData(Key)));
            tx.Spawn<UsBase>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        });
    }

    /// <summary>Parent and child. The child inherits the field; both live in <see cref="UsBase"/>'s subtree, so the key must not repeat.</summary>
    [Test]
    [Ignore("#678 — a unique index is enforced per archetype, not across the declaring archetype's subtree. Acceptance test for the subtree hash.")]
    public void AncestorAndDescendant_DuplicateKey_Throws()
    {
        using var dbe = SetupEngine();

        Assert.Throws<UniqueConstraintViolationException>(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<UsBase>(UsBase.Data.Set(new UsData(Key)));
            tx.Spawn<UsMonster>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        });
    }

    /// <summary>Two descendants, neither an ancestor of the other — the shape a per-archetype tree cannot see across however deep it looks.</summary>
    [Test]
    [Ignore("#678 — a unique index is enforced per archetype, not across the declaring archetype's subtree. Acceptance test for the subtree hash.")]
    public void SiblingArchetypes_DuplicateKey_Throws()
    {
        using var dbe = SetupEngine();

        Assert.Throws<UniqueConstraintViolationException>(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<UsMonster>(UsBase.Data.Set(new UsData(Key)));
            tx.Spawn<UsBoss>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        });
    }

    /// <summary>
    /// Separate transactions, so the duplicate is committed against a key already durably present rather than resolved within one commit batch. A fix that
    /// only de-duplicates inside a single transaction passes the tests above and fails this one.
    /// </summary>
    [Test]
    [Ignore("#678 — a unique index is enforced per archetype, not across the declaring archetype's subtree. Acceptance test for the subtree hash.")]
    public void SeparateTransactions_DuplicateKeyAcrossArchetypes_Throws()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<UsMonster>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        }

        Assert.Throws<UniqueConstraintViolationException>(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<UsBoss>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        });
    }

    /// <summary>
    /// The reader-side consequence, and the reason this is not merely a writer's concern: a point lookup on a key declared unique returns TWO rows, which
    /// breaks the caller's model just as badly as the accepted write did.
    /// </summary>
    [Test]
    [Ignore("#678 — a unique index is enforced per archetype, not across the declaring archetype's subtree. Acceptance test for the subtree hash.")]
    public void PointLookupOnUniqueKey_ReturnsExactlyOneEntity()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<UsMonster>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        }

        // Second spawn must be rejected; if the constraint were somehow satisfied another way, the query below still pins the observable contract.
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<UsBoss>(UsBase.Data.Set(new UsData(Key)));
            tx.Commit();
        }
        catch (UniqueConstraintViolationException)
        {
            // expected once #678 is fixed
        }

        using var q = dbe.CreateQuickTransaction();
        var hits = q.Query<UsBase>().WhereField<UsData>(d => d.Key == Key).Execute().Count;
        Assert.That(hits, Is.EqualTo(1), "a key declared unique must resolve to exactly one entity across the declaring archetype's subtree");
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<UsData>();
        dbe.InitializeArchetypes();
        return dbe;
    }
}
