using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The fence's membership signal (<see cref="ArchetypeClusterState.PublishStructureChanges"/>): an archetype may publish twice in one tick — from the fence's
/// prep when it has no work, and from the head when an off-DAG destroy left a cluster to finalize meanwhile. The second drain adds to the first's words.
/// </summary>
[TestFixture]
class StructurePublishTests
{
    private static ArchetypeClusterState Tracked()
    {
        var cs = ArchetypeClusterState.CreateActiveListOnlyForTests();
        cs.ClusterStructureChanges = new DirtyBitmap(64 * 8);
        cs.TrackStructureChanges = true;
        return cs;
    }

    [Test]
    [VerifiesRule("SUB-10")]
    public void ASecondPublishOfTheSameTickAddsToTheFirst()
    {
        var cs = Tracked();
        cs.NoteStructureSlots(1, 0b1);
        cs.PublishStructureChanges(5);
        cs.NoteStructureSlots(3, 0b100);
        cs.NoteStructureSlots(1, 0b10);
        cs.PublishStructureChanges(5);
        Assert.Multiple(() =>
        {
            Assert.That(cs.StructureTick, Is.EqualTo(5));
            Assert.That(cs.StructureSlotsOf(1), Is.EqualTo(0b11UL), "the first publish's slot kept, the second's added");
            Assert.That(cs.StructureSlotsOf(3), Is.EqualTo(0b100UL));
        });
    }

    [Test]
    [VerifiesRule("SUB-10")]
    public void ANewTicksPublishReplacesTheLastsWords()
    {
        var cs = Tracked();
        cs.NoteStructureSlots(6, 0b1000);
        cs.PublishStructureChanges(9);

        // Tick 10's first publish drains fewer words than tick 9's; its second must not read tick 9's past them.
        cs.NoteStructureSlots(0, 0b1);
        cs.PublishStructureChanges(10);
        cs.NoteStructureSlots(7, 0b1);
        cs.PublishStructureChanges(10);
        Assert.Multiple(() =>
        {
            Assert.That(cs.StructureSlotsOf(6), Is.Zero, "tick 9's mark is not tick 10's");
            Assert.That(cs.StructureSlotsOf(0), Is.EqualTo(0b1UL));
            Assert.That(cs.StructureSlotsOf(7), Is.EqualTo(0b1UL));
        });
    }

    [Test]
    [VerifiesRule("SUB-10")]
    public void AQuietArchetypePublishesNothingWhenOnlyIfMarked()
    {
        var cs = Tracked();
        cs.NoteStructureSlots(2, 0b1);
        cs.PublishStructureChanges(3, onlyIfMarked: true);
        cs.PublishStructureChanges(4, onlyIfMarked: true);
        Assert.That(cs.StructureTick, Is.EqualTo(3), "nothing marked since tick 3: tick 4 skips the drain");

        cs.NoteStructureSlots(5, 0b1);
        cs.PublishStructureChanges(6, onlyIfMarked: true);
        Assert.Multiple(() =>
        {
            Assert.That(cs.StructureTick, Is.EqualTo(6));
            Assert.That(cs.StructureSlotsOf(5), Is.EqualTo(0b1UL));
            Assert.That(cs.StructureSlotsOf(2), Is.Zero);
        });
    }
}
