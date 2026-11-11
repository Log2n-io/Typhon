using Eval;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T05
{
    [Test]
    public void Moves_And_Persists_Across_Reopen()
    {
        var path = H.NewDbPath("t05");
        var ids = new List<EntityId>();
        var before = new Dictionary<EntityId, (float X, float Y)>();
        var start = new Dictionary<EntityId, (float X, float Y)>();
        using (var db = Solution.Open(path))
        {
            using (var tx = db.CreateQuickTransaction())
            {
                for (int i = 0; i < 5000; i++)
                {
                    var id = tx.Spawn<Unit>(
                        Unit.Position.Set(new Position { X = i, Y = -i }),
                        Unit.Velocity.Set(new Velocity { X = 1 + (i % 7), Y = 2 }));
                    ids.Add(id);
                    start[id] = (i, -i);
                }
                tx.Commit();
            }
        }

        // Reopen before ticking, so the movement is the only change to the data pages since the last checkpoint.
        using (var db = Solution.Open(path))
        {
            using (var rt = Solution.CreateRuntime(db))
            {
                H.RunTicks(rt, 30);
            }

            using (var tx = db.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    var p = tx.Open(id).Read(Unit.Position);
                    before[id] = (p.X, p.Y);
                }
            }
        }

        int notMoved = ids.Count(id => before[id] == start[id]);
        Assert.That(notMoved, Is.Zero, $"{notMoved} units did not move");

        using (var db = Solution.Open(path))
        using (var tx = db.CreateQuickTransaction())
        {
            int lost = 0;
            foreach (var id in ids)
            {
                var p = tx.Open(id).Read(Unit.Position);
                if ((p.X, p.Y) != before[id])
                {
                    lost++;
                }
            }
            Assert.That(lost, Is.Zero, $"{lost} of {ids.Count} positions differ after reopen (movement not persisted)");
        }
    }
}
