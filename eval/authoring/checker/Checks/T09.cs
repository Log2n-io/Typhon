using NUnit.Framework;

namespace EvalChecker;

[TestFixture]
public class T09
{
    [Test]
    public void CountWithin_Matches_BruteForce_Right_After_Spawn()
    {
        using var db = Solution.Open(H.NewDbPath("t09"));
        var rng = new Random(1234);
        var points = new List<(float X, float Y)>();
        for (int i = 0; i < 2000; i++)
        {
            var p = ((float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000));
            points.Add(p);
            Solution.Spawn(db, p.Item1, p.Item2);
        }

        var mismatches = new List<string>();
        for (int q = 0; q < 50; q++)
        {
            float x = (float)(rng.NextDouble() * 1000), y = (float)(rng.NextDouble() * 1000), r = 10 + (float)(rng.NextDouble() * 140);
            int expected = points.Count(p => Math.Sqrt((double)(p.X - x) * (p.X - x) + (double)(p.Y - y) * (p.Y - y)) <= r);
            int got = Solution.CountWithin(db, x, y, r);
            if (got != expected)
            {
                mismatches.Add($"({x:F1},{y:F1},r={r:F1}): {got} vs {expected}");
            }
        }
        Assert.That(mismatches, Is.Empty, string.Join("; ", mismatches.Take(5)));
    }

    [Test]
    public void Moves_Reflected()
    {
        using var db = Solution.Open(H.NewDbPath("t09"));
        var ids = new List<Typhon.Engine.EntityId>();
        for (int i = 0; i < 200; i++)
        {
            ids.Add(Solution.Spawn(db, 100 + (i % 20), 100 + (i / 20)));
        }
        Assert.That(Solution.CountWithin(db, 900, 900, 50), Is.Zero);
        for (int i = 0; i < 50; i++)
        {
            Solution.Move(db, ids[i], 900 + (i % 10), 900 + (i / 10));
        }
        Assert.That(Solution.CountWithin(db, 900, 900, 50), Is.EqualTo(50), "moved entities not found at their new position");
        Assert.That(Solution.CountWithin(db, 110, 105, 60), Is.EqualTo(150), "moved entities still found at their old position");
    }

    [Test]
    public void Incremental_Spawns_Counted()
    {
        using var db = Solution.Open(H.NewDbPath("t09"));
        for (int i = 1; i <= 20; i++)
        {
            Solution.Spawn(db, 500, 500);
            Assert.That(Solution.CountWithin(db, 500, 500, 1), Is.EqualTo(i), $"after spawn #{i}");
        }
    }
}
